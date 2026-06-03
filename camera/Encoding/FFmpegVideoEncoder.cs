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

        public bool IsInitialized => _ffmpegProcess != null && !_ffmpegProcess.HasExited;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public double Fps { get; private set; }
        public string Codec { get; private set; } = "";
        public string OutputPath { get; private set; } = "";

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
        public bool Initialize(int width, int height, double fps, string codec, int quality, string outputPath)
        {
            if (IsInitialized)
            {
                Flush();
            }

            Width = width;
            Height = height;
            Fps = fps;
            Codec = codec;
            OutputPath = outputPath;

            try
            {
                string encoderArgs = GetEncoderArguments(codec, quality);
                string fullArgs;
                // GOP 大小 = 2秒 × fps，确保关键帧间隔合理
                // 行业标准: OBS 默认 keyint = 2s，FFmpeg 推荐 -g = fps * 2
                int gopSize = Math.Max(15, (int)(fps * 2));

                if (string.IsNullOrEmpty(outputPath))
                {
                    fullArgs = $"-y -hide_banner -f rawvideo -pix_fmt yuv420p -s {width}x{height} -r {fps} -i - {encoderArgs} -pix_fmt yuv420p -g {gopSize} -f null -";
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
                    fullArgs = $"-y -hide_banner -f rawvideo -pix_fmt yuv420p -s {width}x{height} -r {fps} -i - {encoderArgs} -pix_fmt yuv420p -g {gopSize} -movflags +faststart -an \"{outputPath}\"";
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

                Logger.LogInfo($"[FFmpegEncoder] 启动录制进程: {codec}, {width}x{height} @ {fps}fps");
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
            return codec switch
            {
                // NVIDIA NVENC — p1 预设（最快）+ 低延迟调优
                "h264_nvenc" => $"-c:v h264_nvenc -preset p1 -tune ll",
                // AMD AMF — 速度优先模式
                "h264_amf" => $"-c:v h264_amf -quality speed",
                // Intel QSV — 最快预设
                "h264_qsv" => $"-c:v h264_qsv -preset veryfast",
                // libx264 — CPU 软件编码，ultrafast + zerolatency + CRF 控制质量
                "libx264" => $"-c:v libx264 -preset ultrafast -tune zerolatency -crf {Math.Clamp(quality, 26, 31)}",
                // mpeg4 — 最终兜底方案（兼容性最好但质量/压缩率最差）
                "mpeg4" => $"-c:v mpeg4 -threads 0 -q:v {Math.Clamp(quality / 6, 3, 6)}",
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

            byte[] rentedArray = null;
            Mat yuvFrame = null;
            try
            {
                yuvFrame = Infrastructure.MatPool.Rent(frame.Width, frame.Height * 3 / 2, MatType.CV_8UC1);
                Cv2.CvtColor(frame, yuvFrame, ColorConversionCodes.BGR2YUV_I420);

                int frameSize = (int)yuvFrame.Total() * yuvFrame.ElemSize();
                
                // ArrayPool<byte> 是 .NET 标准的高频缓冲区管理方案
                // 参考: https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1
                rentedArray = ArrayPool<byte>.Shared.Rent(frameSize);
                Marshal.Copy(yuvFrame.Data, rentedArray, 0, frameSize);

                _ffmpegProcess.StandardInput.BaseStream.Write(rentedArray, 0, frameSize);
                _ffmpegProcess.StandardInput.BaseStream.Flush();

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[FFmpegEncoder] 帧写入失败: {ex.Message}");
                return false;
            }
            finally
            {
                if (rentedArray != null)
                {
                    ArrayPool<byte>.Shared.Return(rentedArray);
                }
                if (yuvFrame != null)
                {
                    Infrastructure.MatPool.Return(yuvFrame);
                }
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
            _cts?.Cancel();
            try { _errorReadTask?.Wait(500); } catch { }
            _cts?.Dispose();
            _cts = null;

            if (!string.IsNullOrEmpty(Codec))
            {
                VideoEncoderFactory.ReleaseSession(Codec);
                Codec = "";
            }

            if (_ffmpegProcess != null)
            {
                try
                {
                    if (!_ffmpegProcess.HasExited)
                    {
                        _ffmpegProcess.Kill();
                        _ffmpegProcess.WaitForExit(1000);
                    }
                }
                catch { }
                _ffmpegProcess.Dispose();
                _ffmpegProcess = null;
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
