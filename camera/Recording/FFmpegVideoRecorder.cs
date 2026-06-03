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

                    // 重新计算跳帧率（如果 fps 发生改变）
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
                if (!_isRecording || _isPaused || _encoder == null) return;

                // 录制跳帧降频，减少 CPU 编码负载
                _frameCount++;
                if (_frameCount < _skipInterval) return;
                _frameCount = 0;

                try
                {
                    CheckSegment(timestamp);
                    _encoder.EncodeFrame(frame, timestamp);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[FFmpegRecorder] 录制单帧失败: {ex.Message}");
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
