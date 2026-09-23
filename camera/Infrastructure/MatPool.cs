using OpenCvSharp;
using System.Collections.Concurrent;
using System.Threading;

namespace camera.Infrastructure
{
    public static class MatPool
    {
        private class PoolQueue
        {
            public readonly ConcurrentQueue<Mat> Queue = new();
            private int _count = 0;

            public int Count => Volatile.Read(ref _count);

            public void Enqueue(Mat mat)
            {
                Queue.Enqueue(mat);
                Interlocked.Increment(ref _count);
            }

            public bool TryDequeue(out Mat mat)
            {
                if (Queue.TryDequeue(out mat))
                {
                    Interlocked.Decrement(ref _count);
                    return true;
                }
                return false;
            }
        }

        // 规格键 (Width, Height, Type) -> 缓存队列包装类
        private static readonly ConcurrentDictionary<(int Width, int Height, MatType Type), PoolQueue> _pools = new();
        private const int MaxPoolSizePerSpec = 8; // 每种分辨率规格最大缓存数量

        public static Mat Rent(int width, int height, MatType type)
        {
            var key = (width, height, type);
            if (_pools.TryGetValue(key, out var pool))
            {
                while (pool.TryDequeue(out var mat))
                {
                    if (mat != null && !mat.IsDisposed)
                    {
                        return mat;
                    }
                }
            }
            return new Mat(rows: height, cols: width, type);
        }

        public static void Return(Mat mat)
        {
            if (mat == null || mat.IsDisposed) return;

            var key = (mat.Width, mat.Height, mat.Type());
            var pool = _pools.GetOrAdd(key, _ => new PoolQueue());

            if (pool.Count < MaxPoolSizePerSpec)
            {
                pool.Enqueue(mat);
            }
            else
            {
                try { mat.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// 排空并释放池中所有缓存的 Mat 原生对象（在应用退出或全局重置时调用）
        /// </summary>
        public static void Clear()
        {
            foreach (var kvp in _pools)
            {
                var queue = kvp.Value;
                while (queue.TryDequeue(out var mat))
                {
                    try
                    {
                        if (mat != null && !mat.IsDisposed)
                        {
                            mat.Dispose();
                        }
                    }
                    catch { }
                }
            }
            _pools.Clear();
        }
    }
}
