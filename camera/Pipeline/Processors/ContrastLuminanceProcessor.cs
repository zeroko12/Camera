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

        public void Process(Mat frame, DateTime timestamp)
        {
            if (Contrast != 1.0 || Luminance != 0.0)
            {
                Cv2.ConvertScaleAbs(frame, frame, Contrast, Luminance);
            }
        }
    }
}
