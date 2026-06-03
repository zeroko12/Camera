using OpenCvSharp;
using System;

namespace camera.CameraV3.Abstractions
{
    public interface IPreviewController
    {
        event EventHandler<Mat>? PreviewFrameReady;
        bool IsEnabled { get; set; }
        int TargetFps { get; set; }
        void UpdateFrame(Mat frame, DateTime timestamp);
    }
}
