using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using camera.CameraV3.Abstractions;

namespace camera.CameraV3.Pipeline
{
    public class FramePipeline : IFramePipeline
    {
        private readonly List<IFrameProcessor> _processors = new List<IFrameProcessor>();

        public IReadOnlyList<IFrameProcessor> Processors => _processors.AsReadOnly();

        public void Process(Mat frame, DateTime timestamp)
        {
            foreach (var processor in _processors.OrderBy(p => p.Order))
            {
                if (processor.Enabled)
                {
                    processor.Process(frame, timestamp);
                }
            }
        }

        public IFramePipeline AddProcessor(IFrameProcessor processor)
        {
            _processors.Add(processor);
            return this;
        }
    }
}
