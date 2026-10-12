using OpenCvSharp;
using System;
using camera.Recording;

namespace camera.Pipeline.Processors
{
    public class RecordingProcessor : IFrameProcessor
    {
        public string Name => "Recording";
        public bool Enabled { get; set; } = true;
        public int Order { get; set; } = 90; // Run before final UI changes if needed, or after overlays. Order 90 is perfect.

        public IVideoRecorder Recorder { get; set; }

        public void Process(Mat frame, DateTime timestamp)
        {
            if (Recorder != null && Recorder.IsRecording && !Recorder.IsPaused)
            {
                try
                {
                    Recorder.RecordFrame(frame, timestamp);
                }
                catch (Exception ex)
                {
                    // 录像写入异常绝对不向上传播到图像处理管线，确保后续预览控制器永远安全收到帧
                    Logger.LogWarning($"[RecordingProcessor] 录像处理器写入异常: {ex.Message}");
                }
            }
        }
    }
}
