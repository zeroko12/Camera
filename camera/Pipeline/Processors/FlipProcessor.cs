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

        public void Process(Mat frame, DateTime timestamp)
        {
            if (!Enabled || frame == null || frame.Empty()) return;

            bool h = FlipHorizontal;
            bool v = FlipVertical;

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
