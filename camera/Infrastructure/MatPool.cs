using OpenCvSharp;
using System.Collections.Concurrent;

namespace camera.Infrastructure
{
    public static class MatPool
    {
        // 规格键 (Width, Height, Type) -> 缓存队列
        private static readonly ConcurrentDictionary<(int Width, int Height, MatType Type), ConcurrentQueue<Mat>> _pools = new();
        private const int MaxPoolSizePerSpec = 8; // 每种分辨率规格最大缓存数量

        public static Mat Rent(int width, int height, MatType type)
        {
            var key = (width, height, type);
            if (_pools.TryGetValue(key, out var queue) && queue.TryDequeue(out var mat))
            {
                if (mat != null && !mat.IsDisposed)
                {
                    return mat;
                }
            }
            return new Mat(width, height, type);
        }

        public static void Return(Mat mat)
        {
            if (mat == null || mat.IsDisposed) return;

            var key = (mat.Width, mat.Height, mat.Type());
            var queue = _pools.GetOrAdd(key, _ => new ConcurrentQueue<Mat>());

            if (queue.Count < MaxPoolSizePerSpec)
            {
                queue.Enqueue(mat);
            }
            else
            {
                mat.Dispose();
            }
        }
    }
}
