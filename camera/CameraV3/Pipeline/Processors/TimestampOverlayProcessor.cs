using OpenCvSharp;
using System;
using camera.CameraV3.Abstractions;

namespace camera.CameraV3.Pipeline.Processors
{
    public class TimestampOverlayProcessor : IFrameProcessor
    {
        public string Name => "TimestampOverlay";
        public bool Enabled { get; set; } = true;
        public int Order { get; set; } = 0;

        public Scalar TextColor { get; set; } = Scalar.Red;
        public double FontScale { get; set; } = 1.0;
        public int Thickness { get; set; } = 2;
        public int PositionX { get; set; } = 10;
        public int PositionY { get; set; } = 30;

        public void Process(Mat frame, DateTime timestamp)
        {
            if (!Enabled) return;

            var text = timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
            Cv2.PutText(frame, text, new Point(PositionX, PositionY),
                HersheyFonts.HersheySimplex, FontScale, TextColor, Thickness);
        }
    }
}
