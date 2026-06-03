using OpenCvSharp;
using System;

namespace camera.Preview
{
    public class PreviewController : IPreviewController, IDisposable
    {
        private readonly ISceneChangeDetector _detector;
        private int _frameCount;
        private double _cameraFps = 30.0;
        private readonly object _lock = new();

        public bool IsEnabled { get; set; } = true;
        public int TargetFps { get; set; } = 15;
        public int MaxWidth { get; set; } = 640;
        public bool IsHighDefinition { get; set; } = false;

        public event EventHandler<Mat> PreviewFrameReady;

        public PreviewController(ISceneChangeDetector detector = null)
        {
            _detector = detector ?? new SceneChangeDetector();
        }

        public void SetCameraFps(double fps)
        {
            lock (_lock)
            {
                _cameraFps = fps > 0 ? fps : 30.0;
            }
        }

        public void UpdateFrame(Mat frame, DateTime timestamp)
        {
            if (!IsEnabled || frame == null || frame.Empty()) return;

            // UI 节流：跳帧判断
            int skipInterval;
            lock (_lock)
            {
                skipInterval = Math.Max(1, (int)_cameraFps / Math.Max(1, TargetFps));
            }

            _frameCount++;
            if (_frameCount < skipInterval) return;
            _frameCount = 0;

            // 智能画面变化检测
            if (!_detector.DetectChange(frame)) return;

            // 满足触发条件，生成预览图像
            Mat previewFrame = null;
            try
            {
                int targetWidth = Math.Min(MaxWidth, frame.Width);
                if (!IsHighDefinition && frame.Width > targetWidth)
                {
                    previewFrame = new Mat();
                    double ratio = (double)targetWidth / frame.Width;
                    int newHeight = (int)(frame.Height * ratio);
                    Cv2.Resize(frame, previewFrame, new Size(targetWidth, newHeight), 0, 0, InterpolationFlags.Lanczos4);
                }
                else
                {
                    previewFrame = frame.Clone();
                }

                if (previewFrame != null)
                {
                    PreviewFrameReady?.Invoke(this, previewFrame);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[PreviewController] 生成预览帧失败", ex);
                previewFrame?.Dispose();
            }
        }

        public void Dispose()
        {
            if (_detector is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
