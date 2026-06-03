using OpenCvSharp;
using System;

namespace camera.Preview
{
    public class SceneChangeDetector : ISceneChangeDetector, IDisposable
    {
        private Mat _lastGrayFrame;
        private readonly object _lock = new();

        public double Threshold { get; set; } = 8.0;
        public bool Enabled { get; set; } = true;

        public bool DetectChange(Mat frame)
        {
            if (!Enabled) return true;
            if (frame == null || frame.Empty()) return false;

            lock (_lock)
            {
                Mat smallFrame = null;
                Mat grayFrame = null;
                Mat diff = null;

                try
                {
                    // 1. 创建缩小的灰度图（320x240，节省计算量和内存）
                    smallFrame = Infrastructure.MatPool.Rent(320, 240, frame.Type());
                    Cv2.Resize(frame, smallFrame, new Size(320, 240), 0, 0, InterpolationFlags.Linear);

                    grayFrame = Infrastructure.MatPool.Rent(320, 240, MatType.CV_8UC1);
                    Cv2.CvtColor(smallFrame, grayFrame, ColorConversionCodes.BGR2GRAY);

                    if (_lastGrayFrame == null || _lastGrayFrame.Empty())
                    {
                        _lastGrayFrame = grayFrame; // 接管引用
                        return true;
                    }

                    if (_lastGrayFrame.Size() != grayFrame.Size())
                    {
                        Infrastructure.MatPool.Return(_lastGrayFrame);
                        _lastGrayFrame = grayFrame; // 接管引用
                        return true;
                    }

                    // 2. 计算差分
                    diff = Infrastructure.MatPool.Rent(320, 240, MatType.CV_8UC1);
                    Cv2.Absdiff(grayFrame, _lastGrayFrame, diff);

                    Scalar meanDiff = Cv2.Mean(diff);
                    double diffValue = meanDiff.Val0;

                    if (diffValue > Threshold)
                    {
                        Infrastructure.MatPool.Return(_lastGrayFrame);
                        _lastGrayFrame = grayFrame; // 更新缓存帧
                        return true;
                    }

                    Infrastructure.MatPool.Return(grayFrame); // 未发生变化，释放当前帧到池中
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[SceneChangeDetector] 检测画面变化时发生异常: {ex.Message}");
                    if (grayFrame != null) Infrastructure.MatPool.Return(grayFrame);
                    return true;
                }
                finally
                {
                    if (smallFrame != null) Infrastructure.MatPool.Return(smallFrame);
                    if (diff != null) Infrastructure.MatPool.Return(diff);
                }
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                if (_lastGrayFrame != null)
                {
                    Infrastructure.MatPool.Return(_lastGrayFrame);
                    _lastGrayFrame = null;
                }
            }
        }

        public void Dispose()
        {
            Reset();
        }
    }
}
