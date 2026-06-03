using OpenCvSharp;
using System;

namespace camera.Capture
{
    public interface ICameraCapture : IDisposable
    {
        bool IsOpened { get; }
        int Index { get; }
        int Width { get; }
        int Height { get; }
        double Fps { get; }
        string ImplementationName { get; }

        bool Open(int index, int width = 0, int height = 0, double fps = 0);
        bool Read(Mat frame);
        void Release();
    }
}
