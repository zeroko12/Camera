using OpenCvSharp;

namespace camera.CameraV3.Abstractions
{
    public interface ISceneChangeDetector
    {
        double Threshold { get; set; }
        bool DetectChange(Mat frame);
        void Reset();
    }
}
