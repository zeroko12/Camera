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

                if (string.IsNullOrEmpty(outputPath))
                {
                    fullArgs = $"-y -hide_banner -f rawvideo -pix_fmt yuv420p -s {width}x{height} -r {fps} -i - {encoderArgs} -f null -";
                }
                else
                {
                    string directory = Path.GetDirectoryName(outputPath) ?? "";
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    fullArgs = $"-y -hide_banner -f rawvideo -pix_fmt yuv420p -s {width}x{height} -r {fps} -i - {encoderArgs} -an \"{outputPath}\"";
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

                Logger.LogInfo($"[FFmpegEncoder] 启动 FFmpeg: {codec}, {width}x{height} @ {fps}fps");
                _ffmpegProcess.Start();

                _cts = new CancellationTokenSource();
                _errorReadTask = Task.Run(() => ReadErrorOutputAsync(_cts.Token), _cts.Token);

                // 等待 500 毫秒以确保显卡加速正常启动（若是 QSV 会在这段时间报错）
                Thread.Sleep(500);

                if (_ffmpegProcess.HasExited)
                {
                    string stderr = "";
                    try { stderr = _ffmpegProcess.StandardError.ReadToEnd(); } catch { }
                    Logger.LogError($"[FFmpegEncoder] FFmpeg 启动后立即退出，错误信息: {stderr}");
                    return false;
                }

                Logger.LogInfo($"[FFmpegEncoder] FFmpeg 进程已成功启动 (PID: {_ffmpegProcess.Id})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[FFmpegEncoder] 初始化失败: {ex.Message}", ex);
                Cleanup();
                return false;
            }
        }

        private string GetEncoderArguments(string codec, int quality)
        {
            return codec switch
            {
                // NVIDIA NVENC - OBS 风格：最快预设 + 低延迟
                "h264_nvenc" => $"-c:v h264_nvenc -preset p1 -tune ll",
                // AMD AMF - OBS 风格：速度优先
                "h264_amf" => $"-c:v h264_amf -quality speed",
                // Intel QSV - OBS 风格：最快预设 + 低功耗模式
                "h264_qsv" => $"-c:v h264_qsv -preset veryfast -low_power true",
                // libx264 - 软件回退：ultrafast + 零延迟 + 稍高CRF降低CPU
                "libx264" => $"-c:v libx264 -preset ultrafast -tune zerolatency -crf {Math.Clamp(quality, 26, 31)}",
                // mpeg4 - 最终回退
                "mpeg4" => $"-c:v mpeg4 -threads 0 -q:v {Math.Clamp(quality / 6, 3, 6)}",
                _ => $"-c:v mpeg4 -threads 0 -q:v 4"
            };
        }

        public bool EncodeFrame(Mat frame, DateTime timestamp)
        {
            if (!IsInitialized || _ffmpegProcess == null || !_ffmpegProcess.StandardInput.BaseStream.CanWrite)
                return false;

            byte[] rentedArray = null;
            Mat yuvFrame = null;
            try
            {
                // 转换 BGR 到 YUV_I420，数据量减半，且消除 FFmpeg 色彩空间转换负载
                yuvFrame = new Mat();
                Cv2.CvtColor(frame, yuvFrame, ColorConversionCodes.BGR2YUV_I420);

                int frameSize = (int)yuvFrame.Total() * yuvFrame.ElemSize();
                
                // V2 核心优化：使用 ArrayPool 消除堆内存高频分配，极大降低 GC 卡顿
                rentedArray = ArrayPool<byte>.Shared.Rent(frameSize);
                Marshal.Copy(yuvFrame.Data, rentedArray, 0, frameSize);

                _ffmpegProcess.StandardInput.BaseStream.Write(rentedArray, 0, frameSize);
                _ffmpegProcess.StandardInput.BaseStream.Flush();

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[FFmpegEncoder] 写入 FFmpeg 帧失败: {ex.Message}");
                return false;
            }
            finally
            {
                if (rentedArray != null)
                {
                    ArrayPool<byte>.Shared.Return(rentedArray);
                }
                yuvFrame?.Dispose();
            }
        }

        public void Flush()
        {
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try
                {
                    _ffmpegProcess.StandardInput.Close();
                    _ffmpegProcess.WaitForExit(3000);
                }
                catch { }
            }
            Cleanup();
        }

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
                            bool isError = line.Contains("error") || line.Contains("Error") ||
                                           line.Contains("invalid") || line.Contains("Invalid") ||
                                           line.Contains("cannot") || line.Contains("Cannot") ||
                                           line.Contains("failed") || line.Contains("Failed");

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
