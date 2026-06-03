using OpenCvSharp;
using System;

namespace camera.Pipeline.Processors
{
    public class TimestampOverlayProcessor : IFrameProcessor
    {
        public string Name => "TimestampOverlay";
        public bool Enabled { get; set; } = true;
        public int Order { get; set; } = 100; // Time overlay should run near the end

        private string _cachedTimeString = "";
        private DateTime _lastTimeStringUpdate = DateTime.MinValue;
        private readonly object _lock = new();

        public void Process(Mat frame, DateTime timestamp)
        {
            lock (_lock)
            {
                if ((timestamp - _lastTimeStringUpdate).TotalSeconds >= 1.0)
                {
                    _cachedTimeString = timestamp.ToString();
                    _lastTimeStringUpdate = timestamp;
                }
            }

            string nowStr = _cachedTimeString;
            const double fontScale = 0.3;
            const int thickness = 1;
            const HersheyFonts font = HersheyFonts.HersheySimplex;

            OpenCvSharp.Size textSize = Cv2.GetTextSize(nowStr, font, fontScale, thickness, out _);
            var textOrg = new OpenCvSharp.Point(frame.Width - textSize.Width - 10, textSize.Height + 10);

            // 只在文字区域绘制半透明背景（ROI 操作，而非全帧 AddWeighted）
            var bgRect = new OpenCvSharp.Rect(
                textOrg.X - 5, textOrg.Y - textSize.Height - 5,
                textSize.Width + 10, textSize.Height + 10);

            // 边界裁剪
            bgRect = bgRect.Intersect(new OpenCvSharp.Rect(0, 0, frame.Width, frame.Height));
            if (bgRect.Width > 0 && bgRect.Height > 0)
            {
                using var roi = new Mat(frame, bgRect);
                roi.ConvertTo(roi, -1, 0.5, 0); // 只暗化文字背景区域
            }

            Cv2.PutText(frame, nowStr, textOrg, font, fontScale, new Scalar(255, 255, 255), thickness, LineTypes.AntiAlias);
        }
    }
}
