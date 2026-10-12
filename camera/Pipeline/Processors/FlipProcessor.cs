using OpenCvSharp;
using System;

namespace camera.Pipeline.Processors
{
    public class FlipProcessor : IFrameProcessor
    {
        public string Name => "Flip";
        public bool Enabled { get; set; } = true;
        public int Order { get; set; } = 25;

        public volatile bool FlipHorizontal = false;
        public volatile bool FlipVertical = false;
        public volatile bool UseGpuAcceleration = true;
        public volatile bool IsRecordingActive = false;

        public void Process(Mat frame, DateTime timestamp)
        {
            if (!Enabled || frame == null || frame.Empty()) return;

            bool h = FlipHorizontal;
            bool v = FlipVertical;
            if (!h && !v) return;

            // 2. GPU 模式：未录像时完全跳过 CPU Cv2.Flip，由 WPF Direct3D 硬件变换 (ScaleTransform) 呈现（零 CPU 开销）；
            // CPU 模式：或者录像中需将翻转保存到视频流时，强制全部由 CPU 执行 Cv2.Flip 内存矩阵翻转。
            if (UseGpuAcceleration && !IsRecordingActive)
            {
                return;
            }

            if (h && v)
            {
                Cv2.Flip(frame, frame, FlipMode.XY);
            }
            else if (h)
            {
                Cv2.Flip(frame, frame, FlipMode.Y);
            }
            else if (v)
            {
                Cv2.Flip(frame, frame, FlipMode.X);
            }
        }
    }
}
