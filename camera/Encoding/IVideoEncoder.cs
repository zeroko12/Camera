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

        bool Initialize(int inputWidth, int inputHeight, int recordWidth, int recordHeight, double fps, string codec, int quality, string outputPath, string customText = null, double fontScale = 0.3);
        bool EncodeFrame(Mat frame, DateTime timestamp);
        void Flush();
    }
}
