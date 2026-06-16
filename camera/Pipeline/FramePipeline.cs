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
        private volatile IFrameProcessor[] _cachedSortedProcessors = Array.Empty<IFrameProcessor>();

        public IReadOnlyList<IFrameProcessor> Processors => _cachedSortedProcessors;

        public IFramePipeline AddProcessor(IFrameProcessor processor)
        {
            lock (_lock)
            {
                _processors.Add(processor);
                RebuildCache();
            }
            return this;
        }

        private void RebuildCache()
        {
            _cachedSortedProcessors = _processors.OrderBy(p => p.Order).ToArray();
        }

        public void Process(Mat frame, DateTime timestamp)
        {
            var activeProcessors = _cachedSortedProcessors; // Thread-safe snapshot copy
            for (int i = 0; i < activeProcessors.Length; i++)
            {
                var processor = activeProcessors[i];
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
