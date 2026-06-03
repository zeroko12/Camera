using OpenCvSharp;
using System;

namespace camera.Capture
{
    public enum CaptureFormat { MJPEG, YUY2, NV12 }

    public class OpenCvCameraCapture : ICameraCapture
    {
        private VideoCapture _cap;
        private int _index = -1;
        private int _width = 0;
        private int _height = 0;
        private double _fps = 0;
        private bool _isOpened = false;
        private CaptureFormat _preferredFormat = CaptureFormat.MJPEG;

        public bool IsOpened => _isOpened;
        public int Index => _index;
        public int Width => _width;
        public int Height => _height;
        public double Fps => _fps;
        public string ImplementationName => "OpenCV";

        public OpenCvCameraCapture()
        {
        }

        public bool Open(int index, int width = 0, int height = 0, double fps = 0)
        {
            try
            {
                _index = index;
                _width = width > 0 ? width : 1280;
                _height = height > 0 ? height : 720;
                _fps = fps > 0 ? fps : 30;

                Logger.LogInfo("[OpenCV] 正在打开摄像头索引 " + index);

                _cap = new VideoCapture(index);
                if (!_cap.IsOpened())
                {
                    Logger.LogWarning("[OpenCV] 无法打开摄像头 " + index);
                    return false;
                }

                SetOptimalFormat();

                _cap.Set(VideoCaptureProperties.FrameWidth, _width);
                _cap.Set(VideoCaptureProperties.FrameHeight, _height);
                _cap.Set(VideoCaptureProperties.Fps, _fps);

                _width = (int)_cap.Get(VideoCaptureProperties.FrameWidth);
                _height = (int)_cap.Get(VideoCaptureProperties.FrameHeight);
                _fps = _cap.Get(VideoCaptureProperties.Fps);

                _isOpened = true;
                Logger.LogInfo($"[OpenCV] 摄像头 {index} 已打开: {_width}x{_height} @ {_fps}fps");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[OpenCV] 初始化失败: " + ex.Message);
                Release();
                return false;
            }
        }

        private void SetOptimalFormat()
        {
            try
            {
                _cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('Y', 'U', 'Y', '2'));
                _preferredFormat = CaptureFormat.YUY2;
                Logger.LogInfo("[OpenCV] 尝试 YUY2 格式");
            }
            catch
            {
                try
                {
                    _cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('N', 'V', '1', '2'));
                    _preferredFormat = CaptureFormat.NV12;
                    Logger.LogInfo("[OpenCV] 尝试 NV12 格式");
                }
                catch
                {
                    _cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                    _preferredFormat = CaptureFormat.MJPEG;
                    Logger.LogInfo("[OpenCV] 使用 MJPEG 格式");
                }
            }
        }

        public bool Read(Mat frame)
        {
            if (!_isOpened || _cap == null)
                return false;

            return _cap.Read(frame);
        }

        public void Release()
        {
            if (_cap != null)
            {
                if (_cap.IsOpened())
                {
                    _cap.Release();
                }
                _cap.Dispose();
                _cap = null;
            }
            _isOpened = false;
        }

        public void Dispose()
        {
            Release();
        }
    }
}
