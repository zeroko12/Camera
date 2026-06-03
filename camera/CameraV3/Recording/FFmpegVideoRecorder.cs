using OpenCvSharp;
using System;
using System.IO;
using System.Threading;
using camera.CameraV3.Abstractions;
using camera.CameraV3.Encoding;

namespace camera.CameraV3.Recording
{
    public class FFmpegVideoRecorder : IVideoRecorder
    {
        private readonly RecordingOptions _options;
        private readonly string _ffmpegPath;
        private IVideoEncoder? _encoder;
        private string? _currentOutputPath;
        private DateTime _recordingStartTime;
        private DateTime _lastSegmentTime;
        private int _frameCount;
        private int _skipInterval;
        private bool _isRecording;
        private bool _isPaused;
        private bool _isDisposed;
        private readonly object _lock = new object();

        public bool IsRecording => _isRecording;
        public bool IsPaused => _isPaused;
        public string? CurrentOutputPath => _currentOutputPath;
        public DateTime RecordingStartTime => _recordingStartTime;

        public event EventHandler? RecordingStarted;
        public event EventHandler? RecordingStopped;
        public event EventHandler? RecordingPaused;
        public event EventHandler? RecordingResumed;
        public event EventHandler<Exception>? RecordingError;

        public FFmpegVideoRecorder(RecordingOptions options, string? ffmpegPath = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _ffmpegPath = ffmpegPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            _skipInterval = Math.Max(1, (int)options.Fps / Math.Max(1, (int)options.RecordFps));
        }

        public bool StartRecording(string outputPath, int width, int height, double fps)
        {
            lock (_lock)
            {
                if (_isRecording) return false;

                try
                {
                    _currentOutputPath = outputPath;
                    _recordingStartTime = DateTime.Now;
                    _lastSegmentTime = _recordingStartTime;
                    _frameCount = 0;

                    _encoder = VideoEncoderFactory.CreateWithAutoFallback(
                        width, height, fps, outputPath, _ffmpegPath);

                    _isRecording = true;
                    _isPaused = false;

                    Logger.LogInfo($"[FFmpegRecorder] 开始录制: {outputPath}");
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
                    Logger.LogWarning($"[FFmpegRecorder] 录制帧失败: {ex.Message}");
                }
            }
        }

        private void CheckSegment(DateTime timestamp)
        {
            if (_options.SegmentDuration <= TimeSpan.Zero) return;

            var elapsed = timestamp - _lastSegmentTime;
            if (elapsed >= _options.SegmentDuration)
            {
                Logger.LogInfo($"[FFmpegRecorder] 分段时间到，创建新文件");
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
                _options.Width, _options.Height, _options.Fps, newPath, _ffmpegPath);
            _currentOutputPath = newPath;
        }

        private string GenerateOutputPath()
        {
            string fileName = _options.FileNamePattern
                .Replace("{CameraIndex}", _options.CameraIndex.ToString())
                .Replace("{Timestamp}", DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            string directory = string.IsNullOrEmpty(_options.SaveDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : _options.SaveDirectory;

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return Path.Combine(directory, fileName);
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
