using MaterialDesignThemes.Wpf;
using OpenCvSharp;
using OpenCvSharp.Flann;
using OpenCvSharp.WpfExtensions;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace camera
{
    public class CameraService : BindableBase, IDisposable
    {
        VideoCapture cap;
        VideoWriter capWriter;
        double FpsOrigin;
        double WidthOrigin;
        double HeightOrigin;
        bool isCapture = false;//是否退出捕获线程

        private Mat _BufferFrame = new();

        private object _lockObject = new object();

        //string _ffmpegPath = @"D:\ffmpeg-7.0.2-full_build\bin\ffmpeg.exe";
        string _ffmpegPath = @"./ffmpeg.exe";


        private int _Quality = 30;
        public int Quality
        {
            get { return _Quality; }
            set { SetProperty(ref _Quality, value); }
        }

        private bool _isSave = false;//是否保存
        public bool isSave
        {
            get { return _isSave; }
            set
            {
                //防止递归
                if (isSave != value)
                {

                    SetProperty(ref _isSave, value);
                    OnIsSaveChanged();
                }

            }
        }

        private void OnIsSaveChanged()
        {
            if (isSave)
            {
                InitializeFFmpeg();
            }
            else
            {
                FinalizeFfmpeg();
            }
        }

        private double _Fps;
        public double Fps
        {
            get { return _Fps; }
            set { SetProperty(ref _Fps, value); }
        }

        private double _Width;
        public double Width
        {
            get { return _Width; }
            set { SetProperty(ref _Width, value); }
        }

        private double _Height;
        public double Height
        {
            get { return _Height; }
            set { SetProperty(ref _Height, value); }
        }

        //帧对外开放读取
        private Mat _Frame = new();
        public Mat Frame
        {
            get { return _Frame; }
            set { SetProperty(ref _Frame, value); }
        }

        private string _Name = "test";
        public string Name
        {
            get { return _Name; }
            set { SetProperty(ref _Name, value); }
        }


        //记录摄像机的编号
        private int _Index = -1;
        public int Index
        {
            get { return _Index; }
            set { SetProperty(ref _Index, value); }
        }

        private DateTime _lastSaveTime;

        public string saveDirectory = "SavedVideos"; // 保存视频的目录
        //此处还应该区分视频保存的目录


        private TimeSpan _saveInterval = TimeSpan.FromMinutes(1); // 默认5分钟保存一次

        public TimeSpan SaveInterval
        {
            get { return _saveInterval; }
            set { SetProperty(ref _saveInterval, value); }
        }


        private long _diskThreshold = 1024;
        public long DiskThreshold
        {
            get { return _diskThreshold; }
            set
            {
                if (_diskThreshold != value)
                {
                    SetProperty(ref _diskThreshold, value);
                    Debug.WriteLine($"设置{Index}磁盘阈值:{DiskThreshold}");
                }
            }
        }

        #region 后期处理相关

        /// <summary>
        /// 对比度
        /// </summary>
        private double contrast = 1.0;
        public double Contrast
        {
            get { return contrast; }
            set { SetProperty(ref contrast, value); }
        }

        /// <summary>
        /// 亮度
        /// </summary>
        private double luminance = 0.0;
        public double Luminance
        {
            get { return luminance; }
            set { SetProperty(ref luminance, value); }
        }

        private bool _IsGrayHomogenization = false;
        public bool IsGrayHomogenization
        {
            get { return _IsGrayHomogenization; }
            set { SetProperty(ref _IsGrayHomogenization, value); }
        }

        #endregion


        //记录保存策略是否为覆盖旧文件
        private bool _overwriteOldFiles = true;
        public bool OverwriteOldFiles
        {
            get { return _overwriteOldFiles; }
            set
            {
                if (_overwriteOldFiles != value)
                {
                    SetProperty(ref _overwriteOldFiles, value);
                    Debug.WriteLine($"设置{Index}保存模式为{OverwriteOldFiles}");
                }
            }
        }

        #region 委托与事件

        public delegate void FrameChangedEventHandler(Mat frame);
        public event FrameChangedEventHandler FrameChanged;

        public event EventHandler<string> DiskSpaceLow;//空间不足提醒

        #endregion

        public CameraService()
        {
            FrameChanged += AddOverlayTime;
            //   if (!File.Exists(_ffmpegPath))
            // {
            //     Debug.WriteLine($"FFmpeg 可执行文件未找到：{_ffmpegPath}");
            //     
            // }
        }

        public int getCameraNum()
        {
            return 1;
        }

        private void ShowVideoInfo()
        {
            cap.Set(VideoCaptureProperties.Fps, 25.0);
            cap.Set(VideoCaptureProperties.FrameWidth, 320);
            cap.Set(VideoCaptureProperties.FrameHeight, 240);
            FpsOrigin = cap.Get(VideoCaptureProperties.Fps);
            WidthOrigin = cap.Get(VideoCaptureProperties.FrameWidth);
            HeightOrigin = cap.Get(VideoCaptureProperties.FrameHeight);
            Debug.WriteLine($"原始数据格式:{WidthOrigin}*{HeightOrigin} {FpsOrigin}Hz");
            //估算码率
            // long totalBytes = 0;
            // totalBytes+= Frame.Total() * Frame.ElemSize();
            // double bitrate = (totalBytes * 8) / elapsedSeconds; // 比特/秒
            // Debug.WriteLine($"估算比特率: {bitrate / 1000000:F2} Mbps");
        }

        /// <summary>
        /// 开始捕获摄像
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public bool StartCapture(int index = 0)
        {
            try
            {
                Logger.LogInfo($"正在打开摄像头索引 {index}...");
                cap = new VideoCapture(index);

                Logger.LogInfo($"设置摄像头 {index} 编码格式为 MJPG...");
                cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));

                if (!cap.IsOpened())
                {
                    Logger.LogWarning($"摄像头 {index} 无法打开（IsOpened 返回 false）");
                    return false;
                }

                this.Index = index;
                Logger.LogInfo($"摄像头 {index} 已打开，正在获取视频信息...");

                //获取视频本身的各种数据
                ShowVideoInfo();
                Fps = FpsOrigin;
                Width = WidthOrigin;
                Height = HeightOrigin;

                Logger.LogInfo($"摄像头 {index} 视频信息: {Width}x{Height} @ {Fps}fps");
                Logger.LogInfo($"启动摄像头 {index} 的帧刷新线程...");

                RefreshFrame();

                Logger.LogInfo($"摄像头 {index} 启动成功");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"启动摄像头 {index} 时发生错误", ex);
                Debug.WriteLine($"启动摄像头时发生错误: {ex.Message}");
                return false;
            }
        }


        // 自适应直方图均衡化
        private Mat ApplyCLAHE(Mat input)
        {
            Mat output = new Mat();
            Mat[] channels = null;
            Mat enhancedL = null;
            Mat resizedL = null;
            Mat blendedL = null;
            CLAHE clahe = null;

            try
            {
                // 转换到LAB颜色空间
                Cv2.CvtColor(input, output, ColorConversionCodes.BGR2Lab);

                // 分离通道
                channels = Cv2.Split(output);

                // 对L通道应用CLAHE
                clahe = Cv2.CreateCLAHE(3.0, new OpenCvSharp.Size(8, 8));
                enhancedL = new Mat();
                clahe.Apply(channels[0], enhancedL);

                // 创建一个与原始L通道大小相同的Mat
                resizedL = new Mat();
                Cv2.Resize(enhancedL, resizedL, channels[0].Size(), 0, 0, InterpolationFlags.Linear);

                // 使用双线性插值混合原始L通道和增强后的L通道
                blendedL = new Mat();
                Cv2.AddWeighted(channels[0], 0.5, resizedL, 0.5, 0, blendedL);

                // 用混合后的L通道替换原始L通道
                // 先释放原来的通道
                channels[0].Dispose();
                channels[0] = blendedL;
                blendedL = null; // 避免被后续释放

                // 合并通道
                Cv2.Merge(channels, output);

                // 转换回BGR颜色空间
                Cv2.CvtColor(output, output, ColorConversionCodes.Lab2BGR);

                return output;
            }
            finally
            {
                // 释放所有临时 Mat 对象
                if (channels != null)
                {
                    foreach (var channel in channels)
                    {
                        channel?.Dispose();
                    }
                }
                enhancedL?.Dispose();
                resizedL?.Dispose();
                blendedL?.Dispose();
                clahe?.Dispose();
            }
        }

        /// <summary>
        /// 刷新Frame帧的线程,使用isCapture控制是否跳出循环,跳出循环时执行关闭写入并压缩覆盖
        /// </summary>
        private void RefreshFrame()
        {
            isCapture = true;
            _lastSaveTime = DateTime.Now;

            Task.Run(() =>
            {
                Stopwatch stopwatch = new Stopwatch();
                long frameInterval = (long)(1000.0 / Fps * TimeSpan.TicksPerMillisecond);
                long nextFrameTime = 0;

                while (isCapture)
                {
                    stopwatch.Start();
                    Mat tempFrame = null;
                    Mat processedFrame = null;

                    try
                    {
                        tempFrame = new Mat();
                        if (!cap.Read(tempFrame))
                        {
                            Debug.WriteLine("无法读取帧");
                            //MessageBox.Show("无法读取帧,请检查是否被其他程序占用!");
                            //结束自身
                            tempFrame?.Dispose();
                            continue;
                        }
                        if (tempFrame.Empty())
                        {
                            Debug.WriteLine("帧为空");
                            tempFrame?.Dispose();
                            continue;
                        }

                        FrameChanged?.Invoke(tempFrame);

                        // 创建处理后的帧
                        processedFrame = tempFrame.Clone();

                        //针对亮度通道的自适应直方图均衡化
                        if (IsGrayHomogenization)
                        {
                            Mat claheResult = ApplyCLAHE(processedFrame);
                            processedFrame.Dispose();
                            processedFrame = claheResult;
                        }
                        //亮度和对比度设置
                        Cv2.ConvertScaleAbs(processedFrame, processedFrame, Contrast, Luminance);

                        lock (_lockObject)
                        {
                            // 释放旧的缓冲帧并替换
                            var oldBuffer = _BufferFrame;
                            _BufferFrame = processedFrame.Clone();
                            oldBuffer?.Dispose();
                        }

                        if (isSave)
                        {
                            SaveFrame(processedFrame);
                        }

                        if (Application.Current == null)
                        {
                            tempFrame?.Dispose();
                            processedFrame?.Dispose();
                            break;
                        }

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                var oldFrame = _Frame;
                                _Frame = _BufferFrame.Clone();
                                oldFrame?.Dispose();
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"更新 UI 帧时发生错误: {ex.Message}");
                            }
                        });

                        long elapsedTicks = stopwatch.ElapsedTicks;
                        if (elapsedTicks < frameInterval)
                        {
                            long sleepTime = (frameInterval - elapsedTicks) / TimeSpan.TicksPerMillisecond;
                            if (sleepTime > 1)
                                Thread.Sleep((int)sleepTime - 1);
                        }

                        nextFrameTime += frameInterval;
                        while (stopwatch.ElapsedTicks < nextFrameTime)
                        {
                            Thread.Sleep(0);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"捕获帧时发生错误: {ex.Message}");
                        // 不要 return，继续尝试
                    }
                    finally
                    {
                        // 确保释放临时 Mat 对象
                        tempFrame?.Dispose();
                        processedFrame?.Dispose();
                    }
                }
                //跳出循环的时候压缩视频文件
                //CloseVideoWriter();

            });
        }
        string fileName = "";
        string filePath = "";

        public long availableSpace { get; set; }
        private Process _ffmpegProcess;
        private bool _isProcessingVideo = false;
        private CancellationTokenSource _ffmpegErrorReadCts;

        private void InitializeFFmpeg()
        {
            if (!isSave)
                return;

            // 先关闭旧的进程（如果存在）
            FinalizeFfmpeg();

            if (!Directory.Exists(saveDirectory))
            {
                Directory.CreateDirectory(saveDirectory);
            }
            availableSpace = CalcDiskSpaceEnough();//初始化时设置可用空间
            fileName = $"{Index}_{DateTime.Now:yyyyMMdd_HH-mm-ss}.mp4";
            filePath = Path.Combine(saveDirectory, fileName);
            string directory = Path.GetDirectoryName(filePath);
            var TargetFile = FileName2FullPath(directory, fileName);//真正要保存的路径
            Debug.WriteLine($"目标保存路径为:{TargetFile}");
            if (string.IsNullOrEmpty(TargetFile))
                return;
            string directoryPath = Path.GetDirectoryName(TargetFile);
            if (!Directory.Exists(directoryPath))
            {
                try
                {
                    Directory.CreateDirectory(directoryPath);
                    Debug.WriteLine($"目录已创建：{directoryPath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"创建目录时发生错误：{ex.Message}");
                    return;
                }
            }
            //开始计时
            _lastSaveTime = DateTime.Now;

            // 初始化 FFmpeg 进程
            _ffmpegProcess = new Process();
            _ffmpegProcess.StartInfo.FileName = _ffmpegPath;
            //_ffmpegProcess.StartInfo.Arguments = $"-y -hide_banner -f rawvideo -pix_fmt bgr24 -s {Width}x{Height} -r {Fps} -i - -c:v libx264 -preset ultrafast -crf {Quality} -an \"{TargetFile}\"";
            //_ffmpegProcess.StartInfo.Arguments = $"-y -hide_banner -f rawvideo -pix_fmt bgr24 -s {Width}x{Height} -r {Fps} -i - -c:v mpeg4 -preset ultrafast -crf {Quality} -an \"{TargetFile}\"";
            _ffmpegProcess.StartInfo.Arguments = $"-y -hide_banner -f rawvideo -pix_fmt bgr24 -s {Width}x{Height} -r {Fps} -i - -c:v libx264 -preset ultrafast -crf {Quality} -an \"{TargetFile}\"";
            _ffmpegProcess.StartInfo.UseShellExecute = false;
            _ffmpegProcess.StartInfo.RedirectStandardInput = true;
            _ffmpegProcess.StartInfo.RedirectStandardError = true;
            _ffmpegProcess.StartInfo.CreateNoWindow = true;

            try
            {
                _ffmpegProcess.Start();
                _isProcessingVideo = true;

                // 启动一个任务来读取错误输出（使用 CancellationToken）
                _ffmpegErrorReadCts = new CancellationTokenSource();
                Task.Run(async () =>
                {
                    try
                    {
                        string error = await _ffmpegProcess.StandardError.ReadToEndAsync();
                        if (!string.IsNullOrEmpty(error) && !_ffmpegErrorReadCts.Token.IsCancellationRequested)
                        {
                            Debug.WriteLine($"FFmpeg 错误: {error}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"读取 FFmpeg 错误输出时发生异常: {ex.Message}");
                    }
                }, _ffmpegErrorReadCts.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启动 FFmpeg 进程时发生错误: {ex.Message}");
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
                _ffmpegErrorReadCts?.Dispose();
                _ffmpegErrorReadCts = null;
            }
        }

        private void FinalizeFfmpeg()
        {
            // 取消错误读取任务
            if (_ffmpegErrorReadCts != null)
            {
                _ffmpegErrorReadCts.Cancel();
                _ffmpegErrorReadCts.Dispose();
                _ffmpegErrorReadCts = null;
            }

            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try
                {
                    // 关闭标准输入流，通知 FFmpeg 输入结束
                    _ffmpegProcess.StandardInput?.Close();

                    // 等待进程结束，设置超时时间
                    if (!_ffmpegProcess.WaitForExit(5000))  // 等待5秒
                    {
                        _ffmpegProcess.Kill();  // 如果进程没有及时结束，强制结束它
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"关闭 FFmpeg 进程时发生错误: {ex.Message}");
                }
                finally
                {
                    _ffmpegProcess.Dispose();
                    _ffmpegProcess = null;
                }
            }
            else if (_ffmpegProcess != null)
            {
                _ffmpegProcess.Dispose();
                _ffmpegProcess = null;
            }

            _isProcessingVideo = false;
        }

        private void SaveFrame(Mat frame)
        {
            if (!_isProcessingVideo)
            {
                InitializeFFmpeg();
            }

            try
            {
                // 将帧数据直接写入 FFmpeg 的标准输入
                int frameSize = (int)frame.Total() * frame.ElemSize();
                byte[] frameData = new byte[frameSize];
                Marshal.Copy(frame.Data, frameData, 0, frameSize);
                _ffmpegProcess.StandardInput.BaseStream.Write(frameData, 0, frameSize);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"写入帧数据时发生错误: {ex.Message}");
            }

            if (DateTime.Now - _lastSaveTime >= SaveInterval)
            {
                //CloseVideoWriter();
                InitializeFFmpeg();
                _lastSaveTime = DateTime.Now;
            }
        }


        private long CalcDiskSpaceEnough()
        {
            string drive = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
            DriveInfo driveInfo = new DriveInfo(drive);
            long availableSpace = driveInfo.AvailableFreeSpace / (1024 * 1024); // 转换为MB
            Debug.WriteLine("可用空间:" + availableSpace);
            return availableSpace;
        }

        private void DeleteOldestFile()
        {
            string directory = Path.Combine(saveDirectory, Index.ToString());
            if (!Directory.Exists(directory))
            {
                return;
            }

            var oldestFile = Directory.GetFiles(directory, "*.mp4", SearchOption.AllDirectories)
                .OrderBy(f => new FileInfo(f).CreationTime)
                .FirstOrDefault();

            if (oldestFile != null)
            {
                try
                {
                    File.Delete(oldestFile);
                    Debug.WriteLine($"已删除最旧的文件: {oldestFile}");
                    //若文件夹为空,删除文件夹
                    var deleteDirectory = Path.GetDirectoryName(oldestFile);
                    if (Directory.GetFiles(deleteDirectory).Length == 0)
                    {
                        Directory.Delete(deleteDirectory);
                        Console.WriteLine($"目录 '{deleteDirectory}' 已被删除。");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"删除文件时出错: {ex.Message}");
                }
            }
        }


        private void DeleteFilesInPathWithStartStr(string path, string startStr)
        {
            string directoryPath = Path.GetDirectoryName(path);
            if (!Directory.Exists(directoryPath))
                return;

            string[] files = Directory.GetFiles(directoryPath);
            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                if (fileName.StartsWith(startStr, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Delete(file);
                        Debug.WriteLine($"已删除文件：{file}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"删除文件 {file} 时出错：{ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 将 `编号_日期_时间.mp4` 转换为拼接好的文件保存的绝对路径路径,directory/编号/日期/时间.mp4 
        /// </summary>
        private string FileName2FullPath(string directory, string fileName)
        {
            string[] fileNameSplit = fileName.Split('_');
            string date = fileNameSplit[1];
            string time = fileNameSplit[2];
            string fullPath = Path.Combine(directory, Index.ToString(), date, time);
            return fullPath;
        }

        public void ChangeCameraInfo()
        {
            // 设置分辨率
            cap.Set(VideoCaptureProperties.FrameWidth, Width);
            cap.Set(VideoCaptureProperties.FrameHeight, Height);

            // 设置帧率
            cap.Set(VideoCaptureProperties.Fps, Fps);
            // 验证设置是否成功
            double actualWidth = cap.Get(VideoCaptureProperties.FrameWidth);
            double actualHeight = cap.Get(VideoCaptureProperties.FrameHeight);
            double actualFps = cap.Get(VideoCaptureProperties.Fps);
            Debug.WriteLine($"设置的分辨率: {Width}x{Height}, 实际分辨率: {actualWidth}x{actualHeight}");
            Debug.WriteLine($"设置的帧率: {Fps}, 实际帧率: {actualFps}");
            //捕获如果是开启的,就要关闭
            if (cap.IsOpened())
            {
                isCapture = false;
                cap.Release();
                cap = new VideoCapture(0);
                if (!cap.IsOpened())
                {
                    Debug.WriteLine("重新打开摄像头失败");
                    return;
                }
                RefreshFrame();
            }
        }

        /// <summary>
        /// 添加时间文字信息
        /// </summary>
        /// <param name="frame"></param>
        private void AddOverlayTime(Mat frame)
        {
            String nowStr = DateTime.Now.ToString();
            // 设置文本参数
            double fontScale = 0.3;
            int thickness = 1;
            HersheyFonts font = HersheyFonts.HersheySimplex;

            // 计算文本大小
            OpenCvSharp.Size textSize = Cv2.GetTextSize(nowStr, font, fontScale, thickness, out int baseline);

            // 计算日期位置（右上角，留出10像素的边距）
            OpenCvSharp.Point textOrg = new OpenCvSharp.Point(frame.Width - textSize.Width - 10, textSize.Height + 10);

            // 创建一个与原始帧相同大小和类型的覆盖层
            using (Mat overlay = new Mat(frame.Size(), frame.Type()))
            {
                overlay.SetTo(new Scalar(0, 0, 0, 0)); // 完全透明

                // 绘制半透明背景
                OpenCvSharp.Rect bgRect = new OpenCvSharp.Rect(textOrg.X - 5, textOrg.Y - textSize.Height - 5, textSize.Width + 10, textSize.Height + 10);
                Cv2.Rectangle(overlay, bgRect, new Scalar(0, 0, 0, 0), -1);

                // 将背景叠加到原始帧上
                Cv2.AddWeighted(overlay, 0.5, frame, 1.0, 0, frame);
            }

            // 绘制透明背景文本(但必须在带有alpha的RGBA图像上)
            // OpenCvSharp.Rect bgRect = new OpenCvSharp.Rect(textOrg.X - 5, textOrg.Y - textSize.Height - 5, textSize.Width + 10, textSize.Height + 10);
            // Cv2.Rectangle(frame, bgRect, new Scalar(0, 0, 0, 0), -1);

            // 绘制日期文本
            Cv2.PutText(frame, nowStr, textOrg, font, fontScale, new Scalar(255, 255, 255), thickness, LineTypes.AntiAlias);
        }

        public void UpdateOverlayDate()
        {

        }

        public void StopCapture()
        {
            isCapture = false;//跳出循环的时候会压缩视频
            //CloseVideoWriter(); // 确保在停止捕获时关闭视频写入器
            cap.Release();
        }


        #region IDisposable Implementation

        private bool _disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // 释放托管资源
                try
                {
                    // 停止捕获循环
                    isCapture = false;

                    // 取消事件订阅
                    if (FrameChanged != null)
                    {
                        FrameChanged -= AddOverlayTime;
                        // 清空所有其他订阅者
                        foreach (var d in FrameChanged.GetInvocationList())
                        {
                            FrameChanged -= (FrameChangedEventHandler)d;
                        }
                    }

                    // 关闭 FFmpeg 进程
                    FinalizeFfmpeg();

                    // 释放 VideoCapture
                    if (cap != null)
                    {
                        if (cap.IsOpened())
                        {
                            cap.Release();
                        }
                        cap.Dispose();
                        cap = null;
                    }

                    // 释放 VideoWriter（如果使用）
                    if (capWriter != null)
                    {
                        capWriter.Dispose();
                        capWriter = null;
                    }

                    // 释放 Mat 对象
                    if (_BufferFrame != null)
                    {
                        _BufferFrame.Dispose();
                        _BufferFrame = null;
                    }

                    if (_Frame != null)
                    {
                        _Frame.Dispose();
                        _Frame = null;
                    }

                    Debug.WriteLine($"CameraService {Index} 已释放资源");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"释放 CameraService 资源时发生错误: {ex.Message}");
                }
            }

            _disposed = true;
        }

        ~CameraService()
        {
            Dispose(false);
        }

        #endregion


    }
}
