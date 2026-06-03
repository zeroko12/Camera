using System;
using camera.CameraV3.Abstractions;

namespace camera.CameraV3.Pipeline
{
    public class FramePipelineBuilder
    {
        private readonly FramePipeline _pipeline = new FramePipeline();

        public static FramePipelineBuilder Create()
        {
            return new FramePipelineBuilder();
        }

        public FramePipelineBuilder AddProcessor(IFrameProcessor processor)
        {
            _pipeline.AddProcessor(processor);
            return this;
        }

        public IFramePipeline Build()
        {
            return _pipeline;
        }
    }
}
