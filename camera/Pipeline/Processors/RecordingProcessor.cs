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
                Recorder.RecordFrame(frame, timestamp);
            }
        }
    }
}
