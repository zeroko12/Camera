using OpenCvSharp;
using System.Collections.Concurrent;

namespace camera.Infrastructure
{
    public static class MatPool
    {
        private static readonly ConcurrentBag<Mat> _pool = new();
        private const int MaxPoolSize = 32;

        public static Mat Rent(int width, int height, MatType type)
        {
            if (_pool.TryTake(out var mat))
            {
                if (mat.IsDisposed)
                {
                    return new Mat(width, height, type);
                }

                mat.Create(height, width, type);
                return mat;
            }
            return new Mat(width, height, type);
        }

        public static void Return(Mat mat)
        {
            if (mat == null || mat.IsDisposed) return;

            if (_pool.Count < MaxPoolSize)
            {
                _pool.Add(mat);
            }
            else
            {
                mat.Dispose();
            }
        }
    }
}
