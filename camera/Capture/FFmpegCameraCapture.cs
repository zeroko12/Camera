using OpenCvSharp;
using System;

namespace camera.Capture
{
    public class FFmpegCameraCapture : ICameraCapture
    {
        private int _index = -1;
        private int _width = 0;
        private int _height = 0;
        private double _fps = 0;
        private bool _isOpened = false;

        public bool IsOpened => _isOpened;
        public int Index => _index;
        public int Width => _width;
        public int Height => _height;
        public double Fps => _fps;
        public string ImplementationName => "FFmpeg";

        public FFmpegCameraCapture() { }

        public bool Open(int index, int width = 0, int height = 0, double fps = 0)
        {
            _index = index;
            Logger.LogInfo("[FFmpeg] 占位符实现，降级到 OpenCV");
            return false;
        }

        public bool Read(Mat frame) { return false; }
        public void Release() { _isOpened = false; }
        public void Dispose() { Release(); }
    }
}
