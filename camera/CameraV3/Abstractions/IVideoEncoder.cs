using OpenCvSharp;
using System;

namespace camera.CameraV3.Abstractions
{
    public interface IVideoEncoder : IDisposable
    {
        bool IsInitialized { get; }
        int Width { get; }
        int Height { get; }
        double Fps { get; }
        string Codec { get; }

        event EventHandler<byte[]>? FrameEncoded;
        event EventHandler<Exception>? Error;

        bool Initialize(int width, int height, double fps, string codec, int bitrate = 5000000);
        bool EncodeFrame(Mat frame, DateTime timestamp);
        void Flush();
    }

    public class EncodingOptions
    {
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public double Fps { get; set; } = 30;
        public string Codec { get; set; } = "libx264";
        public int Bitrate { get; set; } = 5000000;
        public string Preset { get; set; } = "veryfast";
        public int Crf { get; set; } = 23;
    }
}
