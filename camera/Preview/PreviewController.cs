using OpenCvSharp;
using System;

namespace camera.Preview
{
    public class PreviewController : IPreviewController, IDisposable
    {
        private readonly ISceneChangeDetector _detector;
        private double _cameraFps = 30.0;
        private DateTime _lastPreviewTime = DateTime.MinValue;
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

            // UI 节流：基于时间戳（Wall-clock Time）进行跳帧判断，防止与上游管道形成双重节流导致的帧率减半问题
            double targetIntervalMs = 1000.0 / Math.Max(1, TargetFps);
            DateTime now = DateTime.Now;
            double elapsedMs = (now - _lastPreviewTime).TotalMilliseconds;

            // 允许 2.0ms 的极小容差来抵消操作系统的调度抖动
            if (elapsedMs < targetIntervalMs - 2.0) return;
            _lastPreviewTime = now;

            // 智能画面变化检测
            if (!_detector.DetectChange(frame)) return;

            // 满足触发条件，生成预览图像
            Mat previewFrame = null;
            try
            {
                int targetWidth = Math.Min(MaxWidth, frame.Width);
                if (!IsHighDefinition && frame.Width > targetWidth)
                {
                    double ratio = (double)targetWidth / frame.Width;
                    int newHeight = (int)(frame.Height * ratio);
                    previewFrame = Infrastructure.MatPool.Rent(targetWidth, newHeight, frame.Type());
                    Cv2.Resize(frame, previewFrame, new Size(targetWidth, newHeight), 0, 0, InterpolationFlags.Linear);
                }
                else
                {
                    previewFrame = Infrastructure.MatPool.Rent(frame.Width, frame.Height, frame.Type());
                    frame.CopyTo(previewFrame);
                }

                if (previewFrame != null)
                {
                    PreviewFrameReady?.Invoke(this, previewFrame);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[PreviewController] 生成预览帧失败", ex);
                if (previewFrame != null)
                {
                    Infrastructure.MatPool.Return(previewFrame);
                }
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
