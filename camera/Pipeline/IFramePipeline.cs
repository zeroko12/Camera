using OpenCvSharp;
using System;
using System.Collections.Generic;

namespace camera.Pipeline
{
    public interface IFramePipeline
    {
        IReadOnlyList<IFrameProcessor> Processors { get; }
        void Process(Mat frame, DateTime timestamp);
        IFramePipeline AddProcessor(IFrameProcessor processor);
    }
}
