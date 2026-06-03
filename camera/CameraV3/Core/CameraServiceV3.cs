using OpenCvSharp;
using System;
using System.IO;
using System.Threading;
using camera.Capture;
using camera.CameraV3.Abstractions;
using camera.CameraV3.Pipeline.Processors;
using camera.CameraV3.Preview;
using camera.CameraV3.Recording;

namespace camera.CameraV3.Core
{
    public class CameraServiceV3 : IDisposable
    {
        private readonly CameraServiceOptions _options;
        private ICameraCapture? _capture;
        private readonly IFramePipeline _pipeline;
        private readonly IPreviewController _previewController;
        private readonly RecordingProcessor? _recordingProcessor;
        private IVideoRecorder? _recorder;
        private Thread? _captureThread;
        private volatile bool _isRunning;
        private Mat? _currentFrame;
        private readonly object _frameLock = new object();
        private readonly object _recordingLock = new object();

        public event EventHandler<Mat>? FrameReady;
        public event EventHandler? Started;
        public event EventHandler? Stopped;
        public event EventHandler<Exception>? Error;
        public event EventHandler? RecordingStarted;
        public event EventHandler? RecordingStopped;

        public bool IsRunning => _isRunning;
        public bool IsRecording => _recordingProcessor?.IsRecording ?? false;
        public int CameraIndex => _options.CameraIndex;
        public int Width => _capture?.Width ?? _options.Width;
        public int Height => _capture?.Height ?? _options.Height;
        public double Fps => _capture?.Fps ?? _options.Fps;
        public string? CurrentRecordingPath => _recorder?.CurrentOutputPath;

        public CameraServiceV3(
            CameraServiceOptions options,
            ICameraCapture? capture = null,
            IFramePipeline? pipeline = null,
            IPreviewController? previewController = null,
            IVideoRecorder? recorder = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _capture = capture;
            _pipeline = pipeline ?? new Pipeline.FramePipeline();
            _previewController = previewController ?? new PreviewController();
            _previewController.TargetFps = options.PreviewFps;
            _previewController.IsEnabled = options.EnablePreview;

            _recorder = recorder;
            if (_recorder == null && !string.IsNullOrEmpty(options.SaveDirectory))
            {
                var recordingOptions = new RecordingOptions
                {
                    CameraIndex = options.CameraIndex,
                    Width = options.Width,
                    Height = options.Height,
                    Fps = options.Fps,
                    RecordFps = options.RecordFps,
                    SaveDirectory = options.SaveDirectory,
                    FileNamePattern = options.FileNamePattern,
                    SegmentDuration = TimeSpan.FromMinutes(options.SegmentMinutes)
                };
                _recorder = new FFmpegVideoRecorder(recordingOptions);
            }

            if (_recorder != null)
            {
                _recordingProcessor = new RecordingProcessor { Recorder = _recorder };
                _recordingProcessor.RecordingStarted += (s, e) => RecordingStarted?.Invoke(this, e);
                _recordingProcessor.RecordingStopped += (s, e) => RecordingStopped?.Invoke(this, e);
                _pipeline.AddProcessor(_recordingProcessor);
            }
        }

        public bool Start()
        {
            if (_isRunning) return false;

            try
            {
                Logger.LogInfo("[CameraServiceV3] 正在启动...");

                _capture ??= CameraCaptureFactory.Create(
                    _options.CameraIndex,
                    _options.Width,
                    _options.Height,
                    _options.Fps);

                if (!_capture.IsOpened)
                {
                    Logger.LogError("[CameraServiceV3] 无法打开摄像头");
                    return false;
                }

                _isRunning = true;
                _captureThread = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _captureThread.Start();

                Started?.Invoke(this, EventArgs.Empty);
                Logger.LogInfo("[CameraServiceV3] 已启动");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("[CameraServiceV3] 启动失败: " + ex.Message);
                Error?.Invoke(this, ex);
                _isRunning = false;
                return false;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            Logger.LogInfo("[CameraServiceV3] 正在停止...");
            _isRunning = false;

            _captureThread?.Join(2000);

            lock (_frameLock)
            {
                _currentFrame?.Dispose();
                _currentFrame = null;
            }

            _capture?.Release();
            Stopped?.Invoke(this, EventArgs.Empty);
            Logger.LogInfo("[CameraServiceV3] 已停止");
        }

        private void CaptureLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var frame = new Mat();
                    if (_capture?.Read(frame) == true)
                    {
                        var timestamp = DateTime.Now;

                        lock (_frameLock)
                        {
                            _currentFrame?.Dispose();
                            _currentFrame = frame.Clone();
                        }

                        _pipeline.Process(frame, timestamp);
                        FrameReady?.Invoke(this, frame);

                        if (_options.EnablePreview)
                        {
                            _previewController.UpdateFrame(frame, timestamp);
                        }
                    }
                    else
                    {
                        frame.Dispose();
                        Thread.Sleep(10);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("[CameraServiceV3] 捕获循环异常: " + ex.Message);
                }
            }
        }

        public Mat? GetCurrentFrame()
        {
            lock (_frameLock)
            {
                return _currentFrame?.Clone();
            }
        }

        public IFramePipeline Pipeline => _pipeline;

        public bool StartRecording()
        {
            return StartRecording("");
        }

        public bool StartRecording(string outputPath)
        {
            lock (_recordingLock)
            {
                if (_recordingProcessor == null || _recorder == null)
                {
                    Logger.LogWarning("[CameraServiceV3] 录制器未初始化");
                    return false;
                }

                if (_recordingProcessor.IsRecording)
                {
                    Logger.LogWarning("[CameraServiceV3] 已经在录制中");
                    return false;
                }

                string path = string.IsNullOrEmpty(outputPath)
                    ? GenerateOutputPath()
                    : outputPath;

                bool success = _recordingProcessor.StartRecording(
                    path,
                    Width,
                    Height,
                    Fps);

                if (success)
                {
                    Logger.LogInfo($"[CameraServiceV3] 开始录制: {path}");
                }
                return success;
            }
        }

        public void StopRecording()
        {
            lock (_recordingLock)
            {
                if (_recordingProcessor != null && _recordingProcessor.IsRecording)
                {
                    Logger.LogInfo("[CameraServiceV3] 停止录制");
                    _recordingProcessor.StopRecording();
                }
            }
        }

        public void PauseRecording()
        {
            _recordingProcessor?.Pause();
        }

        public void ResumeRecording()
        {
            _recordingProcessor?.Resume();
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

        public void Dispose()
        {
            StopRecording();
            Stop();
            _capture?.Dispose();
            _recorder?.Dispose();
        }
    }
}
