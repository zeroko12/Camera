using OpenCvSharp;
using System;
using camera.CameraV3.Abstractions;

namespace camera.CameraV3.Preview
{
    public class PreviewController : IPreviewController
    {
        private readonly ISceneChangeDetector _sceneChangeDetector;
        private DateTime _lastUpdateTime;
        private readonly object _lock = new object();

        public event EventHandler<Mat>? PreviewFrameReady;

        public bool IsEnabled { get; set; } = true;
        public int TargetFps { get; set; } = 8;
        public bool UseSceneChangeDetection { get; set; } = true;

        public PreviewController(ISceneChangeDetector? sceneChangeDetector = null)
        {
            _sceneChangeDetector = sceneChangeDetector ?? new SceneChangeDetector();
            _lastUpdateTime = DateTime.MinValue;
        }

        public void UpdateFrame(Mat frame, DateTime timestamp)
        {
            if (!IsEnabled) return;

            lock (_lock)
            {
                var minInterval = TimeSpan.FromMilliseconds(1000.0 / TargetFps);
                var elapsed = timestamp - _lastUpdateTime;

                if (elapsed < minInterval) return;

                bool shouldUpdate = true;
                if (UseSceneChangeDetection)
                {
                    shouldUpdate = _sceneChangeDetector.DetectChange(frame);
                }

                if (shouldUpdate)
                {
                    _lastUpdateTime = timestamp;
                    PreviewFrameReady?.Invoke(this, frame);
                }
            }
        }
    }
}
