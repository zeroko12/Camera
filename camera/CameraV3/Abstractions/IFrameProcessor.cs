using OpenCvSharp;
using System;

namespace camera.CameraV3.Abstractions
{
    public interface IFrameProcessor
    {
        string Name { get; }
        bool Enabled { get; set; }
        int Order { get; set; }
        void Process(Mat frame, DateTime timestamp);
    }
}
