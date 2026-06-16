using OpenCvSharp;
using System;

namespace camera.Pipeline.Processors
{
    public class ClaheProcessor : IFrameProcessor, IDisposable
    {
        public string Name => "Clahe";
        public bool Enabled { get; set; } = false;
        public int Order { get; set; } = 10;

        private CLAHE _clahe;
        private readonly object _lock = new();

        public void Process(Mat frame, DateTime timestamp)
        {
            if (frame == null || frame.Empty()) return;

            Mat output = null;
            Mat[] channels = null;
            Mat enhancedL = null;

            try
            {
                output = Infrastructure.MatPool.Rent(frame.Width, frame.Height, frame.Type());

                // 转换到LAB颜色空间
                Cv2.CvtColor(frame, output, ColorConversionCodes.BGR2Lab);

                // 分离通道
                channels = Cv2.Split(output);

                // 对L通道应用CLAHE
                if (_clahe == null)
                {
                    lock (_lock)
                    {
                        if (_clahe == null)
                        {
                            _clahe = Cv2.CreateCLAHE(3.0, new OpenCvSharp.Size(8, 8));
                        }
                    }
                }

                enhancedL = Infrastructure.MatPool.Rent(channels[0].Width, channels[0].Height, channels[0].Type());
                _clahe.Apply(channels[0], enhancedL);

                // 使用双线性插值混合原始L通道和增强后的L通道，直接原地更新到 channels[0]
                Cv2.AddWeighted(channels[0], 0.5, enhancedL, 0.5, 0, channels[0]);

                // 合并通道
                Cv2.Merge(channels, output);

                // 转换回BGR颜色空间
                Cv2.CvtColor(output, frame, ColorConversionCodes.Lab2BGR); // 直接写入原 frame
            }
            finally
            {
                // 释放所有临时 Mat 对象
                if (channels != null)
                {
                    foreach (var channel in channels)
                    {
                        channel?.Dispose();
                    }
                }
                if (enhancedL != null) Infrastructure.MatPool.Return(enhancedL);
                if (output != null) Infrastructure.MatPool.Return(output);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _clahe?.Dispose();
                _clahe = null;
            }
        }
    }
}
