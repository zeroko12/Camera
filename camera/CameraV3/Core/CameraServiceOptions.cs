namespace camera.CameraV3.Core
{
    public class CameraServiceOptions
    {
        public int CameraIndex { get; set; } = 0;
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public double Fps { get; set; } = 30;
        public int PreviewFps { get; set; } = 8;
        public int RecordFps { get; set; } = 10;
        public string SaveDirectory { get; set; } = "";
        public bool EnablePreview { get; set; } = true;
        public bool EnableSceneChangeDetection { get; set; } = true;
        public bool AutoStartRecording { get; set; } = false;
        public string FileNamePattern { get; set; } = "{CameraIndex}_{Timestamp:yyyyMMdd_HHmmss}.mp4";
        public int SegmentMinutes { get; set; } = 10;
    }
}
