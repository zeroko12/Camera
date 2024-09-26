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
    public class CameraService : BindableBase
    {
        VideoCapture cap;
        VideoWriter capWriter;
        double FpsOrigin;
        double WidthOrigin;
        double HeightOrigin;
        bool isCapture = false;//是否退出捕获线程

        private Mat _BufferFrame = new();

        private object _lockObject = new object();


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
            if (_isSave)
            {
                InitializeVideoWriter();
            }
            else
            {
                CloseVideoWriter();
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

        private VideoWriter _videoWriter;
        private DateTime _lastSaveTime;

        private string _saveDirectory = "SavedVideos"; // 保存视频的目录
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
                cap = new VideoCapture(index);
                cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                if (!cap.IsOpened())
                    return false;
                this.Index = index;
                //获取视频本身的各种数据
                ShowVideoInfo();
                Fps = FpsOrigin;
                Width = WidthOrigin;
                Height = HeightOrigin;
                RefreshFrame();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启动摄像头时发生错误: {ex.Message}");
                return false;
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
                    try
                    {
                        Mat tempFrame = new Mat();
                        if (!cap.Read(tempFrame))
                        {
                            Debug.WriteLine("无法读取帧");
                            MessageBox.Show("无法读取帧,请检查是否被其他程序占用!");
                            //结束自身
                            continue;
                        }
                        if (tempFrame.Empty())
                        {
                            Debug.WriteLine("帧为空");
                            continue;
                        }

                        FrameChanged?.Invoke(tempFrame);
                        lock (_lockObject)
                        {
                            tempFrame.CopyTo(_BufferFrame);
                        }

                        if (isSave)
                        {
                            SaveFrame(tempFrame);
                        }

                        if (Application.Current == null)
                            break;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            Frame = _BufferFrame.Clone();
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
                        return;
                    }
                }
                //跳出循环的时候压缩视频文件
                CloseVideoWriter();

            });
        }
        string fileName = "";
        string filePath = "";
        string originFileName = "";

        public long availableSpace { get; set; }
        private void InitializeVideoWriter()
        {
            if (!isSave)
                return;
            if (!Directory.Exists(_saveDirectory))
            {
                Directory.CreateDirectory(_saveDirectory);
            }
            availableSpace = CalcDiskSpaceEnough();//初始化时设置可用空间
            originFileName = $"{Index}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            fileName = "TEMP_" + originFileName;
            filePath = Path.Combine(_saveDirectory, fileName);
            //开始计时
            _lastSaveTime = DateTime.Now;
            //_videoWriter = new VideoWriter(filePath, FourCC.MP4V, Fps, new OpenCvSharp.Size(Width, Height));
            _videoWriter = new VideoWriter(filePath, FourCC.MJPG, Fps, new OpenCvSharp.Size(Width, Height));
        }

        private void SaveFrame(Mat frame)
        {
            if (_videoWriter == null || !_videoWriter.IsOpened())
            {
                InitializeVideoWriter();
            }

            _videoWriter.Write(frame);

            if (DateTime.Now - _lastSaveTime >= SaveInterval)
            {
                CloseVideoWriter();
                InitializeVideoWriter();
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
            string directory = Path.Combine(_saveDirectory, Index.ToString());
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

        //string _ffmpegPath = @"D:\ffmpeg-7.0.2-full_build\bin\ffmpeg.exe";
        string _ffmpegPath = @"./ffmpeg.exe";
        // 启动FFmpeg进程
        private void StartFFmpegProcess(string absolutePath, string OutputPath)
        {
            if (!File.Exists(absolutePath))
                return;
            if (string.IsNullOrEmpty(OutputPath))
                return;
            string directoryPath = Path.GetDirectoryName(OutputPath);
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
            //根据OutputPath解析最终存放路径
            var arguments = $"-i \"{absolutePath}\" -c:v libx264 -preset slow -threads 2 -crf {Quality} -an \"{OutputPath}\"";

            var processStartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directoryPath
            };

            using (Process process = new Process { StartInfo = processStartInfo })
            {
                process.Start();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Debug.WriteLine($"FFmpeg 执行失败。错误：{error}");
                }
                else
                {
                    if (File.Exists(OutputPath))
                    {
                        //File.Delete(absolutePath);
                        DeleteFilesInPathWithStartStr(absolutePath, "TEMP_");
                        Debug.WriteLine($"删除{absolutePath}");
                    }
                    else
                    {
                        Debug.WriteLine("临时输出文件未找到");
                    }
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

        private void CloseVideoWriter()
        {
            try
            {
                // 检查磁盘空间
                if (availableSpace <= DiskThreshold && isSave)//空间告罄,并且保存开启的清况下
                {
                    if (OverwriteOldFiles)
                    {
                        DeleteOldestFile();
                    }
                    else
                    {
                        // 丢弃当前帧并停止保存
                        //isSave = false;
                        _videoWriter?.Dispose();
                        // 触发事件而不是直接显示消息框
                        DiskSpaceLow?.Invoke(this, "磁盘空间不足,停止保存视频");

                        return;
                    }
                }
                _videoWriter?.Release();
                _videoWriter?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            //压缩视频
            if (filePath == "" || originFileName == "")
                return;
            string absolutePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filePath);
            string directory = Path.GetDirectoryName(absolutePath);
            string outputPath = FileName2FullPath(directory, originFileName);

            Debug.WriteLine("绝对路径:" + absolutePath);
            Task.Run(() => StartFFmpegProcess(absolutePath, outputPath));
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
            Mat overlay = new Mat(frame.Size(), frame.Type());
            overlay.SetTo(new Scalar(0, 0, 0, 0)); // 完全透明

            // 绘制半透明背景
            OpenCvSharp.Rect bgRect = new OpenCvSharp.Rect(textOrg.X - 5, textOrg.Y - textSize.Height - 5, textSize.Width + 10, textSize.Height + 10);
            Cv2.Rectangle(overlay, bgRect, new Scalar(0, 0, 0, 0), -1);

            // 将背景叠加到原始帧上
            Cv2.AddWeighted(overlay, 0.5, frame, 1.0, 0, frame);

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
            cap.Release();
        }


        ~CameraService()
        {
            Debug.WriteLine("析构析构析构析构析构析构析构");
        }

    }
}
