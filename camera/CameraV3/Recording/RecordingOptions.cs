using System;

namespace camera.CameraV3.Recording
{
    public class RecordingOptions
    {
        public string SaveDirectory { get; set; } = "";
        public string FileNamePattern { get; set; } = "{CameraIndex}_{Timestamp:yyyyMMdd_HHmmss}.mp4";
        public TimeSpan SegmentDuration { get; set; } = TimeSpan.FromMinutes(10);
        public long MinDiskSpaceMB { get; set; } = 1024;
        public int CameraIndex { get; set; } = 0;
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public double Fps { get; set; } = 30;
        public double RecordFps { get; set; } = 10;
    }
}
