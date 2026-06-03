using OpenCvSharp;
using System;
using camera.CameraV3.Abstractions;

namespace camera.CameraV3.Pipeline.Processors
{
    public class RecordingProcessor : IFrameProcessor
    {
        private IVideoRecorder? _recorder;

        public string Name => "RecordingProcessor";
        public bool Enabled { get; set; } = true;
        public int Order { get; set; } = 1000;

        public IVideoRecorder? Recorder
        {
            get => _recorder;
            set
            {
                if (_recorder != null)
                {
                    _recorder.RecordingStarted -= OnRecordingStarted;
                    _recorder.RecordingStopped -= OnRecordingStopped;
                }
                _recorder = value;
                if (_recorder != null)
                {
                    _recorder.RecordingStarted += OnRecordingStarted;
                    _recorder.RecordingStopped += OnRecordingStopped;
                }
            }
        }

        public bool IsRecording => _recorder?.IsRecording ?? false;

        public event EventHandler? RecordingStarted;
        public event EventHandler? RecordingStopped;

        public void Process(Mat frame, DateTime timestamp)
        {
            if (!Enabled || _recorder == null) return;
            _recorder.RecordFrame(frame, timestamp);
        }

        public bool StartRecording(string outputPath, int width, int height, double fps)
        {
            if (_recorder == null)
            {
                Logger.LogWarning("[RecordingProcessor] 没有设置 Recorder");
                return false;
            }
            return _recorder.StartRecording(outputPath, width, height, fps);
        }

        public void StopRecording()
        {
            _recorder?.StopRecording();
        }

        public void Pause()
        {
            _recorder?.Pause();
        }

        public void Resume()
        {
            _recorder?.Resume();
        }

        private void OnRecordingStarted(object? sender, EventArgs e)
        {
            RecordingStarted?.Invoke(this, e);
        }

        private void OnRecordingStopped(object? sender, EventArgs e)
        {
            RecordingStopped?.Invoke(this, e);
        }
    }
}
