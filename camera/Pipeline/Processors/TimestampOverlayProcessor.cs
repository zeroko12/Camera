using OpenCvSharp;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

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

        public double FontScale { get; set; } = 0.3;
        public string CustomText { get; set; } = "";

        public void Process(Mat frame, DateTime timestamp)
        {
            if (!Enabled || frame == null || frame.Empty()) return;

            // 确保是 3 通道 BGR 图像
            if (frame.Type() != MatType.CV_8UC3) return;

            lock (_lock)
            {
                if ((timestamp - _lastTimeStringUpdate).TotalSeconds >= 1.0)
                {
                    _cachedTimeString = timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                    _lastTimeStringUpdate = timestamp;
                }
            }

            string nowStr = _cachedTimeString;

            try
            {
                // 使用直接指向 Mat 内存的零拷贝 Bitmap 包装器
                using (var bmp = new Bitmap(
                    frame.Width,
                    frame.Height,
                    (int)frame.Step(),
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb,
                    frame.Data))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        // 启用高质量抗锯齿绘制
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        g.SmoothingMode = SmoothingMode.AntiAlias;

                        // 计算根据分辨率等比缩放的字体大小
                        // 1920 宽度下基准字号为 32pt，乘以用户滑块比例 FontScale
                        float baseSize = 32f;
                        float fontSize = (float)(baseSize * FontScale * (frame.Width / 1920.0));
                        if (fontSize < 8f) fontSize = 8f; // 限制最小大小防止看不清

                        using (var font = new Font("Microsoft YaHei", fontSize, FontStyle.Bold))
                        using (var brush = new SolidBrush(Color.White))
                        using (var bgBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) // 半透明背景
                        {
                            // 1. 绘制时间戳 (右上角)
                            System.Drawing.SizeF timeSize = g.MeasureString(nowStr, font);
                            float timeX = frame.Width - timeSize.Width - 15;
                            float timeY = 15;

                            // 绘制背景半透明黑底
                            g.FillRectangle(bgBrush, timeX - 5, timeY - 2, timeSize.Width + 10, timeSize.Height + 4);
                            g.DrawString(nowStr, font, brush, timeX, timeY);

                            // 2. 绘制自定义文本 (左上角)
                            if (!string.IsNullOrEmpty(CustomText))
                            {
                                System.Drawing.SizeF customSize = g.MeasureString(CustomText, font);
                                float customX = 15;
                                float customY = 15;

                                // 绘制背景半透明黑底
                                g.FillRectangle(bgBrush, customX - 5, customY - 2, customSize.Width + 10, customSize.Height + 4);
                                g.DrawString(CustomText, font, brush, customX, customY);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"绘制水印时发生异常: {ex.Message}");
            }
        }
    }
}
