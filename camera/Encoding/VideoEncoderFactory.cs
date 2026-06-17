using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace camera.Encoding
{
    /// <summary>
    /// 视频编码器工厂 — 负责编码器能力探测、会话管理和自动回退。
    /// 
    /// 行业标准参考:
    /// - OBS Studio: obs-ffmpeg/obs-ffmpeg-nvenc.c 中 nvenc_check_device() 通过试编码检测 NVENC
    /// - HandBrake: test/test.c 中 hb_video_encoder_is_supported() 通过试编码探测硬件编码器
    /// - FFmpeg 自身: avcodec_open2() 负责编码器初始化，返回错误码而非崩溃
    /// 
    /// 核心设计模式:
    /// 1. 「预探测」(Probe) — 在实际录制前，用合成输入独立测试编码器可用性
    /// 2. 「优雅降级」(Graceful Degradation) — 按 GPU硬编 → CPU软编 的优先级自动回退
    /// 3. 「会话计数」(Session Counting) — 限制消费级显卡的并发编码会话数
    /// </summary>
    public static class VideoEncoderFactory
    {
        private static readonly HashSet<string> _failedEncoders = new();
        private static readonly object _lock = new();

        // ═══════════════════════════════════════════════════════════
        // 编码器能力探测（一次性，结果缓存）
        // ═══════════════════════════════════════════════════════════
        private static bool _probeCompleted = false;

        // ═══════════════════════════════════════════════════════════
        // 显卡硬件并发会话限制（防止消费级显卡超载崩溃）
        // NVIDIA 消费级卡 (GTX/RTX) 限制 2-3 路并发 NVENC
        // AMD 消费级卡限制约 3 路 AMF
        // Intel iGPU 限制约 2 路 QSV
        // ═══════════════════════════════════════════════════════════
        private static int _nvencSessionCount = 0;
        private static int _amfSessionCount = 0;
        private static int _qsvSessionCount = 0;

        private static int _maxNvencSessions = 2;
        private static int _maxAmfSessions = 3;
        // QSV 默认值在静态构造函数中通过 CPU 自动检测覆盖，此处 4 仅为字段声明的占位保守值
        private static int _maxQsvSessions = 4;

        public static int MaxNvencSessions
        {
            get { lock (_lock) return _maxNvencSessions; }
            set { lock (_lock) _maxNvencSessions = Math.Max(1, value); }
        }
        public static int MaxAmfSessions
        {
            get { lock (_lock) return _maxAmfSessions; }
            set { lock (_lock) _maxAmfSessions = Math.Max(1, value); }
        }
        public static int MaxQsvSessions
        {
            get { lock (_lock) return _maxQsvSessions; }
            set { lock (_lock) _maxQsvSessions = Math.Max(1, value); }
        }

        static VideoEncoderFactory()
        {
            // 第一步：根据 CPU 型号自动推断 QSV 上限
            _maxQsvSessions = DetectQsvSessionLimit();

            // 第二步：App.config 显式配置可覆盖自动检测值
            // 约定：MaxQsvSessions=0 表示「使用自动检测」，正数表示强制覆盖
            TryLoadEncoderLimitsFromConfig();
        }

        private static void TryLoadEncoderLimitsFromConfig()
        {
            try
            {
                var config = System.Configuration.ConfigurationManager.AppSettings;
                if (int.TryParse(config["MaxNvencSessions"], out int nvenc) && nvenc > 0)
                    _maxNvencSessions = nvenc;
                if (int.TryParse(config["MaxAmfSessions"], out int amf) && amf > 0)
                    _maxAmfSessions = amf;
                // qsv=0 时保留自动检测结果；正数则强制覆盖
                if (int.TryParse(config["MaxQsvSessions"], out int qsv) && qsv > 0)
                    _maxQsvSessions = qsv;
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        // QSV 并发上限自动检测
        // 原理: 从注册表读取 CPU 型号字符串，按 Intel 代际规则推断
        // 参考: Intel ARK 数据库各代 Quick Sync 能力说明
        //       https://ark.intel.com (Quick Sync Video 支持矩阵)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 自动检测当前 CPU 能支持的 QSV 最大并发会话数。
        /// 通过注册表读取 CPU 品牌字符串，按 Intel 代际和产品线映射到合理上限。
        /// 若检测失败，返回保守默认值 4。
        /// </summary>
        private static int DetectQsvSessionLimit()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                string cpuName = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "";

                if (!string.IsNullOrEmpty(cpuName))
                {
                    int limit = GetQsvLimitFromCpuName(cpuName);
                    Logger.LogInfo($"[EncoderFactory] CPU: {cpuName} → QSV 并发上限自动设置为 {limit}");
                    return limit;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[EncoderFactory] CPU 型号检测失败: {ex.Message}，使用保守默认值 4");
            }
            return 4;
        }

        /// <summary>
        /// 根据 CPU 型号字符串推断 QSV 最大并发会话数。
        /// 
        /// 分类依据:
        ///   Intel Arc 独显          → 16  (专用媒体引擎，会话数远高于 iGPU)
        ///   Intel 12代+ / N100系列  → 8   (Alder/Raptor Lake，iGPU EU 大幅提升)
        ///   Intel 11代 Tiger Lake   → 8   (Iris Xe iGPU，Gen12 媒体引擎)
        ///   Intel 8-10代 UHD        → 6   (Gen9.5/Gen11 iGPU)
        ///   Intel 6-7代 HD          → 4   (Gen9/Gen9.5 iGPU，限制较多)
        ///   未知 Intel              → 4   (保守兜底)
        ///   非 Intel (AMD/其他)     → 0   (不支持 QSV，完全不用)
        /// </summary>
        private static int GetQsvLimitFromCpuName(string cpuName)
        {
            // ── AMD / 非 Intel: 不支持 QSV ──────────────────────────
            if (Regex.IsMatch(cpuName, @"\b(AMD|Ryzen|EPYC|Threadripper)\b", RegexOptions.IgnoreCase))
                return 0;

            // ── Intel Arc 独显 (通常不在 CPU 名称里，但以防万一) ─────
            if (Regex.IsMatch(cpuName, @"\bArc\b", RegexOptions.IgnoreCase))
                return 16;

            // ── Intel Alder Lake-N 系列: N100 / N95 / N200 / N305 ──
            // 特征: 型号为 N + 3位数字，与旧 N3xxx/N4xxx/N5xxx 区分
            if (Regex.IsMatch(cpuName, @"\bN[1-9]\d{2}\b"))
                return 8;

            // ── Intel Core Ultra (Meteor Lake / Arrow Lake, 14代+) ──
            if (Regex.IsMatch(cpuName, @"Core\s+Ultra", RegexOptions.IgnoreCase))
                return 8;

            // ── Intel Xeon (通常与同代 Core 相当，给保守值) ─────────
            if (Regex.IsMatch(cpuName, @"\bXeon\b", RegexOptions.IgnoreCase))
                return 6;

            // ── Intel Core i3/i5/i7/i9 — 按代际解析 ─────────────────
            // 格式示例:
            //   12代: i7-12700H, i5-1235U  → 首位数字 ≥ 1 且总位数 ≥ 4
            //   11代: i7-1165G7, i5-11400  → 首位为 1, 且第2位为1
            //   10代: i7-10510U, i5-10400  → 首位为 1, 第2位为0
            //   8-9代: i7-8750H, i5-9600K  → 首位为 8 或 9
            //   6-7代: i7-6700K, i5-7400   → 首位为 6 或 7
            var m = Regex.Match(cpuName, @"i[3579]-?(\d)(\d{3,4})");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int firstDigit))
            {
                // 4位型号，首位≥2 → 12代及以上 (如 i7-12700, i5-1235U 首位1但后3位≥200)
                // 更严格判断：取完整4位数字
                string fullNum = m.Groups[1].Value + m.Groups[2].Value;
                if (int.TryParse(fullNum, out int modelNum))
                {
                    if (modelNum >= 1200) return 8; // 12代+
                    if (modelNum >= 1100) return 8; // 11代
                    if (modelNum >= 1000) return 6; // 10代
                    if (modelNum >= 800)  return 6; // 8-9代
                    if (modelNum >= 600)  return 4; // 6-7代
                }
                // 3位型号 (老一代, 如 i7-870)
                if (firstDigit <= 5) return 4;
            }

            // ── 兜底: 未知 Intel 型号，保守返回 4 ─────────────────────
            Logger.LogInfo($"[EncoderFactory] 未能识别 CPU 代际，QSV 使用保守值 4");
            return 4;
        }

        // ═══════════════════════════════════════════════════════════
        // 编码器能力探测（行业标准: OBS / HandBrake 实现方式）
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 确保硬件编码器已完成一次性探测。首次调用 CreateWithAutoFallback 时自动执行。
        /// 使用 Double-Checked Locking 模式保证线程安全且仅执行一次。
        /// </summary>
        private static void EnsureEncodersProbed(string ffmpegPath)
        {
            if (_probeCompleted) return;

            lock (_lock)
            {
                if (_probeCompleted) return;

                Logger.LogInfo("[EncoderFactory] ════════ 开始一次性编码器能力探测 ════════");
                var stopwatch = Stopwatch.StartNew();

                // 仅探测硬件编码器。软件编码器（libx264/mpeg4）由 FFmpeg 内置，无需探测。
                var hwCodecs = new[] { "h264_nvenc", "h264_amf", "h264_qsv" };
                foreach (var codec in hwCodecs)
                {
                    bool available = ProbeEncoderAvailability(codec, ffmpegPath);
                    if (!available)
                    {
                        _failedEncoders.Add(codec);
                    }
                }

                stopwatch.Stop();
                Logger.LogInfo($"[EncoderFactory] ════════ 编码器探测完成，耗时 {stopwatch.ElapsedMilliseconds}ms ════════");
                _probeCompleted = true;
            }
        }

        /// <summary>
        /// 使用 FFmpeg 合成输入（lavfi color source）测试指定编码器是否可以正常初始化和编码。
        /// 
        /// 原理（行业标准做法）:
        /// - 使用 lavfi 的 color 滤镜生成合成黑色视频源（64x64，极小分辨率以最小化资源消耗）
        /// - 仅编码 3 帧以验证编码器可以正常工作
        /// - 输出丢弃到 null（-f null -），不产生任何临时文件
        /// - 通过 FFmpeg 的退出码（0=成功）判断编码器是否可用
        /// 
        /// 此方法与 OBS Studio 的 nvenc_check_device() 和 HandBrake 的 encoder probe 完全对等。
        /// 
        /// 相比之前的 "dummy 帧写入 + 竞态检测" 方法，此方法:
        /// 1. 不依赖进程退出时序（无竞态条件）
        /// 2. 不污染实际录制视频（无开头黑帧）
        /// 3. 探测与录制完全解耦（单一职责原则）
        /// </summary>
        private static bool ProbeEncoderAvailability(string codec, string ffmpegPath)
        {
            try
            {
                // lavfi color source: 64x64 黑色视频，0.2秒，15fps → 总共 3 帧
                // 这是能触发编码器完整初始化的最小负载
                string probeArgs = $"-y -hide_banner -loglevel error " +
                    $"-f lavfi -i color=c=black:s=64x64:d=0.2:r=15 " +
                    $"-frames:v 3 -c:v {codec} -f null -";

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = probeArgs,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                    }
                };

                process.Start();
                // 必须先读完 stderr 再 WaitForExit，否则可能死锁
                string stderr = process.StandardError.ReadToEnd();
                bool exited = process.WaitForExit(10000);

                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    Logger.LogWarning($"[EncoderFactory] 编码器 {codec} 探测超时（>10s），标记为不可用");
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    // 仅记录第一行错误信息以保持日志简洁
                    string firstError = stderr.Trim().Split('\n')[0];
                    Logger.LogInfo($"[EncoderFactory] 编码器 {codec} 探测失败 (exit={process.ExitCode}): {firstError}");
                    return false;
                }

                Logger.LogInfo($"[EncoderFactory] 编码器 {codec} 探测通过 ✓");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[EncoderFactory] 编码器 {codec} 探测异常: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 会话管理
        // ═══════════════════════════════════════════════════════════

        public static void RegisterSession(string codec)
        {
            lock (_lock)
            {
                if (codec == "h264_nvenc") _nvencSessionCount++;
                else if (codec == "h264_amf") _amfSessionCount++;
                else if (codec == "h264_qsv") _qsvSessionCount++;
                
                Logger.LogInfo($"[EncoderFactory] 会话分配 - NVENC: {_nvencSessionCount}/{_maxNvencSessions}, AMF: {_amfSessionCount}/{_maxAmfSessions}, QSV: {_qsvSessionCount}/{_maxQsvSessions}");
            }
        }

        public static void ReleaseSession(string codec)
        {
            lock (_lock)
            {
                if (codec == "h264_nvenc" && _nvencSessionCount > 0) _nvencSessionCount--;
                else if (codec == "h264_amf" && _amfSessionCount > 0) _amfSessionCount--;
                else if (codec == "h264_qsv" && _qsvSessionCount > 0) _qsvSessionCount--;
                
                Logger.LogInfo($"[EncoderFactory] 会话释放 - NVENC: {_nvencSessionCount}/{_maxNvencSessions}, AMF: {_amfSessionCount}/{_maxAmfSessions}, QSV: {_qsvSessionCount}/{_maxQsvSessions}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 编码器创建（自动回退链）
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 创建视频编码器，按照优先级自动回退:
        /// h264_nvenc → h264_amf → h264_qsv → libx264 → mpeg4
        /// 
        /// 回退策略（行业标准「优雅降级」模式）:
        /// - 硬件编码器: 受预探测结果和会话数限制过滤
        /// - 软件编码器: libx264 作为首选软件回退（质量/性能平衡最优）
        /// - mpeg4: 最终兜底（兼容性最好，但质量最差）
        /// </summary>
        public static IVideoEncoder CreateWithAutoFallback(
            int inputWidth,
            int inputHeight,
            int recordWidth,
            int recordHeight,
            double fps,
            int quality,
            string outputPath = null,
            string ffmpegPath = null,
            string customText = null,
            double fontScale = 0.3)
        {
            string resolvedFfmpegPath = ffmpegPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");

            // 首次调用时执行一次性硬件编码器探测
            EnsureEncodersProbed(resolvedFfmpegPath);

            var encodersToTry = new List<string>();

            lock (_lock)
            {
                // 硬件编码器：需通过探测 + 会话数限制
                if (_nvencSessionCount < _maxNvencSessions && !_failedEncoders.Contains("h264_nvenc"))
                    encodersToTry.Add("h264_nvenc");

                if (_amfSessionCount < _maxAmfSessions && !_failedEncoders.Contains("h264_amf"))
                    encodersToTry.Add("h264_amf");

                if (_qsvSessionCount < _maxQsvSessions && !_failedEncoders.Contains("h264_qsv"))
                    encodersToTry.Add("h264_qsv");

                // 软件编码器：无会话限制
                if (!_failedEncoders.Contains("libx264"))
                    encodersToTry.Add("libx264");

                encodersToTry.Add("mpeg4"); // 最终兜底，永不黑名单
            }

            foreach (var codec in encodersToTry)
            {
                try
                {
                    var encoder = new FFmpegVideoEncoder(resolvedFfmpegPath);
                    if (encoder.Initialize(inputWidth, inputHeight, recordWidth, recordHeight, fps, codec, quality, outputPath ?? "", customText, fontScale))
                    {
                        RegisterSession(codec);
                        return encoder;
                    }
                    else
                    {
                        encoder.Dispose();
                        lock (_lock)
                        {
                            _failedEncoders.Add(codec);
                        }
                        Logger.LogWarning($"[EncoderFactory] 编码器 {codec} 初始化失败，已加入黑名单，尝试下一个...");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[EncoderFactory] 编码器 {codec} 初始化异常: {ex.Message}，尝试下一个...");
                    lock (_lock)
                    {
                        _failedEncoders.Add(codec);
                    }
                }
            }

            throw new InvalidOperationException("所有编码器（包括 CPU 软解 mpeg4）均启动失败！");
        }

        /// <summary>
        /// 将指定编码器标记为运行时失败（如管道崩溃），加入黑名单。
        /// 与探测阶段的黑名单共享同一个集合，确保不会重复尝试已知坏掉的编码器。
        /// </summary>
        public static void MarkAsFailed(string codec)
        {
            if (string.IsNullOrEmpty(codec)) return;
            lock (_lock)
            {
                _failedEncoders.Add(codec);
            }
            Logger.LogWarning($"[EncoderFactory] 编码器 {codec} 运行时发生错误，已加入黑名单。");
        }

        /// <summary>
        /// 重置所有黑名单和探测缓存。通常在用户手动切换显卡/安装驱动后调用。
        /// </summary>
        public static void ResetFailedEncoders()
        {
            lock (_lock)
            {
                _failedEncoders.Clear();
                _probeCompleted = false;
            }
            Logger.LogInfo("[EncoderFactory] 已重置所有编码器黑名单和探测缓存");
        }
    }
}
