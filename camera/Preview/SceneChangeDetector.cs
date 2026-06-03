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
                try
                {
                    // 1. 创建缩小的灰度图（320x240，节省计算量和内存）
                    using var smallFrame = new Mat();
                    Cv2.Resize(frame, smallFrame, new Size(320, 240), 0, 0, InterpolationFlags.Linear);

                    var grayFrame = new Mat();
                    Cv2.CvtColor(smallFrame, grayFrame, ColorConversionCodes.BGR2GRAY);

                    if (_lastGrayFrame == null || _lastGrayFrame.Empty())
                    {
                        _lastGrayFrame = grayFrame; // 接管引用
                        return true;
                    }

                    if (_lastGrayFrame.Size() != grayFrame.Size())
                    {
                        _lastGrayFrame.Dispose();
                        _lastGrayFrame = grayFrame; // 接管引用
                        return true;
                    }

                    // 2. 计算差分
                    using var diff = new Mat();
                    Cv2.Absdiff(grayFrame, _lastGrayFrame, diff);

                    Scalar meanDiff = Cv2.Mean(diff);
                    double diffValue = meanDiff.Val0;

                    if (diffValue > Threshold)
                    {
                        _lastGrayFrame.Dispose();
                        _lastGrayFrame = grayFrame; // 更新缓存帧
                        return true;
                    }

                    grayFrame.Dispose(); // 未发生变化，释放当前帧
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[SceneChangeDetector] 检测画面变化时发生异常: {ex.Message}");
                    return true;
                }
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _lastGrayFrame?.Dispose();
                _lastGrayFrame = null;
            }
        }

        public void Dispose()
        {
            Reset();
        }
    }
}
