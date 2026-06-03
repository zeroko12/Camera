using OpenCvSharp;
using System;

namespace camera.Encoding
{
    public interface IVideoEncoder : IDisposable
    {
        bool IsInitialized { get; }
        int Width { get; }
        int Height { get; }
        double Fps { get; }
        string Codec { get; }
        string OutputPath { get; }

        bool Initialize(int width, int height, double fps, string codec, int quality, string outputPath);
        bool EncodeFrame(Mat frame, DateTime timestamp);
        void Flush();
    }
}
