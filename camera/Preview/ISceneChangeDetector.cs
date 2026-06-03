using OpenCvSharp;

namespace camera.Preview
{
    public interface ISceneChangeDetector
    {
        double Threshold { get; set; }
        bool Enabled { get; set; }
        bool DetectChange(Mat frame);
        void Reset();
    }
}
