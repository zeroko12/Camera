using OpenCvSharp;
using System;
using System.IO;
using camera.Encoding;

namespace camera.Recording
{
    public class FFmpegVideoRecorder : IVideoRecorder
    {
        private readonly RecordingOptions _options;
        private readonly string _ffmpegPath;
        private IVideoEncoder _encoder;
        private string _currentOutputPath;
        private DateTime _recordingStartTime;
        private DateTime _lastSegmentTime;
        private int _frameCount;
        private int _skipInterval;
        private bool _isRecording;
        private bool _isPaused;
        private bool _isDisposed;
        private int _currentQuality = 30;
        private readonly object _lock = new();

        // 故障自愈控制参数（与 V2 对齐）
        private int _initFailCount = 0;
        private DateTime _lastInitAttempt = DateTime.MinValue;
        private static readonly TimeSpan InitCooldown = TimeSpan.FromSeconds(10);
        private const int MaxInitRetries = 3;

        public bool IsRecording => _isRecording;
        public bool IsPaused => _isPaused;
        public string CurrentOutputPath => _currentOutputPath;
        public DateTime RecordingStartTime => _recordingStartTime;

        public event EventHandler RecordingStarted;
        public event EventHandler RecordingStopped;
        public event EventHandler RecordingPaused;
        public event EventHandler RecordingResumed;
        public event EventHandler<Exception> RecordingError;

        public FFmpegVideoRecorder(RecordingOptions options, string ffmpegPath = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _ffmpegPath = ffmpegPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            _skipInterval = Math.Max(1, (int)options.Fps / Math.Max(1, options.RecordFps));
        }

        public bool StartRecording(string outputPath, int width, int height, double fps, int quality)
        {
            lock (_lock)
            {
                if (_isRecording) return false;

                try
                {
                    _currentQuality = quality;
                    _currentOutputPath = string.IsNullOrEmpty(outputPath) ? GenerateOutputPath() : outputPath;
                    _recordingStartTime = DateTime.Now;
                    _lastSegmentTime = _recordingStartTime;
                    _frameCount = 0;

                    // 重置自愈计数器
                    _initFailCount = 0;
                    _lastInitAttempt = DateTime.MinValue;

                    // 重新计算跳帧率
                    _skipInterval = Math.Max(1, width > 0 ? (int)fps / Math.Max(1, _options.RecordFps) : 30 / 15);

                    _encoder = VideoEncoderFactory.CreateWithAutoFallback(
                        width, height, _options.RecordFps, quality, _currentOutputPath, _ffmpegPath);

                    _isRecording = true;
                    _isPaused = false;

                    Logger.LogInfo($"[FFmpegRecorder] 开始录制到文件: {_currentOutputPath}");
                    RecordingStarted?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[FFmpegRecorder] 启动录制失败: {ex.Message}", ex);
                    RecordingError?.Invoke(this, ex);
                    Cleanup();
                    return false;
                }
            }
        }

