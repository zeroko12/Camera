using OpenCvSharp;
using System;
using camera.Streaming;

namespace camera.Pipeline.Processors
{
    /// <summary>
    /// 局域网推流帧处理算子。
    /// 挂载在 FramePipeline 中，将处理好的最终画面送至 LanStreamManager 供局域网客户端查看。
    /// </summary>
    public class StreamingProcessor : IFrameProcessor
    {
        public string Name => "Streaming";
        public bool Enabled { get; set; } = true;
        public int Order { get; set; } = 95; // 放在 RecordingProcessor (90) 之后

        public int CameraIndex { get; set; }

        public void Process(Mat frame, DateTime timestamp)
        {
            if (Enabled)
            {
                try
                {
                    LanStreamManager.Instance.OnCameraFrame(CameraIndex, frame, timestamp);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[StreamingProcessor] 推流分发异常 (通道 {CameraIndex}): {ex.Message}");
                }
            }
        }
    }
}
