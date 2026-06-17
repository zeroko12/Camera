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

        // ═══ 持久缓冲区：避免每帧从 MatPool 租借/归还 ═══
        private Mat _enhancedL;

        public void Process(Mat frame, DateTime timestamp)
        {
            if (frame == null || frame.Empty()) return;

            Mat[] channels = null;

            try
            {
                // 原地 BGR→LAB 转换（消除中间 output Mat 的分配与拷贝）
                Cv2.CvtColor(frame, frame, ColorConversionCodes.BGR2Lab);

                // 分离通道（Split 内部分配，需每帧 Dispose）
                channels = Cv2.Split(frame);

                // 对L通道应用CLAHE
                if (_clahe == null)
                {
                    lock (_lock)
                    {
                        _clahe ??= Cv2.CreateCLAHE(3.0, new OpenCvSharp.Size(8, 8));
                    }
                }

                // 持久 enhancedL 缓冲区：仅在尺寸不匹配时重新分配
                EnsureEnhancedLBuffer(channels[0]);
                _clahe.Apply(channels[0], _enhancedL);

                // 使用双线性插值混合原始L通道和增强后的L通道，直接原地更新到 channels[0]
                Cv2.AddWeighted(channels[0], 0.5, _enhancedL, 0.5, 0, channels[0]);

                // 合并通道并原地 LAB→BGR 转换
                Cv2.Merge(channels, frame);
                Cv2.CvtColor(frame, frame, ColorConversionCodes.Lab2BGR);
            }
            finally
            {
                // 释放 Split 分配的通道 Mat 对象
                if (channels != null)
                {
                    foreach (var channel in channels)
                    {
                        channel?.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// 确保持久 enhancedL 缓冲区与 L 通道尺寸匹配。
        /// 仅在首次使用或分辨率变化时重新分配。
        /// </summary>
        private void EnsureEnhancedLBuffer(Mat lChannel)
        {
            if (_enhancedL == null ||
                _enhancedL.Width != lChannel.Width ||
                _enhancedL.Height != lChannel.Height ||
                _enhancedL.Type() != lChannel.Type())
            {
                _enhancedL?.Dispose();
                _enhancedL = new Mat(lChannel.Height, lChannel.Width, lChannel.Type());
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _clahe?.Dispose();
                _clahe = null;
                _enhancedL?.Dispose();
                _enhancedL = null;
            }
        }
    }
}
