using System;

namespace camera.Recording
{
    public class RecordingOptions
    {
        public int CameraIndex { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public int RecordFps { get; set; }
        public string SaveDirectory { get; set; } = "SavedVideos";
        public string FileNamePattern { get; set; } = "{CameraIndex}_{Timestamp}.mp4";
        public TimeSpan SegmentDuration { get; set; } = TimeSpan.FromMinutes(1);
    }
}
