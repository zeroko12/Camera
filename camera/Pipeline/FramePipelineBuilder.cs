using System;

namespace camera.Pipeline
{
    public class FramePipelineBuilder
    {
        private readonly FramePipeline _pipeline = new();

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
