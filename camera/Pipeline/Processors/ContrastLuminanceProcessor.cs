using OpenCvSharp;
using System;

namespace camera.Pipeline.Processors
{
    public class ContrastLuminanceProcessor : IFrameProcessor
    {
        public string Name => "ContrastLuminance";
        public bool Enabled { get; set; } = true;
        public int Order { get; set; } = 20;

        public double Contrast { get; set; } = 1.0;
        public double Luminance { get; set; } = 0.0;
        public bool UseGpuAcceleration { get; set; } = true;
        public volatile bool IsRecordingActive = false;

        public void Process(Mat frame, DateTime timestamp)
        {
            if (!Enabled || frame == null || frame.Empty()) return;

            if (Contrast == 1.0 && Luminance == 0.0) return;

            // GPU 模式下当未处于录制状态时，亮度和对比度完全交由 UI 层的 Direct3D HLSL 像素着色器硬件呈现（零 CPU 开销）；
            // CPU 模式（仅使用 CPU）或录制中需固化至录像文件时，由 CPU 执行 Cv2.ConvertScaleAbs 内存变换。
            if (UseGpuAcceleration && !IsRecordingActive)
            {
                return;
            }

            Cv2.ConvertScaleAbs(frame, frame, Contrast, Luminance);
        }
    }
}