        public void StopRecording()
        {
            lock (_lock)
            {
                if (!_isRecording) return;

                Logger.LogInfo("[FFmpegRecorder] 停止录制");
                _isRecording = false;
                Cleanup();
                RecordingStopped?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                if (!_isRecording || _isPaused) return;
                _isPaused = true;
                Logger.LogInfo("[FFmpegRecorder] 暂停录制");
                RecordingPaused?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Resume()
        {
            lock (_lock)
            {
                if (!_isRecording || !_isPaused) return;
                _isPaused = false;
                Logger.LogInfo("[FFmpegRecorder] 恢复录制");
                RecordingResumed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void RecordFrame(Mat frame, DateTime timestamp)
        {
            lock (_lock)
            {
                if (!_isRecording || _isPaused) return;

                // 核心自愈逻辑：若编码进程崩溃，执行冷却退避并尝试重启拉起
                if (_encoder == null || !_encoder.IsInitialized)
                {
                    if ((DateTime.Now - _lastInitAttempt) < InitCooldown)
                    {
                        return; // 冷却时间内，静默跳过
                    }

                    if (_initFailCount >= MaxInitRetries)
                    {
                        if (_initFailCount == MaxInitRetries)
                        {
                            Logger.LogError($"[FFmpegRecorder] 编码器连续崩溃达到上限 {MaxInitRetries} 次，停止重试。");
                            _initFailCount++;
                        }
                        return;
                    }

                    _lastInitAttempt = DateTime.Now;
                    Logger.LogWarning($"[FFmpegRecorder] 检测到编码器已断开，尝试自动重新拉起 (第 {_initFailCount + 1}/{MaxInitRetries} 次)...");

                    try
                    {
                        if (_encoder != null)
                        {
                            try { _encoder.Dispose(); } catch { }
                            _encoder = null;
                        }

                        // 使用新路径重新拉起，避免损坏已有视频
                        _currentOutputPath = GenerateOutputPath();
                        _encoder = VideoEncoderFactory.CreateWithAutoFallback(
                            frame.Width, frame.Height, _options.RecordFps, _currentQuality, _currentOutputPath, _ffmpegPath);

                        if (_encoder != null && _encoder.IsInitialized)
                        {
                            _initFailCount = 0;
                            Logger.LogInfo($"[FFmpegRecorder] 编码进程重连成功: {_currentOutputPath}");
                        }
                        else
                        {
                            _initFailCount++;
                            Logger.LogError($"[FFmpegRecorder] 编码进程重连失败");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _initFailCount++;
                        Logger.LogError($"[FFmpegRecorder] 编码器自愈重建发生异常", ex);
                        return;
                    }
                }

                // 动态计算跳帧间隔以响应运行时录像帧率（RecordFps）的调节，与 V2 原理一致
                int currentSkipInterval = Math.Max(1, _options.Fps > 0 ? (int)_options.Fps / Math.Max(1, _options.RecordFps) : 30 / 15);
                _frameCount++;
                if (_frameCount < currentSkipInterval) return;
                _frameCount = 0;

                try
                {
                    CheckSegment(timestamp);
                    
                    // 尝试写入管道，若失败（管道破裂等），销毁编码器，触发下帧的自愈重启
                    bool success = _encoder.EncodeFrame(frame, timestamp);
                    if (!success)
                    {
                        Logger.LogWarning($"[FFmpegRecorder] 视频帧编码写入失败，可能是管道意外关闭。销毁编码器以触发自愈。");
                        try { _encoder.Dispose(); } catch { }
                        _encoder = null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[FFmpegRecorder] 录制单帧发生异常: {ex.Message}");
                }
            }
        }

        private void CheckSegment(DateTime timestamp)
        {
            if (_options.SegmentDuration <= TimeSpan.Zero) return;

            var elapsed = timestamp - _lastSegmentTime;
            if (elapsed >= _options.SegmentDuration)
            {
                Logger.LogInfo($"[FFmpegRecorder] 录像分段计时达标 ({_options.SegmentDuration.TotalMinutes} 分钟)，切换到新文件...");
                RotateSegment();
                _lastSegmentTime = timestamp;
            }
        }

        private void RotateSegment()
        {
            if (_encoder == null) return;

            _encoder.Flush();
            _encoder.Dispose();

            string newPath = GenerateOutputPath();
            _encoder = VideoEncoderFactory.CreateWithAutoFallback(
                _encoder.Width, _encoder.Height, _options.RecordFps, _currentQuality, newPath, _ffmpegPath);
            _currentOutputPath = newPath;
        }

        private string GenerateOutputPath()
        {
            DateTime now = DateTime.Now;
            string dateStr = now.ToString("yyyyMMdd");
            string timeStr = now.ToString("HH-mm-ss") + ".mp4";
            
            string directory = string.IsNullOrEmpty(_options.SaveDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : _options.SaveDirectory;

            // 保持 V2 精确文件结构：SaveDirectory/Index/Date/Time.mp4
            string fullDir = Path.Combine(directory, _options.CameraIndex.ToString(), dateStr);
            if (!Directory.Exists(fullDir))
            {
                Directory.CreateDirectory(fullDir);
            }

            return Path.Combine(fullDir, timeStr);
        }

        private void Cleanup()
        {
            if (_encoder != null)
            {
                try
                {
                    _encoder.Flush();
                    _encoder.Dispose();
                }
                catch { }
                _encoder = null;
            }
            _isRecording = false;
            _isPaused = false;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            StopRecording();
        }
    }
}
