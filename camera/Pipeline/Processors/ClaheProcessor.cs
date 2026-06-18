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
        private Mat _lChannel;

        public void Process(Mat frame, DateTime timestamp)
        {
            if (frame == null || frame.Empty()) return;

            try
            {
                // 原地 BGR→LAB 转换（消除中间 output Mat 的分配与拷贝）
                Cv2.CvtColor(frame, frame, ColorConversionCodes.BGR2Lab);

                // 对L通道应用CLAHE
                if (_clahe == null)
                {
                    lock (_lock)
                    {
                        _clahe ??= Cv2.CreateCLAHE(3.0, new OpenCvSharp.Size(8, 8));
                    }
                }

                // 确保存储 L 通道的持久缓冲区
                EnsureBuffers(frame.Width, frame.Height);

                // 提取 L 通道 (索引为 0) 到持久缓冲区，避免 Split 分割所有通道的开销
                Cv2.ExtractChannel(frame, _lChannel, 0);

                _clahe.Apply(_lChannel, _enhancedL);

                // 使用双线性插值混合原始L通道和增强后的L通道，直接原地更新到 _lChannel
                Cv2.AddWeighted(_lChannel, 0.5, _enhancedL, 0.5, 0, _lChannel);

                // 将更新后的 L 通道插入回 frame (索引为 0)，避免 Merge 合并所有通道的开销
                Cv2.InsertChannel(_lChannel, frame, 0);

                // 原地 LAB→BGR 转换
                Cv2.CvtColor(frame, frame, ColorConversionCodes.Lab2BGR);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[ClaheProcessor] 图像增强处理发生异常", ex);
            }
        }

        /// <summary>
        /// 确保存储通道的持久缓冲区尺寸匹配。
        /// 仅在首次使用或分辨率变化时重新分配。
        /// </summary>
        private void EnsureBuffers(int w, int h)
        {
            if (_enhancedL == null ||
                _enhancedL.Width != w ||
                _enhancedL.Height != h)
            {
                _enhancedL?.Dispose();
                _enhancedL = new Mat(h, w, MatType.CV_8UC1);
            }

            if (_lChannel == null ||
                _lChannel.Width != w ||
                _lChannel.Height != h)
            {
                _lChannel?.Dispose();
                _lChannel = new Mat(h, w, MatType.CV_8UC1);
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
                _lChannel?.Dispose();
                _lChannel = null;
            }
        }
    }
}
