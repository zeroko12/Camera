using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace camera.Pipeline
{
    public class FramePipeline : IFramePipeline
    {
        private readonly List<IFrameProcessor> _processors = new();
        private readonly object _lock = new();

        public IReadOnlyList<IFrameProcessor> Processors
        {
            get
            {
                lock (_lock)
                {
                    return _processors.OrderBy(p => p.Order).ToList();
                }
            }
        }

        public IFramePipeline AddProcessor(IFrameProcessor processor)
        {
            lock (_lock)
            {
                _processors.Add(processor);
            }
            return this;
        }

        public void Process(Mat frame, DateTime timestamp)
        {
            var activeProcessors = Processors;
            foreach (var processor in activeProcessors)
            {
                if (processor.Enabled)
                {
                    try
                    {
                        processor.Process(frame, timestamp);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"[Pipeline] 处理器 {processor.Name} 执行异常: {ex.Message}");
                    }
                }
            }
        }
    }
}
