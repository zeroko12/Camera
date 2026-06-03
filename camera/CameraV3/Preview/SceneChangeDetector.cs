using OpenCvSharp;
using System;
using camera.CameraV3.Abstractions;

namespace camera.CameraV3.Preview
{
    public class SceneChangeDetector : ISceneChangeDetector
    {
        private Mat? _lastFrame;
        private readonly object _lock = new object();

        public double Threshold { get; set; } = 15.0;
        public int DownscaleWidth { get; set; } = 320;
        public int DownscaleHeight { get; set; } = 240;

        public bool DetectChange(Mat frame)
        {
            lock (_lock)
            {
                using var smallFrame = new Mat();
                Cv2.Resize(frame, smallFrame, new Size(DownscaleWidth, DownscaleHeight), 0, 0, InterpolationFlags.Linear);

                using var grayFrame = new Mat();
                Cv2.CvtColor(smallFrame, grayFrame, ColorConversionCodes.BGR2GRAY);

                if (_lastFrame == null)
                {
                    _lastFrame = grayFrame.Clone();
                    return false;
                }

                using var diff = new Mat();
                Cv2.Absdiff(_lastFrame, grayFrame, diff);
                double mean = Cv2.Mean(diff).Val0;

                _lastFrame = grayFrame.Clone();
                return mean > Threshold;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _lastFrame?.Dispose();
                _lastFrame = null;
            }
        }
    }
}
