using System;
using System.Collections.Generic;
using camera.CameraV3.Abstractions;

namespace camera.CameraV3.Encoding
{
    public static class VideoEncoderFactory
    {
        private static readonly HashSet<string> _failedEncoders = new HashSet<string>();
        private static readonly object _lock = new object();

        public static IVideoEncoder Create(string codec = "mpeg4", string? ffmpegPath = null)
        {
            return new FFmpegVideoEncoder(ffmpegPath);
        }

        public static IVideoEncoder CreateWithAutoFallback(
            int width,
            int height,
            double fps,
            string? outputPath = null,
            string? ffmpegPath = null)
        {
            var encodersToTry = new List<string>();

            lock (_lock)
            {
                if (!_failedEncoders.Contains("h264_nvenc"))
                    encodersToTry.Add("h264_nvenc");
                if (!_failedEncoders.Contains("h264_amf"))
                    encodersToTry.Add("h264_amf");
                if (!_failedEncoders.Contains("h264_qsv"))
                    encodersToTry.Add("h264_qsv");
                encodersToTry.Add("libx264");
                encodersToTry.Add("mpeg4");
            }

            foreach (var codec in encodersToTry)
            {
                try
                {
                    var encoder = new FFmpegVideoEncoder(ffmpegPath);
                    if (encoder.Initialize(width, height, fps, codec, 5000000, outputPath ?? ""))
                    {
                        Logger.LogInfo($"[EncoderFactory] 使用编码器: {codec}");
                        return encoder;
                    }
                    else
                    {
                        encoder.Dispose();
                        lock (_lock)
                        {
                            _failedEncoders.Add(codec);
                        }
                        Logger.LogWarning($"[EncoderFactory] 编码器 {codec} 失败，尝试下一个...");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[EncoderFactory] 编码器 {codec} 初始化异常: {ex.Message}");
                    lock (_lock)
                    {
                        _failedEncoders.Add(codec);
                    }
                }
            }

            throw new InvalidOperationException("所有编码器都失败了");
        }

        public static void ResetFailedEncoders()
        {
            lock (_lock)
            {
                _failedEncoders.Clear();
            }
        }
    }
}
