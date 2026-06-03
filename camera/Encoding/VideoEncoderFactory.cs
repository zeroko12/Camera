using System;
using System.Collections.Generic;

namespace camera.Encoding
{
    public static class VideoEncoderFactory
    {
        private static readonly HashSet<string> _failedEncoders = new();
        private static readonly object _lock = new();

        // 显卡硬件并发会话限制（防止消费级显卡超载崩溃，与 V2 完全一致）
        private static int _nvencSessionCount = 0;
        private static int _amfSessionCount = 0;
        private static int _qsvSessionCount = 0;

        private static int _maxNvencSessions = 2; // NVIDIA 消费级卡默认限制 2
        private static int _maxAmfSessions = 3;   // AMD 默认 3
        private static int _maxQsvSessions = 2;   // Intel 默认 2

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

        public static IVideoEncoder CreateWithAutoFallback(
            int width,
            int height,
            double fps,
            int quality,
            string outputPath = null,
            string ffmpegPath = null)
        {
            var encodersToTry = new List<string>();

            lock (_lock)
            {
                // NVIDIA NVENC
                if (_nvencSessionCount < _maxNvencSessions && !_failedEncoders.Contains("h264_nvenc"))
                {
                    encodersToTry.Add("h264_nvenc");
                }
                // AMD AMF
                if (_amfSessionCount < _maxAmfSessions && !_failedEncoders.Contains("h264_amf"))
                {
                    encodersToTry.Add("h264_amf");
                }
                // Intel QSV
                if (_qsvSessionCount < _maxQsvSessions && !_failedEncoders.Contains("h264_qsv"))
                {
                    encodersToTry.Add("h264_qsv");
                }

                // 软件回退（无限制）
                if (!_failedEncoders.Contains("libx264"))
                {
                    encodersToTry.Add("libx264");
                }
                encodersToTry.Add("mpeg4");
            }

            foreach (var codec in encodersToTry)
            {
                try
                {
                    var encoder = new FFmpegVideoEncoder(ffmpegPath);
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
                        Logger.LogWarning($"[EncoderFactory] 编码器 {codec} 开启失败，加入黑名单，尝试下一个...");
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

        public static void ResetFailedEncoders()
        {
            lock (_lock)
            {
                _failedEncoders.Clear();
            }
        }
    }
}
