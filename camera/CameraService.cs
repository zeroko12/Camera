using OpenCvSharp;
using Prism.Mvvm;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace camera
{
    public class CameraService : BindableBase, IDisposable
    {
        #region 摄像头相关字段

        private VideoCapture cap;
        private double FpsOrigin, WidthOrigin, HeightOrigin;
        private bool isCapture = false;

        #endregion

        #region 帧缓冲与同步

        private Mat _BufferFrame = new();
        private readonly object _lockObject = new();

        // 缓存时间字符串（每秒更新一次，而非每帧）
        private string _cachedTimeString = "";
        private DateTime _lastTimeStringUpdate = DateTime.MinValue;

        #endregion

        #region FFmpeg 录制相关字段

        private readonly string _ffmpegPath;
        private readonly object _ffmpegLock = new();
        private Process _ffmpegProcess;
        private CancellationTokenSource _ffmpegErrorReadCts;
        private bool _isProcessingVideo = false;
        private bool _needReinitFFmpeg = false;
        private long _frameCount = 0;
        private string _currentFileName = "";
        private string _currentFilePath = "";

        // 编码器回退：优先 h264_qsv（核显硬编码），失败则自动回退到 mpeg4（CPU 软编码）
        private bool _useQsv = true;
        private bool _qsvChecked = false;

        // 重试控制
        private int _ffmpegInitFailCount = 0;
        private const int MaxFFmpegInitRetries = 3;
        private DateTime _lastFFmpegInitAttempt = DateTime.MinValue;
        private static readonly TimeSpan FFmpegRetryCooldown = TimeSpan.FromSeconds(10);

        #endregion


        private int _Quality = 30;
        public int Quality
        {
            get { return _Quality; }
            set { SetProperty(ref _Quality, value); }
        }

        private int _PreviewFps = 15;
        /// <summary>
        /// 预览帧率（1-30），控制 UI 刷新频率
        /// </summary>
        public int PreviewFps
        {
            get { return _PreviewFps; }
            set
            {
                int clamped = Math.Clamp(value, 1, 30);
                SetProperty(ref _PreviewFps, clamped);
            }
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
                // 用户主动开启录制时，重置失败计数，允许重新尝试
                _ffmpegInitFailCount = 0;
                _lastFFmpegInitAttempt = DateTime.MinValue;
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
            set
            {
                // 释放旧的Mat对象，防止内存泄漏
                var oldFrame = _Frame;
                if (SetProperty(ref _Frame, value))
                {
                    // 只有在值真正改变时才释放旧对象
                    if (oldFrame != null && oldFrame != value)
                    {
                        oldFrame.Dispose();
                    }
                }
            }
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

        public string saveDirectory = "SavedVideos";


        private TimeSpan _saveInterval = TimeSpan.FromMinutes(1);

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
            // 使用应用程序目录的绝对路径，避免工作目录不一致导致找不到 ffmpeg
            _ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            Logger.LogInfo($"FFmpeg 路径: {_ffmpegPath}");

            if (!File.Exists(_ffmpegPath))
            {
                Logger.LogWarning($"FFmpeg 可执行文件未找到: {_ffmpegPath}，录像功能将不可用");
            }

            FrameChanged += AddOverlayTime;
        }

        public int getCameraNum()
        {
            return 1;
        }

        private void ShowVideoInfo()
        {
            // ✅ 不强制设置分辨率，让摄像头使用默认的最高分辨率
            // 移除了原来的强制设置：
            // cap.Set(VideoCaptureProperties.Fps, 25.0);
            // cap.Set(VideoCaptureProperties.FrameWidth, 320);
            // cap.Set(VideoCaptureProperties.FrameHeight, 240);

            // 直接读取摄像头当前的配置（通常是最高分辨率）
            FpsOrigin = cap.Get(VideoCaptureProperties.Fps);
            WidthOrigin = cap.Get(VideoCaptureProperties.FrameWidth);
            HeightOrigin = cap.Get(VideoCaptureProperties.FrameHeight);

            Logger.LogInfo($"摄像头默认配置: {WidthOrigin}x{HeightOrigin} @ {FpsOrigin}fps");
            Debug.WriteLine($"摄像头分辨率: {WidthOrigin}x{HeightOrigin} {FpsOrigin}fps");
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
                int uiFrameSkip = 0;

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
                        // 亮度和对比度设置（默认值时跳过，节省 CPU）
                        if (Contrast != 1.0 || Luminance != 0.0)
                        {
                            Cv2.ConvertScaleAbs(processedFrame, processedFrame, Contrast, Luminance);
                        }

                        Mat frameForSave = null;
                        lock (_lockObject)
                        {
                            // ✅ 性能优化：使用引用赋值而非Clone，避免~6MB内存拷贝
                            var oldBuffer = _BufferFrame;
                            _BufferFrame = processedFrame;
                            processedFrame = null;  // 防止后续被释放
                            oldBuffer?.Dispose();

                            // 保存引用，供 SaveFrame 使用
                            frameForSave = _BufferFrame;
                        }

                        if (isSave && frameForSave != null)
                        {
                            SaveFrame(frameForSave);
                        }

                        if (Application.Current == null)
                        {
                            tempFrame?.Dispose();
                            processedFrame?.Dispose();
                            break;
                        }


                        // UI 节流：根据 PreviewFps 控制刷新率（每帧重新计算，支持实时调整）
                        int uiSkipInterval = Math.Max(1, (int)Fps / Math.Max(1, PreviewFps));
                        uiFrameSkip++;
                        if (uiFrameSkip >= uiSkipInterval)
                        {
                            uiFrameSkip = 0;
                            Application.Current.Dispatcher.BeginInvoke(() =>
                            {
                                try
                                {
                                    Mat snapshot = null;
                                    lock (_lockObject)
                                    {
                                        if (_BufferFrame != null)
                                        {
                                            snapshot = _BufferFrame.Clone();
                                        }
                                    }
                                    if (snapshot != null)
                                    {
                                        Frame = snapshot;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError($"更新 UI 帧时发生错误", ex);
                                }
                            });
                        }

                        // 帧率控制（去掉忙等循环，降低 CPU 空转）
                        long elapsedTicks = stopwatch.ElapsedTicks;
                        if (elapsedTicks < frameInterval)
                        {
                            int sleepMs = (int)((frameInterval - elapsedTicks) / TimeSpan.TicksPerMillisecond);
                            if (sleepMs > 0)
                                Thread.Sleep(sleepMs);
                        }

                        // 检查是否需要重新初始化 FFmpeg（在安全的地方处理）
                        if (_needReinitFFmpeg && isSave)
                        {
                            _needReinitFFmpeg = false;
                            Logger.LogInfo("在主循环中重新初始化 FFmpeg...");

                            try
                            {
                                FinalizeFfmpeg();
                                Thread.Sleep(100); // 短暂等待确保完全关闭
                                InitializeFFmpeg();
                            }
                            catch (Exception reinitEx)
                            {
                                Logger.LogError("重新初始化 FFmpeg 失败", reinitEx);
                            }
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

        public long availableSpace { get; set; }

        private void InitializeFFmpeg()
        {
            if (!isSave)
                return;

            lock (_ffmpegLock)
            {
                // 双重检查锁定：如果已经初始化成功，直接返回
                if (_isProcessingVideo && _ffmpegProcess != null && !_ffmpegProcess.HasExited)
                    return;

                // 检查 FFmpeg 是否存在
                if (!File.Exists(_ffmpegPath))
                {
                    if (_ffmpegInitFailCount == 0)
                    {
                        Logger.LogError($"FFmpeg 可执行文件不存在: {_ffmpegPath}，无法录制视频");
                    }
                    _ffmpegInitFailCount++;
                    return;
                }

                // 先关闭旧的进程（如果存在）
                FinalizeFfmpegInternal(); // 使用内部无锁版本避免死锁

                if (!Directory.Exists(saveDirectory))
                {
                    Directory.CreateDirectory(saveDirectory);
                }
                availableSpace = CalcDiskSpaceEnough();//初始化时设置可用空间
                _currentFileName = $"{Index}_{DateTime.Now:yyyyMMdd_HH-mm-ss}.mp4";
                _currentFilePath = Path.Combine(saveDirectory, _currentFileName);
                string directory = Path.GetDirectoryName(_currentFilePath);
                var TargetFile = FileName2FullPath(directory, _currentFileName);//真正要保存的路径
                Debug.WriteLine($"目标保存路径为:{TargetFile}");
                if (string.IsNullOrEmpty(TargetFile))
                    return;
                string directoryPath = Path.GetDirectoryName(TargetFile);
                if (!Directory.Exists(directoryPath))
                {
                    try
                    {
                        Directory.CreateDirectory(directoryPath);
                        Logger.LogInfo($"目录已创建：{directoryPath}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"创建目录时发生错误：{ex.Message}", ex);
                        return;
                    }
                }
                //开始计时
                _lastSaveTime = DateTime.Now;

                // 编码器尝试列表：优先 QSV 硬件编码，失败则回退 mpeg4
                string[] encodersToTry;
                if (_useQsv)
                    encodersToTry = new[] { "h264_qsv", "mpeg4" };
                else
                    encodersToTry = new[] { "mpeg4" };

                bool started = false;
                foreach (var encoder in encodersToTry)
                {
                    string encoderArgs = encoder == "h264_qsv"
                        ? $"-c:v h264_qsv -global_quality {Quality}"
                        : $"-c:v mpeg4 -q:v {Quality}";

                    string fullArgs = $"-y -hide_banner -f rawvideo -pix_fmt bgr24 -s {(int)Width}x{(int)Height} -r {(int)Fps} -i - {encoderArgs} -an \"{TargetFile}\"";

                    _ffmpegProcess = new Process();
                    _ffmpegProcess.StartInfo.FileName = _ffmpegPath;
                    _ffmpegProcess.StartInfo.Arguments = fullArgs;
                    _ffmpegProcess.StartInfo.UseShellExecute = false;
                    _ffmpegProcess.StartInfo.RedirectStandardInput = true;
                    _ffmpegProcess.StartInfo.RedirectStandardError = true;
                    _ffmpegProcess.StartInfo.CreateNoWindow = true;
                    _ffmpegProcess.StartInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;

                    try
                    {
                        Logger.LogInfo($"启动 FFmpeg ({encoder}): {fullArgs}");
                        _ffmpegProcess.Start();
                        _isProcessingVideo = true;

                        // 等待一小段时间，检查是否立即退出
                        Thread.Sleep(300);

                        if (_ffmpegProcess.HasExited)
                        {
                            _isProcessingVideo = false;
                            string stderrOutput = "";
                            try { stderrOutput = _ffmpegProcess.StandardError.ReadToEnd(); } catch { }

                            Logger.LogError($"FFmpeg ({encoder}) 启动后立即退出，退出码: {_ffmpegProcess.ExitCode}");
                            if (!string.IsNullOrEmpty(stderrOutput))
                                Logger.LogError($"FFmpeg 错误输出: {stderrOutput}");

                            _ffmpegProcess.Dispose();
                            _ffmpegProcess = null;

                            // QSV 失败，标记为不可用，继续尝试下一个编码器
                            if (encoder == "h264_qsv")
                            {
                                _useQsv = false;
                                _qsvChecked = true;
                                Logger.LogWarning("h264_qsv 硬件编码不可用，自动回退到 mpeg4 软件编码...");
                                continue; // 尝试 mpeg4
                            }
                            return; // mpeg4 也失败了，放弃
                        }

                        // 启动成功
                        if (encoder == "h264_qsv" && !_qsvChecked)
                        {
                            _qsvChecked = true;
                            Logger.LogInfo("h264_qsv 硬件编码验证通过，使用核显加速");
                        }
                        else if (encoder == "mpeg4")
                        {
                            Logger.LogInfo("使用 mpeg4 软件编码");
                        }

                        started = true;
                        break; // 启动成功，跳出循环
                    }
                    catch (Exception ex)
                    {
                        _isProcessingVideo = false;
                        Logger.LogError($"启动 FFmpeg ({encoder}) 时发生错误", ex);
                        _ffmpegProcess?.Dispose();
                        _ffmpegProcess = null;

                        if (encoder == "h264_qsv")
                        {
                            _useQsv = false;
                            _qsvChecked = true;
                            continue;
                        }
                        return;
                    }
                }

                if (!started || _ffmpegProcess == null)
                    return;

                // 启动异步读取错误输出
                _ffmpegErrorReadCts = new CancellationTokenSource();
                Task.Run(async () =>
                {
                    try
                    {
                        string error = await _ffmpegProcess.StandardError.ReadToEndAsync();
                        if (!string.IsNullOrEmpty(error) && !_ffmpegErrorReadCts.Token.IsCancellationRequested)
                        {
                            Logger.LogWarning($"FFmpeg 输出: {error}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"读取 FFmpeg 错误输出时发生异常: {ex.Message}");
                    }
                }, _ffmpegErrorReadCts.Token);

                Logger.LogInfo($"FFmpeg 进程已成功启动 (PID: {_ffmpegProcess.Id})");
            }
        }

        private void FinalizeFfmpeg()
        {
            lock (_ffmpegLock)
            {
                FinalizeFfmpegInternal();
            }
        }

        private void FinalizeFfmpegInternal()
        {
            if (_ffmpegProcess != null)
            {
                try
                {
                    if (_ffmpegProcess.HasExited)
                    {
                        // FFmpeg 已经退出（可能是崩溃），尝试读取 stderr 诊断原因
                        int exitCode = _ffmpegProcess.ExitCode;
                        string stderrOutput = "";
                        try
                        {
                            // 先取消异步读取，再同步读取剩余输出
                            _ffmpegErrorReadCts?.Cancel();
                            stderrOutput = _ffmpegProcess.StandardError.ReadToEnd();
                        }
                        catch { }

                        if (exitCode != 0)
                        {
                            Logger.LogError($"FFmpeg 异常退出，退出码: {exitCode}");
                            if (!string.IsNullOrEmpty(stderrOutput))
                            {
                                // 只记录最后 500 个字符，避免日志过大
                                string trimmed = stderrOutput.Length > 500
                                    ? stderrOutput.Substring(stderrOutput.Length - 500)
                                    : stderrOutput;
                                Logger.LogError($"FFmpeg stderr: {trimmed}");
                            }
                        }
                    }
                    else
                    {
                        // FFmpeg 还在运行，正常关闭
                        try
                        {
                            _ffmpegProcess.StandardInput?.Close();
                            if (!_ffmpegProcess.WaitForExit(5000))
                            {
                                _ffmpegProcess.Kill();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"关闭 FFmpeg 进程时发生错误: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FinalizeFfmpeg 异常: {ex.Message}");
                }
                finally
                {
                    _ffmpegProcess.Dispose();
                    _ffmpegProcess = null;
                }
            }

            // 清理错误读取任务
            if (_ffmpegErrorReadCts != null)
            {
                _ffmpegErrorReadCts.Cancel();
                _ffmpegErrorReadCts.Dispose();
                _ffmpegErrorReadCts = null;
            }

            _isProcessingVideo = false;
        }


        private void SaveFrame(Mat frame)
        {
            // 检查 FFmpeg 进程是否正常
            if (!_isProcessingVideo || _ffmpegProcess == null || _ffmpegProcess.HasExited)
            {
                // 冷却时间内不重试，避免高频重试导致崩溃
                if ((DateTime.Now - _lastFFmpegInitAttempt) < FFmpegRetryCooldown)
                {
                    return; // 静默跳过，不刷日志
                }

                // 超过最大重试次数后不再尝试
                if (_ffmpegInitFailCount >= MaxFFmpegInitRetries)
                {
                    if (_ffmpegInitFailCount == MaxFFmpegInitRetries)
                    {
                        Logger.LogError($"FFmpeg 已连续失败 {MaxFFmpegInitRetries} 次，停止重试。请检查 ffmpeg.exe 是否存在于: {_ffmpegPath}");
                        _ffmpegInitFailCount++; // 防止重复打印这条日志
                    }
                    return;
                }

                _lastFFmpegInitAttempt = DateTime.Now;
                Logger.LogWarning($"FFmpeg 进程未运行，尝试重新初始化 (第 {_ffmpegInitFailCount + 1}/{MaxFFmpegInitRetries} 次)...");
                InitializeFFmpeg();

                // 如果初始化失败，不继续
                if (!_isProcessingVideo || _ffmpegProcess == null)
                {
                    _ffmpegInitFailCount++;
                    Logger.LogError($"FFmpeg 初始化失败，跳过此帧 (失败次数: {_ffmpegInitFailCount}/{MaxFFmpegInitRetries})");
                    return;
                }

                // 初始化成功，重置计数
                _ffmpegInitFailCount = 0;
                Logger.LogInfo("FFmpeg 重新初始化成功");
            }

            // 每100帧检查一次磁盘空间（避免频繁检查）
            if (_frameCount % 100 == 0)
            {
                availableSpace = CalcDiskSpaceEnough();

                // 检查是否低于阈值
                if (availableSpace < DiskThreshold)
                {
                    Logger.LogWarning($"磁盘空间不足: {availableSpace}MB < {DiskThreshold}MB");

                    // 触发事件通知用户
                    try
                    {
                        DiskSpaceLow?.Invoke(this, $"磁盘空间不足: {availableSpace}MB");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("触发 DiskSpaceLow 事件时发生错误", ex);
                    }

                    // 根据配置决定是否覆盖旧文件
                    if (OverwriteOldFiles)
                    {
                        Logger.LogInfo("尝试删除最旧的文件以释放空间...");
                        DeleteOldestFile();
                        availableSpace = CalcDiskSpaceEnough(); // 重新检查

                        if (availableSpace < DiskThreshold)
                        {
                            Logger.LogError("删除旧文件后空间仍不足，停止录制");
                            StopSave();
                            return;
                        }
                    }
                    else
                    {
                        Logger.LogWarning("磁盘空间不足且未启用覆盖旧文件，停止录制");
                        StopSave();
                        return;
                    }
                }
            }

            // 再次检查 _ffmpegProcess（可能被其他线程修改）
            if (_ffmpegProcess == null || _ffmpegProcess.HasExited)
            {
                _isProcessingVideo = false;
                return;
            }

            byte[] rentedArray = null;
            try
            {
                // 使用 ArrayPool 提高性能，避免频繁 GC
                int frameSize = (int)frame.Total() * frame.ElemSize();
                rentedArray = ArrayPool<byte>.Shared.Rent(frameSize);

                Marshal.Copy(frame.Data, rentedArray, 0, frameSize);
                _ffmpegProcess.StandardInput.BaseStream.Write(rentedArray, 0, frameSize);
                _ffmpegProcess.StandardInput.BaseStream.Flush();

                _frameCount++;
            }
            catch (IOException ioEx)
            {
                Logger.LogError("写入 FFmpeg 失败（管道可能已关闭）", ioEx);

                // FFmpeg 进程可能已崩溃，清理死掉的进程，由头部重试机制统一处理
                FinalizeFfmpeg();
            }
            catch (Exception ex)
            {
                Logger.LogError("保存帧时发生未知错误", ex);
            }
            finally
            {
                // 归还数组到池中
                if (rentedArray != null)
                {
                    ArrayPool<byte>.Shared.Return(rentedArray);
                }
            }

            // 检查是否需要切割视频文件
            if (DateTime.Now - _lastSaveTime >= SaveInterval)
            {
                Logger.LogInfo($"达到切割间隔 ({SaveInterval})，准备创建新文件...");
                _needReinitFFmpeg = true; // 标记需要重新初始化
                _lastSaveTime = DateTime.Now;
            }
        }


        /// <summary>
        /// 停止保存
        /// </summary>
        private void StopSave()
        {
            Logger.LogInfo("停止录制...");
            isSave = false;
            FinalizeFfmpeg();
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

            // 使用 LastWriteTime 而不是 CreationTime（更可靠）
            var oldestFile = Directory.GetFiles(directory, "*.mp4", SearchOption.AllDirectories)
                .OrderBy(f => new FileInfo(f).LastWriteTime)
                .FirstOrDefault();

            Logger.LogInfo($"尝试删除最旧文件，目录: {directory}");

            if (oldestFile != null)
            {
                try
                {
                    var fileInfo = new FileInfo(oldestFile);
                    long fileSizeMB = fileInfo.Length / (1024 * 1024);

                    File.Delete(oldestFile);
                    Logger.LogInfo($"已删除最旧的文件: {Path.GetFileName(oldestFile)} ({fileSizeMB}MB)");
                    Debug.WriteLine($"已删除最旧的文件: {oldestFile}");

                    //若文件夹为空,删除文件夹
                    var deleteDirectory = Path.GetDirectoryName(oldestFile);
                    if (Directory.Exists(deleteDirectory) && Directory.GetFiles(deleteDirectory).Length == 0)
                    {
                        Directory.Delete(deleteDirectory);
                        Logger.LogInfo($"已删除空目录: {deleteDirectory}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"删除文件失败: {oldestFile}", ex);
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
            DateTime now = DateTime.Now;
            if ((now - _lastTimeStringUpdate).TotalSeconds >= 1.0)
            {
                _cachedTimeString = now.ToString();
                _lastTimeStringUpdate = now;
            }

            string nowStr = _cachedTimeString;
            const double fontScale = 0.3;
            const int thickness = 1;
            const HersheyFonts font = HersheyFonts.HersheySimplex;

            OpenCvSharp.Size textSize = Cv2.GetTextSize(nowStr, font, fontScale, thickness, out _);
            var textOrg = new OpenCvSharp.Point(frame.Width - textSize.Width - 10, textSize.Height + 10);

            // 只在文字区域绘制半透明背景（ROI 操作，而非全帧 AddWeighted）
            var bgRect = new OpenCvSharp.Rect(
                textOrg.X - 5, textOrg.Y - textSize.Height - 5,
                textSize.Width + 10, textSize.Height + 10);

            // 边界裁剪
            bgRect = bgRect.Intersect(new OpenCvSharp.Rect(0, 0, frame.Width, frame.Height));
            if (bgRect.Width > 0 && bgRect.Height > 0)
            {
                using var roi = new Mat(frame, bgRect);
                roi.ConvertTo(roi, -1, 0.5, 0); // 只暗化文字背景区域
            }

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
