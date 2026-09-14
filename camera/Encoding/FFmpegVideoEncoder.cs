using OpenCvSharp;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace camera.Encoding
{
    /// <summary>
    /// FFmpeg 子进程视频编码器 — 通过 stdin 管道发送原始帧数据，由 FFmpeg 编码为 H.264/MPEG4。
    /// 
    /// 架构说明（行业标准 FFmpeg 子进程管理模式）:
    /// ┌────────────┐    stdin (raw YUV420p)    ┌──────────────┐    file
    /// │ C# 应用程序 │ ──────────────────────── → │ ffmpeg.exe   │ ───────→ .mp4
    /// └────────────┘                           └──────────────┘
    ///                                               ↓ stderr
    ///                                          异步日志读取
    /// 
    /// 生命周期:
    /// 1. Initialize: 构建参数 → 启动进程 → 健康检查
    /// 2. EncodeFrame: BGR→YUV 转换 → 管道写入（ArrayPool 零分配）
    /// 3. Flush: 关闭 stdin → 等待 FFmpeg 完成 moov atom 重写 → Cleanup
    /// 4. Dispose: Flush → 释放所有资源
    /// 
    /// 关键设计决策:
    /// - 使用子进程而非 FFmpeg 库绑定 (libavcodec)，避免非托管内存泄漏风险和版本耦合
    /// - 输入像素格式固定为 YUV420p（在 C# 侧用 OpenCV 转换），避免 FFmpeg 内部色彩空间转换的额外开销
    /// - 编码器能力探测由 VideoEncoderFactory 在首次使用前独立完成，Initialize 仅负责启动录制进程
    /// </summary>
    public class FFmpegVideoEncoder : IVideoEncoder
    {
        private readonly string _ffmpegPath;
        private Process _ffmpegProcess;
        private CancellationTokenSource _cts;
        private Task _errorReadTask;
        private bool _isDisposed;
        private Mat _yuvBuffer;  // 持久 YUV 转换缓冲区，避免避免每帧 MatPool Rent/Return

        // ═══ HasExited 缓存：消除每帧 P/Invoke DuplicateHandle ═══
        // 原来每帧调用 Process.HasExited 都会触发一次 DuplicateHandle + GetExitCodeProcess + CloseHandle。
        // 30 fps 时每秒 30 次 kernel syscall,且大量线程争用同一进程句柄。
        // 改为每 200 ms 最多探活一次,既足够捕获崩溃退出,也避免 syscall 风暴。
        private bool _hasExitedCached;
        private long _lastHasExitedCheckTicks;
        private const long HasExitedCheckIntervalTicks = 200 * TimeSpan.TicksPerMillisecond;

        public bool IsInitialized => _ffmpegProcess != null && !GetHasExitedCached();
        public int InputWidth { get; private set; }
        public int InputHeight { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public double Fps { get; private set; }
        public string Codec { get; private set; } = "";
        public string OutputPath { get; private set; } = "";

        /// <summary>
        /// 缓存版的 HasExited。最多每 200 ms 调一次 Process.HasExited 真实探活。
        /// 在窗口期内复用上次结果,确保 EncodeFrame 热路径零 syscall。
        /// </summary>
        private bool GetHasExitedCached()
        {
            var proc = _ffmpegProcess;
            if (proc == null) return true;

            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastHasExitedCheckTicks);
            if (now - last < HasExitedCheckIntervalTicks && last != 0)
            {
                return _hasExitedCached;
            }

            // 试图抢占:把 last 设为 now,失败表示别的线程正在查,直接复用旧结果
            if (Interlocked.CompareExchange(ref _lastHasExitedCheckTicks, now, last) == last)
            {
                bool exited;
                try { exited = proc.HasExited; }
                catch { exited = true; }
                _hasExitedCached = exited;
                return exited;
            }
            return _hasExitedCached;
        }

        public FFmpegVideoEncoder(string ffmpegPath = null)
        {
            _ffmpegPath = ffmpegPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (!File.Exists(_ffmpegPath))
            {
                throw new FileNotFoundException("FFmpeg executable not found", _ffmpegPath);
            }
        }

        /// <summary>
        /// 初始化编码器：启动 FFmpeg 子进程并验证其正常运行。
        /// 
        /// 注意: 编码器的硬件可用性已在 VideoEncoderFactory.EnsureEncodersProbed() 中
        /// 通过独立的探测流程验证。此方法仅负责以指定参数启动实际录制进程，
        /// 并通过短暂的健康检查确认进程没有因参数错误等原因立即退出。
        /// </summary>
        public bool Initialize(int inputWidth, int inputHeight, int recordWidth, int recordHeight, double fps, string codec, int quality, string outputPath, string customText = null, double fontScale = 0.3)
        {
            if (IsInitialized)
            {
                Flush();
            }

            InputWidth = inputWidth;
            InputHeight = inputHeight;
            Width = recordWidth;
            Height = recordHeight;
            Fps = fps;
            Codec = codec;
            OutputPath = outputPath;

            try
            {
                string encoderArgs = GetEncoderArguments(codec, quality);
                
                // 构建缩放与水印滤镜参数
                string vfFilter = "";
                if (inputWidth != recordWidth || inputHeight != recordHeight)
                {
                    vfFilter = $"scale={recordWidth}:{recordHeight}";
                }

                // 微软雅黑与 Arial 字体备份机制，确保不同语言的 Windows 环境下均不因字体缺失而崩溃
                string fontPath = "C\\:/Windows/Fonts/msyh.ttc";
                if (!File.Exists("C:\\Windows\\Fonts\\msyh.ttc"))
                {
                    fontPath = "C\\:/Windows/Fonts/arial.ttf";
                }

                string escapedText = "";
                if (!string.IsNullOrEmpty(customText))
                {
                    escapedText = customText.Replace("'", "'\\''") + " ";
                }

                // 计算水印字体大小比例（以 0.3 为基准，默认对应 24 像素的字体大小）
                int fontSize = (int)Math.Round(24.0 * (fontScale / 0.3));
                if (fontSize < 8) fontSize = 8;

                // 注意：冒号与大括号需要双重甚至三重斜杠转义
                string drawTextFilter = $"drawtext=fontfile='{fontPath}':text='{escapedText}%{{localtime\\:%Y-%m-%d %H\\\\\\:%M\\\\\\:%S}}':x=20:y=h-40:fontsize={fontSize}:fontcolor=white";
                
                if (string.IsNullOrEmpty(vfFilter))
                {
                    vfFilter = drawTextFilter;
                }
                else
                {
                    vfFilter = $"{vfFilter},{drawTextFilter}";
                }

                string vfArgs = string.IsNullOrEmpty(vfFilter) ? "" : $"-vf \"{vfFilter}\"";

                string fullArgs;
                // GOP 大小 = 2秒 × fps，确保关键帧间隔合理
                // 行业标准: OBS 默认 keyint = 2s，FFmpeg 推荐 -g = fps * 2
                int gopSize = Math.Max(15, (int)(fps * 2));

                if (string.IsNullOrEmpty(outputPath))
                {
                    fullArgs = $"-y -hide_banner -f rawvideo -pix_fmt yuv420p -s {inputWidth}x{inputHeight} -r {fps} -i - {encoderArgs} {vfArgs} -pix_fmt yuv420p -g {gopSize} -f null -";
                }
                else
                {
                    string directory = Path.GetDirectoryName(outputPath) ?? "";
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    // -movflags +faststart: 将 moov atom 移到文件开头，支持渐进式加载和在线播放
                    // -an: 无音频流（纯视频录制）
                    fullArgs = $"-y -hide_banner -f rawvideo -pix_fmt yuv420p -s {inputWidth}x{inputHeight} -r {fps} -i - {encoderArgs} {vfArgs} -pix_fmt yuv420p -g {gopSize} -movflags +faststart -an \"{outputPath}\"";
                }

                _ffmpegProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = fullArgs,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                    }
                };

                Logger.LogInfo($"[FFmpegEncoder] 启动录制进程: {codec}, 输入规格 {inputWidth}x{inputHeight} -> 输出规格 {recordWidth}x{recordHeight} @ {fps}fps");
                _ffmpegProcess.Start();

                // 异步读取 stderr 以获取 FFmpeg 进度和错误信息
                _cts = new CancellationTokenSource();
                _errorReadTask = Task.Run(() => ReadErrorOutputAsync(_cts.Token), _cts.Token);

                // ═══ 启动健康检查 ═══
                // 编码器可用性已由 VideoEncoderFactory 预探测验证。
                // 此处仅做简短的启动健康检查（300ms），捕获以下少见但可能的失败场景:
                // - FFmpeg 参数格式错误
                // - 输出路径不可写
                // - GPU 会话数耗尽（运行时动态变化）
                Thread.Sleep(300);
                _ffmpegProcess.Refresh();
                if (_ffmpegProcess.HasExited)
                {
                    string stderr = "";
                    try { stderr = _ffmpegProcess.StandardError.ReadToEnd(); } catch { }
                    Logger.LogError($"[FFmpegEncoder] FFmpeg 启动后立即退出 (exit={_ffmpegProcess.ExitCode}): {stderr}");
                    return false;
                }

                Logger.LogInfo($"[FFmpegEncoder] 录制进程已启动 (PID: {_ffmpegProcess.Id})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[FFmpegEncoder] 初始化失败: {ex.Message}", ex);
                Cleanup();
                return false;
            }
        }

        /// <summary>
        /// 获取指定编码器的 FFmpeg 命令行参数。
        /// 
        /// 参数选择原则（参考 OBS Studio 的编码器预设）:
        /// - 硬件编码器: 速度优先，低延迟（实时录制场景）
        /// - 软件编码器: ultrafast 预设 + zerolatency 调优（最小化 CPU 负载）
        /// </summary>
        private string GetEncoderArguments(string codec, int quality)
        {
            // quality 参数含义: 值越大 → 压缩率越高 → 文件越小 → 质量越低
            // 用户界面范围通常为 20~51
            // 对 QSV global_quality: 1(最优质)~51(最差)，直接映射
            // 对 NVENC qp: 0(最优质)~51(最差)，直接映射  
            // 对 libx264 CRF: 0(最优质)~51(最差)，直接映射
            int clampedQuality = Math.Clamp(quality, 18, 51);

            return codec switch
            {
                // NVIDIA NVENC — p1 预设（最快）+ 低延迟调优 + CQP 质量控制
                "h264_nvenc" => $"-c:v h264_nvenc -preset p1 -tune ll -rc constqp -qp {clampedQuality}",
                // AMD AMF — 速度优先模式 + CQP 质量控制
                "h264_amf" => $"-c:v h264_amf -quality speed -rc cqp -qp_i {clampedQuality} -qp_p {clampedQuality}",
                // Intel QSV — 最快预设 + global_quality 控制（ICQ 模式，比 CQP 更高效）
                "h264_qsv" => $"-c:v h264_qsv -preset veryfast -global_quality {clampedQuality} -look_ahead 0",
                // libx264 — CPU 软件编码，ultrafast + zerolatency + CRF 控制质量
                "libx264" => $"-c:v libx264 -preset ultrafast -tune zerolatency -crf {clampedQuality}",
                // mpeg4 — 最终兜底方案（兼容性最好但质量/压缩率最差）
                "mpeg4" => $"-c:v mpeg4 -threads 0 -q:v {Math.Clamp(quality / 6, 3, 8)}",
                _ => $"-c:v mpeg4 -threads 0 -q:v 4"
            };
        }

        /// <summary>
        /// 编码一帧视频。
        /// 
        /// 性能关键路径 — 每秒调用 10-30 次，必须零分配:
        /// 1. BGR → YUV_I420 色彩空间转换（OpenCV，比 FFmpeg 内部转换更高效）
        /// 2. 使用 ArrayPool 租借缓冲区（避免 GC 压力）
        /// 3. 管道写入 + 强制刷新
        /// </summary>
        public bool EncodeFrame(Mat frame, DateTime timestamp)
        {
            if (!IsInitialized || _ffmpegProcess == null || !_ffmpegProcess.StandardInput.BaseStream.CanWrite)
                return false;

            try
            {
                // 持久 YUV 缓冲区：避免每帧 MatPool Rent/Return 开销
                EnsureYuvBuffer(frame.Width, frame.Height);
                Cv2.CvtColor(frame, _yuvBuffer, ColorConversionCodes.BGR2YUV_I420);

                int frameSize = (int)_yuvBuffer.Total() * _yuvBuffer.ElemSize();
                
                // 优化：利用 ReadOnlySpan 直接包装 native 指针，实现完全零拷贝写入，消除托管堆拷贝开销
                unsafe
                {
                    var span = new ReadOnlySpan<byte>((void*)_yuvBuffer.Data, frameSize);
                    _ffmpegProcess.StandardInput.BaseStream.Write(span);
                }
                _ffmpegProcess.StandardInput.BaseStream.Flush();

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[FFmpegEncoder] 帧写入失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 确保持久 YUV 缓冲区与当前帧尺寸匹配。
        /// 仅在首次使用或分辨率变化时重新分配。
        /// </summary>
        private void EnsureYuvBuffer(int w, int h)
        {
            int yuvH = h * 3 / 2;
            if (_yuvBuffer == null || _yuvBuffer.Width != w || _yuvBuffer.Height != yuvH)
            {
                _yuvBuffer?.Dispose();
                _yuvBuffer = new Mat(yuvH, w, MatType.CV_8UC1);
            }
        }

        /// <summary>
        /// 正常结束编码 — 关闭 stdin 管道并等待 FFmpeg 完成文件收尾。
        /// 
        /// 行业标准的 FFmpeg 子进程优雅关闭流程:
        /// 1. 关闭 stdin → FFmpeg 收到 EOF → 开始刷新内部缓冲区
        /// 2. FFmpeg 写入剩余帧 → 写入文件尾部元数据
        /// 3. 若使用 -movflags +faststart，FFmpeg 还需重新读取整个文件并重写 moov atom
        /// 4. 进程退出
        /// 
        /// 超时时间设为 8 秒，足以覆盖 1 分钟 1080p 视频的 moov atom 重写。
        /// 若超时则强制终止（视频仍可播放，但 moov atom 在文件末尾，不支持渐进式加载）。
        /// </summary>
        public void Flush()
        {
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try
                {
                    Logger.LogInfo($"[FFmpegEncoder] 正在关闭编码管道 (PID: {_ffmpegProcess.Id})...");
                    _ffmpegProcess.StandardInput.Close();
                    if (!_ffmpegProcess.WaitForExit(8000))
                    {
                        Logger.LogWarning($"[FFmpegEncoder] FFmpeg 进程未在 8 秒内退出，将强制终止");
                    }
                    else
                    {
                        Logger.LogInfo($"[FFmpegEncoder] FFmpeg 进程已正常退出 (exit={_ffmpegProcess.ExitCode})");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[FFmpegEncoder] 关闭编码管道异常: {ex.Message}");
                }
            }
            Cleanup();
        }

        /// <summary>
        /// 异步读取 FFmpeg 的 stderr 输出。
        /// FFmpeg 将所有日志（包括进度、警告、错误）输出到 stderr。
        /// 仅记录前 20 行和所有错误行，避免日志爆炸。
        /// </summary>
        private async Task ReadErrorOutputAsync(CancellationToken token)
        {
            try
            {
                int lineCount = 0;
                using (var reader = _ffmpegProcess?.StandardError)
                {
                    if (reader == null) return;
                    string line;
                    while ((line = await reader.ReadLineAsync()) != null && !token.IsCancellationRequested)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            bool isError = line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                           line.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                                           line.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
                                           line.Contains("failed", StringComparison.OrdinalIgnoreCase);

                            if (isError || lineCount < 20)
                            {
                                Logger.LogInfo($"[FFmpeg] {line}");
                                lineCount++;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 强制清理所有资源：取消异步任务、释放会话计数、终止并释放进程。
        /// </summary>
        private void Cleanup()
        {
            // ★ Fix P0-4: 严格顺序释放,消除 finalizer 队列堆积
            // 1. 停止 stderr reader task
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
            }
            if (_errorReadTask != null)
            {
                try
                {
                    if (!_errorReadTask.Wait(2000))
                    {
                        Logger.LogWarning("[FFmpegEncoder] stderr reader 未在 2s 内退出,后台继续");
                    }
                }
                catch (AggregateException) { }
                catch { }
                _errorReadTask = null;
            }
            if (_cts != null)
            {
                try { _cts.Dispose(); } catch { }
                _cts = null;
            }

            // 2. 释放会话计数
            if (!string.IsNullOrEmpty(Codec))
            {
                VideoEncoderFactory.ReleaseSession(Codec);
                Codec = "";
            }

            // 3. YUV 缓冲(必须在 Process 之前)
            if (_yuvBuffer != null)
            {
                try { _yuvBuffer.Dispose(); } catch { }
                _yuvBuffer = null;
            }

            // 4. Process 严格顺序
            if (_ffmpegProcess != null)
            {
                var proc = _ffmpegProcess;
                _ffmpegProcess = null;  // 先置 null,任何后续异常都不会让外部访问到
                try
                {
                    if (!proc.HasExited)
                    {
                        try { proc.Kill(); } catch { }
                        // libx264 软编码 moov atom 重写最坏情况 5-10 秒
                        try { if (!proc.WaitForExit(5000)) Logger.LogWarning("[FFmpegEncoder] FFmpeg 5s 内未退出,继续释放"); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[FFmpegEncoder] 终止 FFmpeg 进程异常: {ex.Message}");
                }
                try { proc.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Flush();
        }
    }
}
