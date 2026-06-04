using OpenCvSharp;
using System;

namespace camera.Recording
{
    public interface IVideoRecorder : IDisposable
    {
        bool IsRecording { get; }
        bool IsPaused { get; }
        string CurrentOutputPath { get; }
        DateTime RecordingStartTime { get; }
        string ActiveCodec { get; }

        event EventHandler RecordingStarted;
        event EventHandler RecordingStopped;
        event EventHandler RecordingPaused;
        event EventHandler RecordingResumed;
        event EventHandler<Exception> RecordingError;

        bool StartRecording(string outputPath, int width, int height, double fps, int quality);
        void StopRecording();
        void Pause();
        void Resume();
        void RecordFrame(Mat frame, DateTime timestamp);
    }
}
