using System;

namespace camera.Capture
{
    public static class CameraCaptureFactory
    {
        public static bool PreferFFmpeg { get; set; } = false;
        public static bool PreferMediaFoundation { get; set; } = false;
        public static bool TryDirectShow { get; set; } = false;

        public static ICameraCapture Create(int index, int width = 0, int height = 0, double fps = 0)
        {
            ICameraCapture capture = null;
            string failedImplementations = "";

            if (PreferFFmpeg)
            {
                try
                {
                    capture = new FFmpegCameraCapture();
                    if (capture.Open(index, width, height, fps))
                    {
                        Logger.LogInfo("[Factory] 成功使用 " + capture.ImplementationName + " 打开摄像头 " + index);
                        return capture;
                    }
                    else
                    {
                        failedImplementations += capture.ImplementationName;
                        capture.Dispose();
                        capture = null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("[Factory] FFmpeg 初始化失败. " + ex.Message);
                    failedImplementations += "FFmpeg";
                    if (capture != null)
                        capture.Dispose();
                    capture = null;
                }
            }

            if (PreferMediaFoundation)
            {
                try
                {
                    capture = new MediaFoundationCapture();
                    if (capture.Open(index, width, height, fps))
                    {
                        if (!string.IsNullOrEmpty(failedImplementations))
                        {
                            Logger.LogInfo("[Factory] " + failedImplementations + " 不可用，降级使用 " + capture.ImplementationName);
                        }
                        else
                        {
                            Logger.LogInfo("[Factory] 成功使用 " + capture.ImplementationName + " 打开摄像头 " + index);
                        }
                        return capture;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(failedImplementations))
                            failedImplementations += ", ";
                        failedImplementations += capture.ImplementationName;
                        capture.Dispose();
                        capture = null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("[Factory] MediaFoundation 初始化失败. " + ex.Message);
                    if (!string.IsNullOrEmpty(failedImplementations))
                        failedImplementations += ", ";
                    failedImplementations += "MediaFoundation";
                    if (capture != null)
                        capture.Dispose();
                    capture = null;
                }
            }

            if (TryDirectShow)
            {
                try
                {
                    capture = new DirectShowCameraCapture();
                    if (capture.Open(index, width, height, fps))
                    {
                        if (!string.IsNullOrEmpty(failedImplementations))
                        {
                            Logger.LogInfo("[Factory] " + failedImplementations + " 不可用，降级使用 " + capture.ImplementationName);
                        }
                        else
                        {
                            Logger.LogInfo("[Factory] 成功使用 " + capture.ImplementationName + " 打开摄像头 " + index);
                        }
                        return capture;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(failedImplementations))
                            failedImplementations += ", ";
                        failedImplementations += capture.ImplementationName;
                        capture.Dispose();
                        capture = null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("[Factory] DirectShow 初始化失败. " + ex.Message);
                    if (!string.IsNullOrEmpty(failedImplementations))
                        failedImplementations += ", ";
                    failedImplementations += "DirectShow";
                    if (capture != null)
                        capture.Dispose();
                    capture = null;
                }
            }

            try
            {
                capture = new OpenCvCameraCapture();
                if (capture.Open(index, width, height, fps))
                {
                    if (!string.IsNullOrEmpty(failedImplementations))
                    {
                        Logger.LogInfo("[Factory] " + failedImplementations + " 不可用，降级使用 " + capture.ImplementationName);
                    }
                    else
                    {
                        Logger.LogInfo("[Factory] 使用 " + capture.ImplementationName + " 打开摄像头 " + index);
                    }
                    return capture;
                }
                else
                {
                    capture.Dispose();
                    throw new InvalidOperationException("OpenCV also failed to open camera");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[Factory] 所有捕获实现均失败. " + ex.Message);
                if (capture != null)
                    capture.Dispose();
                throw;
            }
        }
    }
}
