using OpenCvSharp;
using Prism.Mvvm;
using System;
using System.Buffers;
using System.Collections.Concurrent;
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
        
        // 视频帧保存队列（工业级防死锁）
        private BlockingCollection<Mat> _frameSaveQueue;
        private CancellationTokenSource _ffmpegWriteCts;
        private Task _ffmpegWriteTask;

        // 相机读取失败计数
        private int _continuousReadFailCount = 0;

        private bool _isProcessingVideo = false;
        private bool _needReinitFFmpeg = false;
        private long _frameCount = 0;
        private string _currentFileName = "";
        private string _currentFilePath = "";

        // 编码器会话管理：支持硬件编码器并发限制
        // 硬件编码器会话管理
        private static readonly object _encoderLock = new object();

        // 编码器可用性标记
        private static bool _useNvenc = true;
        private static bool _useAmf = true;
        private static bool _useQsv = true;
        private static bool _useLibx264 = true;
        private static bool _nvencChecked = false;
        private static bool _amfChecked = false;
        private static bool _qsvChecked = false;
        private static bool _libx264Checked = false;

        // 硬件编码器并发会话计数（消费级显卡限制）
        private static int _nvencSessionCount = 0;
        private static int _amfSessionCount = 0;
        private static int _qsvSessionCount = 0;

        // 追踪本次运行时失败的编码器（避免反复尝试失败的编码器）
        private static readonly System.Collections.Generic.HashSet<string> _failedEncoders = new();
        private readonly System.Collections.Generic.HashSet<string> _thisCameraFailedEncoders = new();

        // 硬件编码器最大会话数限制（可配置，基于常见显卡的保守默认值）
        private static int _maxNvencSessions = 2;     // NVIDIA 消费级: 2, 专业卡: 8-32
        private static int _maxAmfSessions = 3;        // AMD: 3-4
        private static int _maxQsvSessions = 2;        // Intel QSV: 2

        // 配置属性 - 允许在运行时调整
        public static int MaxNvencSessions
        {
            get => _maxNvencSessions;
            set { lock (_encoderLock) _maxNvencSessions = Math.Max(1, value); }
        }
        public static int MaxAmfSessions
        {
            get => _maxAmfSessions;
            set { lock (_encoderLock) _maxAmfSessions = Math.Max(1, value); }
        }
        public static int MaxQsvSessions
        {
            get => _maxQsvSessions;
            set { lock (_encoderLock) _maxQsvSessions = Math.Max(1, value); }
        }

        // 初始化编码器会话限制（根据常见显卡类型设置合理值）
        static CameraService()
        {
            // 尝试从环境变量或配置读取（如果存在）
            TryLoadEncoderLimitsFromConfig();
        }

        private static void TryLoadEncoderLimitsFromConfig()
        {
            try
            {
                var config = System.Configuration.ConfigurationManager.AppSettings;
                if (int.TryParse(config["MaxNvencSessions"], out int nvenc))
                    _maxNvencSessions = Math.Max(1, nvenc);
                if (int.TryParse(config["MaxAmfSessions"], out int amf))
                    _maxAmfSessions = Math.Max(1, amf);
                if (int.TryParse(config["MaxQsvSessions"], out int qsv))
                    _maxQsvSessions = Math.Max(1, qsv);
            }
            catch { }
        }

        // 追踪当前实例使用的编码器
        private string _currentEncoder = "";

        // 编码器选择策略
        public enum EncoderPreference
        {
            Auto,       // 自动选择（优先硬件，考虑会话数
            PreferHardware, // 强制硬件优先，会话满则失败
            ForceSoftware   // 强制软件编码
        }
        private static EncoderPreference _globalEncoderPreference = EncoderPreference.Auto;
        public static EncoderPreference GlobalEncoderPreference
        {
            get => _globalEncoderPreference;
            set => _globalEncoderPreference = value;
        }

        // 重试控制
        private int _ffmpegInitFailCount = 0;
        private const int MaxFFmpegInitRetries = 3;
        private DateTime _lastFFmpegInitAttempt = DateTime.MinValue;
        private static readonly TimeSpan FFmpegRetryCooldown = TimeSpan.FromSeconds(10);

        // 摄像头捕获格式
        private enum CaptureFormat { YUY2, NV12, MJPG }
        private static CaptureFormat _preferredFormat = CaptureFormat.YUY2;
        private static bool _formatChecked = false;

        #endregion


        private int _Quality = 20;
        public int Quality
        {
            get { return _Quality; }
            set { SetProperty(ref _Quality, value); }
        }

        private int _PreviewFps = 15;
        /// <summary>
        /// 预览帧率（1-摄像头帧率），控制 UI 刷新频率
        /// </summary>
        public int PreviewFps
        {
            get { return _PreviewFps; }
            set
            {
                int maxFps = Fps > 0 ? (int)Fps : 30;
                int clamped = Math.Clamp(value, 1, maxFps);
                SetProperty(ref _PreviewFps, clamped);
            }
        }

        private int _RecordFps = 15;
        /// <summary>
        /// 录制帧率（1-摄像头帧率），控制喂给 FFmpeg 的帧频率，降低可节省 CPU
        /// </summary>
        public int RecordFps
        {
            get { return _RecordFps; }
            set
            {
                int maxFps = Fps > 0 ? (int)Fps : 30;
                int clamped = Math.Clamp(value, 1, maxFps);
                SetProperty(ref _RecordFps, clamped);
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

        private bool _IsHighDefinition = false;
        public bool IsHighDefinition
        {
            get { return _IsHighDefinition; }
            set { SetProperty(ref _IsHighDefinition, value); }
        }

        private int _MaxPreviewWidth = 640;
        public int MaxPreviewWidth
        {
            get { return _MaxPreviewWidth; }
            set { SetProperty(ref _MaxPreviewWidth, value); }
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
            // OBS Studio 风格的摄像头分辨率探测：尝试多个标准分辨率，选择摄像头支持的最高分辨率
            var testResolutions = new (int Width, int Height)[]
            {
                (3840, 2160), // 4K
                (2560, 1440), // QHD
                (1920, 1080), // 1080p
                (1600, 1200), // UXGA
                (1280, 720),  // 720p
                (1024, 768),  // XGA
                (800, 600),   // SVGA
                (640, 480),   // VGA
            };

            int bestWidth = 0;
            int bestHeight = 0;
            double bestFps = 0;

            // 先读取当前默认配置作为备选
            double defaultFps = cap.Get(VideoCaptureProperties.Fps);
            double defaultWidth = cap.Get(VideoCaptureProperties.FrameWidth);
            double defaultHeight = cap.Get(VideoCaptureProperties.FrameHeight);

            Logger.LogInfo($"摄像头默认配置: {defaultWidth}x{defaultHeight} @ {defaultFps}fps");

            // 尝试从高到低设置各个标准分辨率
            foreach (var (width, height) in testResolutions)
            {
                try
                {
                    // 尝试设置分辨率
                    cap.Set(VideoCaptureProperties.FrameWidth, width);
                    cap.Set(VideoCaptureProperties.FrameHeight, height);
                    Thread.Sleep(30); // 给摄像头一点时间响应

                    // 读取实际设置的分辨率
                    double actualWidth = cap.Get(VideoCaptureProperties.FrameWidth);
                    double actualHeight = cap.Get(VideoCaptureProperties.FrameHeight);
                    double actualFps = cap.Get(VideoCaptureProperties.Fps);

                    // 检查是否设置成功（允许微小误差）
                    if (Math.Abs(actualWidth - width) < 10 && Math.Abs(actualHeight - height) < 10)
                    {
                        bestWidth = (int)actualWidth;
                        bestHeight = (int)actualHeight;
                        bestFps = actualFps > 0 ? actualFps : 30;
                        Logger.LogInfo($"摄像头支持分辨率: {bestWidth}x{bestHeight} @ {bestFps}fps");
                        break; // 找到第一个支持的最高分辨率，停止尝试
                    }
                }
                catch
                {
                    // 这个分辨率不支持，继续尝试下一个
                }
            }

            // 如果没有找到任何支持的标准分辨率，使用默认配置
            if (bestWidth == 0 || bestHeight == 0)
            {
                bestWidth = (int)defaultWidth;
                bestHeight = (int)defaultHeight;
                bestFps = defaultFps > 0 ? defaultFps : 30;
                Logger.LogInfo($"使用摄像头默认分辨率: {bestWidth}x{bestHeight} @ {bestFps}fps");
            }

            FpsOrigin = bestFps;
            WidthOrigin = bestWidth;
            HeightOrigin = bestHeight;

            Logger.LogInfo($"最终摄像头配置: {WidthOrigin}x{HeightOrigin} @ {FpsOrigin}fps");
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

                // 尝试多种捕获格式，优先使用未压缩格式以避免MJPEG解压缩开销
                bool formatSet = false;
                if (!_formatChecked || _preferredFormat == CaptureFormat.YUY2)
                {
                    Logger.LogInfo($"尝试设置摄像头 {index} 编码格式为 YUY2...");
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('Y', 'U', 'Y', '2'));
                    Thread.Sleep(50);
                    if (cap.IsOpened())
                    {
                        formatSet = true;
                        _preferredFormat = CaptureFormat.YUY2;
                        Logger.LogInfo($"摄像头 {index} 使用 YUY2 格式");
                    }
                }

                if (!formatSet && (!_formatChecked || _preferredFormat == CaptureFormat.NV12))
                {
                    Logger.LogInfo($"尝试设置摄像头 {index} 编码格式为 NV12...");
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('N', 'V', '1', '2'));
                    Thread.Sleep(50);
                    if (cap.IsOpened())
                    {
                        formatSet = true;
                        _preferredFormat = CaptureFormat.NV12;
                        Logger.LogInfo($"摄像头 {index} 使用 NV12 格式");
                    }
                }

                if (!formatSet)
                {
                    Logger.LogInfo($"设置摄像头 {index} 编码格式为 MJPG...");
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                    _preferredFormat = CaptureFormat.MJPG;
                }
                _formatChecked = true;

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
                int recordFrameSkip = 0;

                while (isCapture)
                {
                    stopwatch.Restart(); // ✅ 优化3：每帧重置计时器（原 Start() 从不重置，ticks 持续累加）
                    Mat tempFrame = null;
                    Mat processedFrame = null;

                    try
                    {
                        tempFrame = new Mat();
                        if (!cap.Read(tempFrame) || tempFrame.Empty())
                        {
                            Debug.WriteLine("无法读取帧或空帧");
                            tempFrame?.Dispose();

                            _continuousReadFailCount++;
                            if (_continuousReadFailCount > 30) // 大约丢失1秒的帧数
                            {
                                Logger.LogWarning($"摄像头 {Index} 连续读取失败超过 30 次，底端崩溃，尝试重新初始化底层连接...");
                                Thread.Sleep(1000); // 必须退避以防止空循环死锁 CPU
                                try
                                {
                                    if (cap != null && cap.IsOpened())
                                    {
                                        cap.Release();
                                    }
                                    cap?.Dispose();
                                    
                                    cap = new VideoCapture(Index);
                                    // 使用已确定的最优格式
                                    if (_preferredFormat == CaptureFormat.YUY2)
                                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('Y', 'U', 'Y', '2'));
                                    else if (_preferredFormat == CaptureFormat.NV12)
                                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('N', 'V', '1', '2'));
                                    else
                                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                                    cap.Set(VideoCaptureProperties.FrameWidth, Width);
                                    cap.Set(VideoCaptureProperties.FrameHeight, Height);
                                    cap.Set(VideoCaptureProperties.Fps, Fps);
                                    
                                    if (cap.IsOpened())
                                    {
                                        _continuousReadFailCount = 0;
                                        Logger.LogInfo($"摄像头 {Index} 系统自恢复重置成功。");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError($"摄像头 {Index} 尝试重新初始化连接时发生异常", ex);
                                }
                            }
                            else
                            {
                                Thread.Sleep(30); // 普通底层错误时短暂退避，避免占用 100% CPU 空转
                            }
                            continue;
                        }

                        // 能够顺利读到此处，证明底层通路正常运行，清空安全重试错误记录
                        _continuousReadFailCount = 0;

                        FrameChanged?.Invoke(tempFrame);

                        // ✅ 优化2：仅在需要图像处理时才 Clone，否则直接使用原帧
                        bool needProcess = IsGrayHomogenization || Contrast != 1.0 || Luminance != 0.0;
                        if (needProcess)
                        {
                            processedFrame = tempFrame.Clone();

                            // 针对亮度通道的自适应直方图均衡化
                            if (IsGrayHomogenization)
                            {
                                Mat claheResult = ApplyCLAHE(processedFrame);
                                processedFrame.Dispose();
                                processedFrame = claheResult;
                            }
                            // 亮度和对比度设置
                            if (Contrast != 1.0 || Luminance != 0.0)
                            {
                                Cv2.ConvertScaleAbs(processedFrame, processedFrame, Contrast, Luminance);
                            }
                        }
                        else
                        {
                            // 无需处理，直接引用原帧（省去 921KB Clone）
                            processedFrame = tempFrame;
                            tempFrame = null; // 防止被后续释放
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

                        // ✅ 优化1：录制降帧 — 根据 RecordFps 跳帧，减少 FFmpeg 编码量
                        if (isSave && frameForSave != null)
                        {
                            int recordSkipInterval = Math.Max(1, (int)Fps / Math.Max(1, RecordFps));
                            recordFrameSkip++;
                            if (recordFrameSkip >= recordSkipInterval)
                            {
                                recordFrameSkip = 0;
                                // 采用多线程非阻塞队列保存。如果底层 FFmpeg 写入阻塞或被操作系统挂起导致挤压过满，强行丢弃避免拖死相机捕获线程
                                if (_frameSaveQueue != null && !_frameSaveQueue.IsAddingCompleted)
                                {
                                    Mat clonedToSave = frameForSave.Clone();
                                    if (!_frameSaveQueue.TryAdd(clonedToSave))
                                    {
                                        clonedToSave.Dispose(); // 队列满了，牺牲存储而保留实时视频流不死
                                        Logger.LogWarning($"警告: 摄像头 {Index} FFmpeg IO阻塞缓冲区已满，被迫丢掉一帧数据...");
                                    }
                                }
                            }
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
                                        if (_BufferFrame != null && !_BufferFrame.Empty())
                                        {
                                            // 优化 4：预览降采。
                                            // 逻辑：如果未开启高清模式，且摄像头宽度大于设定的最大预览宽度，则进行缩小处理（防止放大）。
                                            int targetWidth = Math.Min(MaxPreviewWidth, _BufferFrame.Width);
                                            
                                            if (!IsHighDefinition && _BufferFrame.Width > targetWidth)
                                            {
                                                snapshot = new Mat();
                                                double ratio = (double)targetWidth / _BufferFrame.Width;
                                                int newHeight = (int)(_BufferFrame.Height * ratio);
                                                Cv2.Resize(_BufferFrame, snapshot, new OpenCvSharp.Size(targetWidth, newHeight), 0, 0, InterpolationFlags.Lanczos4);
                                            }
                                            else
                                            {
                                                snapshot = _BufferFrame.Clone();
                                            }
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
                        // Logger.LogInfo($"目录已创建：{directoryPath}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"创建目录时发生错误：{ex.Message}", ex);
                        return;
                    }
                }
                //开始计时
                _lastSaveTime = DateTime.Now;

                // 构建编码器尝试列表 - 优先硬件编码（参考OBS Studio策略）
                var encodersToTry = new System.Collections.Generic.List<string>();
                lock (_encoderLock)
                {
                    // 优先硬件编码，降低CPU占用
                    if (_globalEncoderPreference != EncoderPreference.ForceSoftware)
                    {
                        if (_useNvenc && _nvencSessionCount < MaxNvencSessions &&
                            !_failedEncoders.Contains("h264_nvenc") &&
                            !_thisCameraFailedEncoders.Contains("h264_nvenc"))
                        {
                            encodersToTry.Add("h264_nvenc");
                            Logger.LogInfo($"NVENC 可用，当前会话: {_nvencSessionCount}/{MaxNvencSessions}");
                        }
                        if (_useAmf && _amfSessionCount < MaxAmfSessions &&
                            !_failedEncoders.Contains("h264_amf") &&
                            !_thisCameraFailedEncoders.Contains("h264_amf"))
                        {
                            encodersToTry.Add("h264_amf");
                            Logger.LogInfo($"AMF 可用，当前会话: {_amfSessionCount}/{MaxAmfSessions}");
                        }
                        if (_useQsv && _qsvSessionCount < MaxQsvSessions &&
                            !_failedEncoders.Contains("h264_qsv") &&
                            !_thisCameraFailedEncoders.Contains("h264_qsv"))
                        {
                            encodersToTry.Add("h264_qsv");
                            Logger.LogInfo($"QSV 可用，当前会话: {_qsvSessionCount}/{MaxQsvSessions}");
                        }
                    }

                    // 软件编码作为回退（总是可用）
                    if (_useLibx264 && _globalEncoderPreference != EncoderPreference.PreferHardware)
                    {
                        encodersToTry.Add("libx264");
                    }
                    encodersToTry.Add("mpeg4");
                }

                bool started = false;
                foreach (var encoder in encodersToTry)
                {
                    string encoderArgs = encoder switch
                    {
                        // NVIDIA NVENC - 使用最简单参数，参考OBS
                        "h264_nvenc" => $"-c:v h264_nvenc",
                        // AMD AMF - 最简单参数
                        "h264_amf" => $"-c:v h264_amf",
                        // Intel QSV - 最简单参数
                        "h264_qsv" => $"-c:v h264_qsv",
                        // libx264 - 软件回退
                        "libx264" => $"-c:v libx264 -preset ultrafast -crf {Math.Clamp(Quality, 23, 28)}",
                        // mpeg4 - 最终回退
                        "mpeg4" => $"-c:v mpeg4 -threads 0 -q:v {Math.Clamp(Quality / 6, 2, 5)}",
                        _ => $"-c:v mpeg4 -threads 0 -q:v 3"
                    };

                    // 优化 1：更改输入像素格式为 yuv420p，减少 50% 的管道传输量，并消除 FFmpeg 内部的色彩转换负载
                    string fullArgs = $"-y -hide_banner -f rawvideo -pix_fmt yuv420p -s {(int)Width}x{(int)Height} -r {RecordFps} -i - {encoderArgs} -an \"{TargetFile}\"";

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
                        // Logger.LogInfo($"启动 FFmpeg ({encoder}): {fullArgs}");
                        _ffmpegProcess.Start();
                        _isProcessingVideo = true;

                        // 等待一小段时间，检查是否立即退出
                        // 500ms 确保 QSV 初始化有足够时间暴露错误
                        Thread.Sleep(500);

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

                            // 标记当前编码器不可用（仅本次会话，下次启动仍会尝试）
                            // 注意：不禁用全局，因为可能是临时问题
                            if (encoder == "h264_nvenc")
                            {
                                Logger.LogWarning("h264_nvenc (NVIDIA) 硬件编码启动失败，尝试下一个...");
                                continue;
                            }
                            if (encoder == "h264_amf")
                            {
                                Logger.LogWarning("h264_amf (AMD) 硬件编码启动失败，尝试下一个...");
                                continue;
                            }
                            if (encoder == "h264_qsv")
                            {
                                Logger.LogWarning("h264_qsv (Intel) 硬件编码启动失败，尝试下一个...");
                                continue;
                            }
                            if (encoder == "libx264")
                            {
                                Logger.LogWarning("libx264 软件编码启动失败，尝试 mpeg4...");
                                continue;
                            }
                            return; // mpeg4 也失败了，放弃
                        }

                        // 启动成功
                        lock (_encoderLock)
                        {
                            _currentEncoder = encoder;
                            if (encoder == "h264_nvenc")
                            {
                                _nvencSessionCount++;
                                if (!_nvencChecked) _nvencChecked = true;
                                Logger.LogInfo($"h264_nvenc (NVIDIA) 硬件编码启动成功，会话数: {_nvencSessionCount}/{MaxNvencSessions}");
                            }
                            else if (encoder == "h264_amf")
                            {
                                _amfSessionCount++;
                                if (!_amfChecked) _amfChecked = true;
                                Logger.LogInfo($"h264_amf (AMD) 硬件编码启动成功，会话数: {_amfSessionCount}/{MaxAmfSessions}");
                            }
                            else if (encoder == "h264_qsv")
                            {
                                _qsvSessionCount++;
                                if (!_qsvChecked) _qsvChecked = true;
                                Logger.LogInfo($"h264_qsv (Intel) 硬件编码启动成功，会话数: {_qsvSessionCount}/{MaxQsvSessions}");
                            }
                            else if (encoder == "libx264")
                            {
                                if (!_libx264Checked) _libx264Checked = true;
                                Logger.LogInfo("libx264 软件编码启动成功");
                            }
                            else if (encoder == "mpeg4")
                            {
                                Logger.LogInfo("mpeg4 软件编码启动成功");
                            }
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

                        lock (_encoderLock)
                        {
                            // 标记该编码器失败，本次运行不再尝试
                            _failedEncoders.Add(encoder);
                            _thisCameraFailedEncoders.Add(encoder);
                            Logger.LogWarning($"编码器 {encoder} 启动失败，添加到失败列表，尝试下一个...");
                        }
                        continue;
                    }
                }

                if (!started || _ffmpegProcess == null)
                    return;

                // 启动异步读取FFmpeg所有输出，记录到日志以便调试（前20行）
                _ffmpegErrorReadCts = new CancellationTokenSource();
                int logLineCount = 0;
                Task.Run(async () =>
                {
                    try
                    {
                        using (var reader = _ffmpegProcess.StandardError)
                        {
                            string line;
                            while ((line = await reader.ReadLineAsync()) != null &&
                                   !_ffmpegErrorReadCts.Token.IsCancellationRequested)
                            {
                                // 记录前20行和所有错误信息
                                if (!string.IsNullOrWhiteSpace(line))
                                {
                                    bool isError = line.Contains("error") || line.Contains("Error") ||
                                                   line.Contains("invalid") || line.Contains("Invalid") ||
                                                   line.Contains("cannot") || line.Contains("Cannot") ||
                                                   line.Contains("failed") || line.Contains("Failed");

                                    if (isError || logLineCount < 20)
                                    {
                                        Logger.LogInfo($"[FFmpeg] {line}");
                                        logLineCount++;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }, _ffmpegErrorReadCts.Token);

                // 解耦机制：初始化缓冲队列与独立的写磁盘线程
                _frameSaveQueue = new BlockingCollection<Mat>(180); // 扩大缓冲区由 90 -> 180，以缓冲磁盘 IO 抖动
                _ffmpegWriteCts = new CancellationTokenSource();
                _ffmpegWriteTask = Task.Run(() => FFmpegWriterLoop(_ffmpegWriteCts.Token), _ffmpegWriteCts.Token);

                // Logger.LogInfo($"FFmpeg 进程已成功启动 (PID: {_ffmpegProcess.Id})");
            }
        }

        private void FFmpegWriterLoop(CancellationToken token)
        {
            try
            {
                // 使用 GetConsumingEnumerable 配合 CancellationToken 实现优雅退出
                // 只要未调用 CompleteAdding() 且未取消 token，循环就会持续等待
                foreach (var frame in _frameSaveQueue.GetConsumingEnumerable(token))
                {
                    try
                    {
                        SaveFrame(frame); 
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("FFmpeg 写入循环发生块级处理错误", ex);
                    }
                    finally
                    {
                        frame?.Dispose(); 
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 任务由于 Cancellation侦听 而正常退出
            }
            catch (ObjectDisposedException)
            {
                // 极罕见情况下集合已被快速销毁，视为正常退出
            }
            catch (Exception ex)
            {
                Logger.LogError("FFmpeg 后端录制独立任务中发生严重异常崩溃", ex);
            }
            finally
            {
                // ★ 关键：后台线程是唯一负责排水和最后清理的角色
                // 此处逻辑确保即便是任务被强制叫停，内存残留的 Mat 也会被彻底释放
                var queueForCleanup = _frameSaveQueue;
                if (queueForCleanup != null)
                {
                    while (queueForCleanup.TryTake(out Mat remainingFrame))
                    {
                        remainingFrame?.Dispose();
                    }
                    // 只有在循环彻底结束后，由后台线程自己销毁集合，防止主线程 Dispose 导致的竞态崩溃
                    try { queueForCleanup.Dispose(); } catch { }
                }
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
            // 0. 释放硬件编码器会话计数
            lock (_encoderLock)
            {
                if (!string.IsNullOrEmpty(_currentEncoder))
                {
                    if (_currentEncoder == "h264_nvenc" && _nvencSessionCount > 0)
                    {
                        _nvencSessionCount--;
                        Logger.LogInfo($"释放 NVENC 会话，剩余: {_nvencSessionCount}/{MaxNvencSessions}");
                    }
                    else if (_currentEncoder == "h264_amf" && _amfSessionCount > 0)
                    {
                        _amfSessionCount--;
                        Logger.LogInfo($"释放 AMF 会话，剩余: {_amfSessionCount}/{MaxAmfSessions}");
                    }
                    else if (_currentEncoder == "h264_qsv" && _qsvSessionCount > 0)
                    {
                        _qsvSessionCount--;
                        Logger.LogInfo($"释放 QSV 会话，剩余: {_qsvSessionCount}/{MaxQsvSessions}");
                    }
                    _currentEncoder = "";
                }
            }

            // 1. 发出停止信号：阻断外部新采集帧排队
            if (_frameSaveQueue != null && !_frameSaveQueue.IsAddingCompleted)
            {
                try { _frameSaveQueue.CompleteAdding(); } catch { }
            }

            // 2. 发出取消信号并等待后台写入线程同步存盘（最多给 1 秒宽限）
            if (_ffmpegWriteCts != null)
            {
                _ffmpegWriteCts.Cancel();
                try 
                { 
                    // 确保等待任务结束，但不直接 Dispose 队列
                    _ffmpegWriteTask?.Wait(1000); 
                } 
                catch { }
                _ffmpegWriteCts.Dispose();
                _ffmpegWriteCts = null;
            }

            // 3. 将引用置空，具体的 Dispose 逻辑交给 FFmpegWriterLoop 的 finally 块
            _frameSaveQueue = null;
            _ffmpegWriteTask = null;

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
                // Logger.LogInfo("FFmpeg 重新初始化成功");
            }

            // 每100帧检查一次磁盘空间（避免频繁检查）
            if (_frameCount % 100 == 0)
            {
                availableSpace = CalcDiskSpaceEnough();

                // 检查是否低于阈值
                if (availableSpace < DiskThreshold)
                {
                    Logger.LogWarning($"磁盘空间不足: {availableSpace}MB < {DiskThreshold}MB");

                    // 根据配置决定是否覆盖旧文件
                    if (OverwriteOldFiles)
                    {
                        // Logger.LogInfo("开始清理旧文件以释放空间 ...");

                        // 目标空间：设置一个安全缓冲 (阈值 + 500MB)
                        // 这样可以一次性清理出足够的空间，防止程序在临界值边缘频繁执行删除和检测操作
                        long targetSpace = DiskThreshold + 500;

                        // 调用全局并发安全的批处理清理（取代了原本导致堵塞的死循环）
                        availableSpace = BatchCleanUpDiskSpace(targetSpace, availableSpace);

                        // 清理循环结束后，再次确认是否甚至低于最低危急阈值
                        if (availableSpace < DiskThreshold)
                        {
                            Logger.LogError("清理所有旧文件后空间仍不足，停止录制");

                            // 触发事件通知用户，增加全局限制：10秒内只弹一次框，防止多摄像头同时抛出十几个弹框叠在一起
                            if ((DateTime.Now - _lastDiskSpaceWarningTime).TotalSeconds > 10)
                            {
                                _lastDiskSpaceWarningTime = DateTime.Now;
                                try
                                {
                                    DiskSpaceLow?.Invoke(this, $"清理所有旧文件后空间仍不足，录像已停止: {availableSpace}MB");
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError("触发 DiskSpaceLow 事件时发生错误", ex);
                                }
                            }

                            StopSave();
                            return;
                        }
                    }
                    else
                    {
                        Logger.LogWarning("磁盘空间不足且未启用覆盖旧文件，停止录制");

                        if ((DateTime.Now - _lastDiskSpaceWarningTime).TotalSeconds > 10)
                        {
                            _lastDiskSpaceWarningTime = DateTime.Now;
                            // 触发事件通知用户
                            try
                            {
                                DiskSpaceLow?.Invoke(this, $"磁盘空间不足且未启用覆盖旧文件，录像已停止: {availableSpace}MB");
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError("触发 DiskSpaceLow 事件时发生错误", ex);
                            }
                        }

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
            Mat yuvFrame = null;
            try
            {
                // 优化 2：在写入管道前进行 YUV 预转换。
                yuvFrame = new Mat();
                Cv2.CvtColor(frame, yuvFrame, ColorConversionCodes.BGR2YUV_I420);

                // 使用 ArrayPool 提高性能，避免频繁 GC
                int frameSize = (int)yuvFrame.Total() * yuvFrame.ElemSize();
                rentedArray = ArrayPool<byte>.Shared.Rent(frameSize);
                Marshal.Copy(yuvFrame.Data, rentedArray, 0, frameSize);

                // 增加写保护判定
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited && _ffmpegProcess.StandardInput.BaseStream.CanWrite)
                {
                    _ffmpegProcess.StandardInput.BaseStream.Write(rentedArray, 0, frameSize);
                    _ffmpegProcess.StandardInput.BaseStream.Flush();
                    _frameCount++;
                }
                else
                {
                    Logger.LogWarning($"摄像头 {Index}: FFmpeg 管道不可写，跳过此帧。进程状态: {(_ffmpegProcess == null ? "Null" : "已退出")}");
                }
            }
            catch (IOException ioEx)
            {
                Logger.LogError("写入 FFmpeg 失败（管道可能已关闭）", ioEx);

                // 如果正在使用硬件编码且进程已崩溃，添加到失败列表
                if (_ffmpegProcess != null && _ffmpegProcess.HasExited)
                {
                    if (!string.IsNullOrEmpty(_currentEncoder))
                    {
                        lock (_encoderLock)
                        {
                            _failedEncoders.Add(_currentEncoder);
                            _thisCameraFailedEncoders.Add(_currentEncoder);
                        }
                        Logger.LogWarning($"摄像头 {Index}: {_currentEncoder} 运行时崩溃，添加到失败列表，将尝试其他编码器");
                    }
                }

                // FFmpeg 进程可能已崩溃，清理死掉的进程，标记需要重新初始化
                FinalizeFfmpeg();
                _needReinitFFmpeg = true;
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
                // 优化 3：销毁临时的 YUV 帧，防止内存泄露
                yuvFrame?.Dispose();
            }

            // 检查是否需要切割视频文件
            if (DateTime.Now - _lastSaveTime >= SaveInterval)
            {
                // Logger.LogInfo($"达到切割间隔 ({SaveInterval})，准备创建新文件...");
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

        private static readonly object _diskCleanupLock = new object();
        private static DateTime _lastDiskSpaceWarningTime = DateTime.MinValue;

        private long BatchCleanUpDiskSpace(long targetSpace, long currentAvailableSpace)
        {
            // 全局并发防护：防止多个摄像头同时发现硬盘满了而同一秒发起上万次全盘搜索洗盘操作
            lock (_diskCleanupLock)
            {
                // 加锁后，再次测量真实剩余容量，可能刚才前一个拿到锁的摄像头已经帮大家把地洗干净了
                currentAvailableSpace = CalcDiskSpaceEnough();
                if (currentAvailableSpace >= targetSpace)
                {
                    return currentAvailableSpace;
                }

                if (!Directory.Exists(saveDirectory))
                {
                    return currentAvailableSpace;
                }

                // Logger.LogInfo($"触发底层全局批清理任务 (目标空间: {targetSpace}MB)...");
                try
                {
                    // [1] 只执行一次极为昂贵的硬盘全盘检索和按时间排序操作！
                    var allMp4Files = Directory.GetFiles(saveDirectory, "*.mp4", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .OrderBy(fi => fi.LastWriteTime)
                        .ToList();

                    int deletedCount = 0;
                    long deletedSpaceMB = 0;

                    // [2] 按时间从老到新循环处理
                    foreach (var fileInfo in allMp4Files)
                    {
                        if (currentAvailableSpace >= targetSpace)
                        {
                            break; // 达标即平稳退出
                        }

                        try
                        {
                            if (File.Exists(fileInfo.FullName))
                            {
                                long fileSizeMB = fileInfo.Length / (1024 * 1024);
                                if (fileSizeMB < 1) fileSizeMB = 1;

                                fileInfo.Delete(); // 发起物理删除
                                deletedCount++;
                                deletedSpaceMB += fileSizeMB;
                                
                                // 软统计增加可用空间记录
                                currentAvailableSpace += fileSizeMB;

                                // 清理附带空包骨架(空文件夹)
                                var parentDir = fileInfo.Directory;
                                if (parentDir != null && parentDir.Exists && (parentDir.GetFiles().Length == 0 && parentDir.GetDirectories().Length == 0))
                                {
                                    try { parentDir.Delete(); } catch { }
                                }
                            }
                        }
                        catch (IOException)
                        {
                            // 忽略被占用的文件
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"批清理删除个别文件失败: {fileInfo.FullName}", ex);
                        }
                    }

                    if (deletedCount > 0)
                    {
                        Logger.LogInfo($"磁盘清理完成: 共删除 {deletedCount} 个过期视频文件，释放空间约 {deletedSpaceMB}MB");
                    }
                    else if (currentAvailableSpace < targetSpace)
                    {
                        Logger.LogWarning($"磁盘空间不足且无可清理的视频文件 (*.mp4)。当前可用: {currentAvailableSpace}MB, 目标: {targetSpace}MB");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("全局批量拉取旧视频时发生越权或其他异常", ex);
                }

                // 最终重判一次磁盘真实水位线
                return CalcDiskSpaceEnough();
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
                        // 清空所有其他订阅者（需判断是否已经被清空为 null）
                        if (FrameChanged != null)
                        {
                            foreach (var d in FrameChanged.GetInvocationList())
                            {
                                FrameChanged -= (FrameChangedEventHandler)d;
                            }
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


                    // 释放积压的写对象队列防止泄露
                    if (_frameSaveQueue != null)
                    {
                        _frameSaveQueue.CompleteAdding();
                        while (_frameSaveQueue.TryTake(out Mat f)) f?.Dispose();
                        _frameSaveQueue.Dispose();
                        _frameSaveQueue = null;
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
