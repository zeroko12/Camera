using OpenCvSharp;
using System;

namespace camera.Pipeline.Processors
{
    public class ClaheProcessor : IFrameProcessor
    {
        public string Name => "Clahe";
        public bool Enabled { get; set; } = false;
        public int Order { get; set; } = 10;

        public void Process(Mat frame, DateTime timestamp)
        {
            if (frame == null || frame.Empty()) return;

            Mat output = null;
            Mat[] channels = null;
            Mat enhancedL = null;
            Mat resizedL = null;
            Mat blendedL = null;
            CLAHE clahe = null;

            try
            {
                output = Infrastructure.MatPool.Rent(frame.Width, frame.Height, frame.Type());

                // 转换到LAB颜色空间
                Cv2.CvtColor(frame, output, ColorConversionCodes.BGR2Lab);

                // 分离通道
                channels = Cv2.Split(output);

                // 对L通道应用CLAHE
                clahe = Cv2.CreateCLAHE(3.0, new OpenCvSharp.Size(8, 8));
                enhancedL = Infrastructure.MatPool.Rent(channels[0].Width, channels[0].Height, channels[0].Type());
                clahe.Apply(channels[0], enhancedL);

                // 创建一个与原始L通道大小相同的Mat
                resizedL = Infrastructure.MatPool.Rent(channels[0].Width, channels[0].Height, channels[0].Type());
                Cv2.Resize(enhancedL, resizedL, channels[0].Size(), 0, 0, InterpolationFlags.Linear);

                // 使用双线性插值混合原始L通道和增强后的L通道
                blendedL = Infrastructure.MatPool.Rent(channels[0].Width, channels[0].Height, channels[0].Type());
                Cv2.AddWeighted(channels[0], 0.5, resizedL, 0.5, 0, blendedL);

                // 将混合后的数据拷贝回 channels[0]
                blendedL.CopyTo(channels[0]);

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
                if (resizedL != null) Infrastructure.MatPool.Return(resizedL);
                if (blendedL != null) Infrastructure.MatPool.Return(blendedL);
                clahe?.Dispose();
                if (output != null) Infrastructure.MatPool.Return(output);
            }
        }
    }
}
