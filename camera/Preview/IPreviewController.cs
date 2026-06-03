using OpenCvSharp;
using System;

namespace camera.Preview
{
    public interface IPreviewController
    {
        bool IsEnabled { get; set; }
        int TargetFps { get; set; }
        int MaxWidth { get; set; }
        bool IsHighDefinition { get; set; }

        event EventHandler<Mat> PreviewFrameReady;
        void UpdateFrame(Mat frame, DateTime timestamp);
    }
}
