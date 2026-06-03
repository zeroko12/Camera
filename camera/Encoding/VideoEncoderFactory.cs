using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

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
        private static int _maxQsvSessions = 2;

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
            TryLoadEncoderLimitsFromConfig();
        }

        private static void TryLoadEncoderLimitsFromConfig()
        {
            try
            {
                var config = System.Configuration.ConfigurationManager.AppSettings;
                if (int.TryParse(config["MaxNvencSessions"], out int nvenc))
                    _maxNvencSessions = Math.Max(1, nvenc);
                if (int.TryParse(config["MaxAmfSessions"], out int amf))
                    _maxAmfSessions = Math.Max(1, amf);
                if (int.TryParse(config["MaxQsvSessions"], out int qsv))
                    _maxQsvSessions = Math.Max(1, qsv);
            }
            catch { }
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
            int width,
            int height,
            double fps,
            int quality,
            string outputPath = null,
            string ffmpegPath = null)
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
                    if (encoder.Initialize(width, height, fps, codec, quality, outputPath ?? ""))
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
