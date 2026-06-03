using OpenCvSharp;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using camera.CameraV3.Abstractions;

namespace camera.CameraV3.Encoding
{
    public class FFmpegVideoEncoder : IVideoEncoder
    {
        private readonly string _ffmpegPath;
        private Process? _ffmpegProcess;
        private CancellationTokenSource? _cts;
        private Task? _errorReadTask;
        private bool _isDisposed;

        public bool IsInitialized => _ffmpegProcess != null && !_ffmpegProcess.HasExited;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public double Fps { get; private set; }
        public string Codec { get; private set; } = "";
        public string OutputPath { get; private set; } = "";

        public event EventHandler<byte[]>? FrameEncoded;
        public event EventHandler<Exception>? Error;

        public FFmpegVideoEncoder(string? ffmpegPath = null)
        {
            _ffmpegPath = ffmpegPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (!File.Exists(_ffmpegPath))
            {
                throw new FileNotFoundException("FFmpeg executable not found", _ffmpegPath);
            }
        }

        public bool Initialize(int width, int height, double fps, string codec, int bitrate = 5000000)
        {
            return Initialize(width, height, fps, codec, bitrate, "");
        }

        public bool Initialize(int width, int height, double fps, string codec, int bitrate, string outputPath)
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
                string encoderArgs = GetEncoderArguments(codec, bitrate);
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

                Thread.Sleep(300);

                if (_ffmpegProcess.HasExited)
                {
                    string stderr = "";
                    try { stderr = _ffmpegProcess.StandardError.ReadToEnd(); } catch { }
                    Logger.LogError($"[FFmpegEncoder] FFmpeg 启动失败: {stderr}");
                    return false;
                }

                Logger.LogInfo($"[FFmpegEncoder] FFmpeg 启动成功 (PID: {_ffmpegProcess.Id})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[FFmpegEncoder] 初始化失败: {ex.Message}", ex);
                Error?.Invoke(this, ex);
                Cleanup();
                return false;
            }
        }

        private string GetEncoderArguments(string codec, int bitrate)
        {
            return codec switch
            {
                "h264_nvenc" => $"-c:v h264_nvenc -preset p1 -tune ll",
                "h264_amf" => $"-c:v h264_amf -quality speed",
                "h264_qsv" => $"-c:v h264_qsv -preset veryfast -low_power true",
                "libx264" => $"-c:v libx264 -preset ultrafast -tune zerolatency -crf 28",
                "mpeg4" => $"-c:v mpeg4 -threads 0 -q:v 4",
                _ => $"-c:v mpeg4 -threads 0 -q:v 4"
            };
        }

        public bool EncodeFrame(Mat frame, DateTime timestamp)
        {
            if (!IsInitialized || _ffmpegProcess == null)
                return false;

            try
            {
                Mat yuvFrame = new Mat();
                if (frame.Channels() == 3)
                {
                    Cv2.CvtColor(frame, yuvFrame, ColorConversionCodes.BGR2YUV_I420);
                }
                else
                {
                    yuvFrame = frame.Clone();
                }

                int dataSize = Width * Height * 3 / 2;
                byte[] buffer = new byte[dataSize];
                System.Runtime.InteropServices.Marshal.Copy(yuvFrame.Data, buffer, 0, dataSize);

                _ffmpegProcess.StandardInput.BaseStream.Write(buffer, 0, buffer.Length);
                _ffmpegProcess.StandardInput.BaseStream.Flush();

                yuvFrame.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[FFmpegEncoder] 编码帧失败: {ex.Message}");
                return false;
            }
        }

        public void Flush()
        {
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try
                {
                    _ffmpegProcess.StandardInput.Close();
                    _ffmpegProcess.WaitForExit(2000);
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
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null && !token.IsCancellationRequested)
                    {
                        if (!string.IsNullOrWhiteSpace(line) && lineCount < 20)
                        {
                            Logger.LogInfo($"[FFmpeg] {line}");
                            lineCount++;
                        }
                    }
                }
            }
            catch { }
        }

        private void Cleanup()
        {
            _cts?.Cancel();
            _errorReadTask?.Wait(500);
            _cts?.Dispose();
            _cts = null;

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
