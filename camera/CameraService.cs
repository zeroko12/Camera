using OpenCvSharp;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;
using camera.Pipeline;
using camera.Pipeline.Processors;
using camera.Preview;
using camera.Recording;

namespace camera
{
    public class CameraService : BindableBase, IDisposable
    {
        #region 私有核心组件

        private VideoCapture cap;
        private Task _captureTask;
        private double FpsOrigin = 15, WidthOrigin = 1280, HeightOrigin = 720;
        private volatile bool _isCapturing = false;
        private int _continuousReadFailCount = 0;
        private long _frameCount = 0;
        private DateTime _lastWarningFrameTime = DateTime.MinValue;
        private DateTime _lastPipelineProcessTime = DateTime.MinValue;

        private IFramePipeline _pipeline;
        private PreviewController _previewController;
        private FFmpegVideoRecorder _recorder;
        private RecordingOptions _recordingOptions;
        private System.Windows.Threading.DispatcherTimer _uiTimer;
        // 帧拉取模型:capture 线程写 _latestFrame,UI 线程定时器拉取。
// 注意:此处不再创建 DispatcherTimer,改由 MainWindowViewModel.SharedFrameTimer
// 统一调度(1 个定时器而非 4 个),且拉取频率与 PreviewFps 对齐避免无意义操作。
        private volatile Mat _latestFrame;
        public Mat PullLatestFrame()
        {
            return Interlocked.Exchange(ref _latestFrame, null);
        }

        /// <summary>
        /// 由 MainWindowViewModel.SharedFrameTimer 在 UI 线程统一调用。
        /// 拉取最新 Mat,若非空则赋给 Frame 属性(触发 binding cascade)。
        /// 比每个 CameraService 单独跑 DispatcherTimer 节省 3 个定时器 +
        /// 所有相机共享 1 个 DispatcherOperation 入队点。
        /// </summary>
        public void PullAndApplyLatestFrame()
        {
            var pending = Interlocked.Exchange(ref _latestFrame, null);
            if (pending != null) Frame = pending;
        }

        // 管道内特定的图像处理算子
        private ClaheProcessor _claheProcessor;
        private ContrastLuminanceProcessor _contrastLuminanceProcessor;
        private TimestampOverlayProcessor _timestampOverlayProcessor;
        private RecordingProcessor _recordingProcessor;

        #endregion

        #region 锁和时间统计

        private readonly object _lockObject = new();
        private static readonly object _diskCleanupLock = new();
        private static DateTime _lastDiskSpaceWarningTime = DateTime.MinValue;
        private readonly Stopwatch _fpsTimer = new();

        #endregion

        #region 公共绑定属性（向后兼容 MainWindowViewModel.cs）

        private int _Quality = 20;
        public int Quality
        {
            get => _Quality;
            set
            {
                if (SetProperty(ref _Quality, value))
                {
                    // 将压缩质量同步传递给录制器，使其在下次分段切换时生效
                    if (_recorder != null)
                        _recorder.CurrentQuality = value;
                }
            }
        }

        private int _PreviewFps = 8;
        public int PreviewFps
        {
            get => _PreviewFps;
            set
            {
                int maxFps = Fps > 0 ? (int)Fps : 30;
                int clamped = Math.Clamp(value, 1, maxFps);
                if (SetProperty(ref _PreviewFps, clamped))
                {
                    if (_previewController != null)
                        _previewController.TargetFps = clamped;
                }
            }
        }

        private int _RecordFps = 10;
        public int RecordFps
        {
            get => _RecordFps;
            set
            {
                int maxFps = Fps > 0 ? (int)Fps : 30;
                int clamped = Math.Clamp(value, 1, maxFps);
                if (SetProperty(ref _RecordFps, clamped))
                {
                    if (_recordingOptions != null)
                        _recordingOptions.RecordFps = clamped;

                    if (Index >= 0)
                    {
                        ConfigurationManager.SaveSetting($"Camera_{GetConfigKey()}_RecordFps", clamped.ToString());
                    }

                    if (isSave)
                    {
                        System.Threading.Tasks.Task.Run(() => RestartSaving());
                    }
                }
            }
        }

        private bool _isSave = false;
        public bool isSave
        {
            get => _isSave;
            set
            {
                if (_isSave != value)
                {
                    SetProperty(ref _isSave, value);
                    OnIsSaveChanged();
                }
            }
        }

        private double _Fps;
        public double Fps
        {
            get => _Fps;
            set => SetProperty(ref _Fps, value);
        }

        private double _Width;
        public double Width
        {
            get => _Width;
            set => SetProperty(ref _Width, value);
        }

        private double _Height;
        public double Height
        {
            get => _Height;
            set => SetProperty(ref _Height, value);
        }

        private Mat _Frame = new();
        public Mat Frame
        {
            get => _Frame;
            set
            {
                // 先在 setter 入口抓住旧引用,SetProperty 之后 _Frame 已指向新值。
                Mat oldFrame = _Frame;
                if (SetProperty(ref _Frame, value) && oldFrame != null && oldFrame != value)
                {
                    // MatPool 内部使用 ConcurrentDictionary + ConcurrentQueue,完全线程安全,
                    // 直接同步归还即可。原先 Task.Run 包装是为了避开 UI 线程 native 释放,
                    // 但 MatPool.Return 只是把 Mat 塞回队列或直接 Dispose,
                    // 真正的 P/Invoke 仅在池满时触发(每规格缓存 8 张),概率极低。
                    try { Infrastructure.MatPool.Return(oldFrame); }
                    catch (Exception ex) { Logger.LogError($"[CameraService #{Index}] 回收旧 Frame Mat 异常", ex); }
                }
            }
        }

        private string _currentTimeText = "";
        public string CurrentTimeText
        {
            get => _currentTimeText;
            set => SetProperty(ref _currentTimeText, value);
        }

        private string _Name = "test";
        public string Name
        {
            get => _Name;
            set => SetProperty(ref _Name, value);
        }

        private string _devicePath = "";
        public string DevicePath
        {
            get => _devicePath;
            set
            {
                if (SetProperty(ref _devicePath, value))
                {
                    // 设备路径变更 → 拓扑缓存失效
                    InvalidateUsbSpeedCache();
                }
            }
        }

        private int _Index = -1;
        public int Index
        {
            get => _Index;
            set => SetProperty(ref _Index, value);
        }

        public static string GetConfigKey(string devicePath, int index)
        {
            if (string.IsNullOrEmpty(devicePath))
            {
                return index.ToString();
            }

            // Stable DJB2 hash algorithm to get a stable, XML-safe key
            uint hash = 5381;
            foreach (char c in devicePath)
            {
                hash = ((hash << 5) + hash) + c;
            }
            return "Dev_" + hash.ToString("X");
        }

        public string GetConfigKey()
        {
            return GetConfigKey(DevicePath, Index);
        }

        public string saveDirectory = "SavedVideos";

        private TimeSpan _saveInterval = TimeSpan.FromMinutes(1);
        public TimeSpan SaveInterval
        {
            get => _saveInterval;
            set
            {
                if (SetProperty(ref _saveInterval, value))
                {
                    if (_recordingOptions != null)
                        _recordingOptions.SegmentDuration = value;
                }
            }
        }

        private long _diskThreshold = 1024;
        public long DiskThreshold
        {
            get => _diskThreshold;
            set
            {
                if (_diskThreshold != value)
                {
                    SetProperty(ref _diskThreshold, value);
                    Debug.WriteLine($"设置{Index}磁盘阈值:{DiskThreshold}");
                }
            }
        }

        private double contrast = 1.0;
        public double Contrast
        {
            get => contrast;
            set
            {
                if (SetProperty(ref contrast, value))
                {
                    if (_contrastLuminanceProcessor != null)
                        _contrastLuminanceProcessor.Contrast = value;
                }
            }
        }

        private double luminance = 0.0;
        public double Luminance
        {
            get => luminance;
            set
            {
                if (SetProperty(ref luminance, value))
                {
                    if (_contrastLuminanceProcessor != null)
                        _contrastLuminanceProcessor.Luminance = value;
                }
            }
        }

        private bool _IsGrayHomogenization = false;
        public bool IsGrayHomogenization
        {
            get => _IsGrayHomogenization;
            set
            {
                if (SetProperty(ref _IsGrayHomogenization, value))
                {
                    if (_claheProcessor != null)
                        _claheProcessor.Enabled = value;
                }
            }
        }

        private bool _IsHighDefinition = false;
        public bool IsHighDefinition
        {
            get => _IsHighDefinition;
            set
            {
                if (SetProperty(ref _IsHighDefinition, value))
                {
                    if (_previewController != null)
                        _previewController.IsHighDefinition = value;
                }
            }
        }

        private int _MaxPreviewWidth = 1080;
        public int MaxPreviewWidth
        {
            get => _MaxPreviewWidth;
            set
            {
                if (SetProperty(ref _MaxPreviewWidth, value))
                {
                    if (_previewController != null)
                        _previewController.MaxWidth = value;
                }
            }
        }

        private bool _overwriteOldFiles = true;
        public bool OverwriteOldFiles
        {
            get => _overwriteOldFiles;
            set
            {
                if (_overwriteOldFiles != value)
                {
                    SetProperty(ref _overwriteOldFiles, value);
                    Debug.WriteLine($"设置{Index}保存模式为{OverwriteOldFiles}");
                }
            }
        }

        private System.Collections.ObjectModel.ObservableCollection<string> _supportedResolutions = new();
        public System.Collections.ObjectModel.ObservableCollection<string> SupportedResolutions
        {
            get => _supportedResolutions;
            set => SetProperty(ref _supportedResolutions, value);
        }

        private System.Collections.ObjectModel.ObservableCollection<string> _supportedRecordResolutions = new();
        public System.Collections.ObjectModel.ObservableCollection<string> SupportedRecordResolutions
        {
            get => _supportedRecordResolutions;
            set => SetProperty(ref _supportedRecordResolutions, value);
        }

        private string _selectedRecordResolution = "与采集分辨率一致";
        public string SelectedRecordResolution
        {
            get => _selectedRecordResolution;
            set
            {
                if (SetProperty(ref _selectedRecordResolution, value) && !string.IsNullOrEmpty(value))
                {
                    if (Index >= 0)
                    {
                        ConfigurationManager.SaveSetting($"Camera_{GetConfigKey()}_RecordResolution", value);
                    }
                    if (isSave)
                    {
                        System.Threading.Tasks.Task.Run(() => RestartSaving());
                    }
                }
            }
        }

        private void RestartSaving()
        {
            lock (_lockObject)
            {
                if (!isSave) return;
                Logger.LogInfo($"[CameraService #{Index}] 正在重启录像以应用新的录像参数...");
                StopSaving();
                StartSaving();
            }
        }

        private bool _isUpdatingResolutionProgrammatically = false;
        private string _selectedResolution = "";
        public string SelectedResolution
        {
            get => _selectedResolution;
            set
            {
                if (string.IsNullOrEmpty(value) || value == _selectedResolution) return;

                string oldRes = _selectedResolution;

                // 若非程序自动恢复/赋值，且原先已有有效分辨率，则尝试拦截并进行 USB 带宽风险检测
                if (!_isUpdatingResolutionProgrammatically && !string.IsNullOrEmpty(oldRes))
                {
                    bool hasRisk = Infrastructure.UsbTopologyHelper.PredictResolutionRisk(
                        DevicePath,
                        oldRes,
                        value,
                        activeCameraCount: 4,
                        currentTotalMbPerSec: EstimatedMbPerSec,
                        out string warningTitle,
                        out string warningMessage);

                    if (hasRisk && !string.IsNullOrEmpty(warningMessage))
                    {
                        var result = System.Windows.MessageBox.Show(
                            warningMessage,
                            warningTitle ?? "⚠️ USB 带宽超限预警",
                            System.Windows.MessageBoxButton.OKCancel,
                            System.Windows.MessageBoxImage.Warning);

                        if (result != System.Windows.MessageBoxResult.OK)
                        {
                            Logger.LogInfo($"[CameraService #{Index}] 用户取消了向高风险分辨率 [{value}] 的切换，保留原分辨率 [{oldRes}]");
                            _isUpdatingResolutionProgrammatically = true;
                            RaisePropertyChanged(nameof(SelectedResolution));
                            _isUpdatingResolutionProgrammatically = false;
                            return;
                        }
                    }
                }

                if (SetProperty(ref _selectedResolution, value) && !string.IsNullOrEmpty(value))
                {
                    var parts = value.Split('x');
                    if (parts.Length == 2 && double.TryParse(parts[0], out double w) && double.TryParse(parts[1], out double h))
                    {
                        Width = w;
                        Height = h;
                        RaisePropertyChanged(nameof(EstimatedMbPerSec));
                        System.Threading.Tasks.Task.Run(() => ChangeCameraInfo());
                    }
                }
            }
        }

        private double _overlayTextSize = 0.3;
        public double OverlayTextSize
        {
            get => _overlayTextSize;
            set
            {
                if (SetProperty(ref _overlayTextSize, value))
                {
                    if (_timestampOverlayProcessor != null)
                        _timestampOverlayProcessor.FontScale = value;

                    if (_recordingOptions != null)
                        _recordingOptions.OverlayTextSize = value;
                }
            }
        }

        private string _customOverlayText = "";
        public string CustomOverlayText
        {
            get => _customOverlayText;
            set
            {
                if (SetProperty(ref _customOverlayText, value))
                {
                    if (_timestampOverlayProcessor != null)
                        _timestampOverlayProcessor.CustomText = value;

                    if (_recordingOptions != null)
                        _recordingOptions.CustomText = value;

                    // 实时保存自定义水印文字内容
                    if (Index >= 0)
                    {
                        ConfigurationManager.SaveSetting($"Camera_{GetConfigKey()}_CustomText", value ?? "");
                    }

                    if (isSave)
                    {
                        System.Threading.Tasks.Task.Run(() => RestartSaving());
                    }
                }
            }
        }

        public string ActiveCodec => _recorder?.ActiveCodec ?? "None";

        public bool IsCodecDowngraded
        {
            get
            {
                if (_recorder == null || !isSave) return false;
                string codec = ActiveCodec.ToLower();
                return codec.Contains("libx264") || codec.Contains("mpeg4") || codec == "none" || codec == "";
            }
        }

        private bool _isPreviewEnabled = true;
        public bool IsPreviewEnabled
        {
            get => _isPreviewEnabled;
            set
            {
                if (SetProperty(ref _isPreviewEnabled, value))
                {
                    if (_previewController != null)
                        _previewController.IsEnabled = value;
                }
            }
        }

        private bool _isManualDisabled = false;
        public bool IsManualDisabled
        {
            get => _isManualDisabled;
            set
            {
                if (SetProperty(ref _isManualDisabled, value))
                {
                    RaisePropertyChanged(nameof(IsConnected));
                    EnableCameraCommand?.RaiseCanExecuteChanged();
                    DisableCameraCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsConnected => !_isManualDisabled && _continuousReadFailCount <= 5;

        public DelegateCommand EnableCameraCommand { get; private set; }
        public DelegateCommand DisableCameraCommand { get; private set; }

        public void EnableCamera()
        {
            lock (_lockObject)
            {
                try
                {
                    Logger.LogInfo($"[CameraService #{Index}] 用户手动开启摄像头 [{Name}]");
                    IsManualDisabled = false;
                    _continuousReadFailCount = 0;

                    // 异步重新握手设备与开启采集流
                    System.Threading.Tasks.Task.Run(() => ChangeCameraInfo());

                    RaisePropertyChanged(nameof(IsConnected));
                    EnableCameraCommand?.RaiseCanExecuteChanged();
                    DisableCameraCommand?.RaiseCanExecuteChanged();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[CameraService #{Index}] 手动开启摄像头发生异常", ex);
                }
            }
        }

        public void DisableCamera()
        {
            lock (_lockObject)
            {
                try
                {
                    Logger.LogInfo($"[CameraService #{Index}] 用户手动关闭摄像头 [{Name}]");
                    IsManualDisabled = true;

                    _isCapturing = false;

                    // 等待采集任务结束
                    if (_captureTask != null)
                    {
                        try
                        {
                            _captureTask.Wait(1000);
                        }
                        catch { }
                    }

                    // 若正在保存录像，停止保存
                    if (isSave)
                    {
                        StopSaving();
                    }

                    // 彻底释放 VideoCapture 句柄
                    if (cap != null)
                    {
                        var capToDispose = cap;
                        cap = null;
                        try
                        {
                            if (capToDispose.IsOpened())
                            {
                                capToDispose.Release();
                            }
                            capToDispose.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning($"[CameraService #{Index}] 释放 VideoCapture 句柄异常: {ex.Message}");
                        }
                    }

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Infrastructure.DeviceLockManager.ReleaseDevice(DevicePath, Index);

                    // 推送已关闭警告帧
                    PushWarningFrame("通道已关闭\n(右键可重新开启)");

                    RaisePropertyChanged(nameof(IsConnected));
                    EnableCameraCommand?.RaiseCanExecuteChanged();
                    DisableCameraCommand?.RaiseCanExecuteChanged();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[CameraService #{Index}] 手动关闭摄像头发生异常", ex);
                }
            }
        }

        // ★ Fix A: UsbSpeedDescription 60 秒缓存
        // 证据:counters 数据 + 静态分析。UsbSpeedDescription 在每次 UpdateActiveCodecStatus 都被读取,
        // 内部触发 cfgmgr32.dll P/Invoke (CM_Locate_DevNodeW / CM_Get_DevNode_PropertyW)。
        // 缓存后,每路 camera 每分钟最多 1 次 P/Invoke,降低 60 倍 sys call + 反射开销。
        private string _cachedUsbSpeedDescription;
        private long _usbSpeedCacheTicks;
        private const long UsbSpeedCacheTtlTicks = 60L * TimeSpan.TicksPerSecond; // 60 秒 TTL

        public string UsbSpeedDescription
        {
            get
            {
                long now = DateTime.UtcNow.Ticks;
                long cached = Interlocked.Read(ref _usbSpeedCacheTicks);
                if (!string.IsNullOrEmpty(_cachedUsbSpeedDescription) && (now - cached) < UsbSpeedCacheTtlTicks)
                {
                    return _cachedUsbSpeedDescription;
                }
                var topo = Infrastructure.UsbTopologyHelper.GetTopologyInfo(DevicePath, Name);
                _cachedUsbSpeedDescription = topo.SpeedDescription;
                Interlocked.Exchange(ref _usbSpeedCacheTicks, now);
                return _cachedUsbSpeedDescription;
            }
        }

        /// <summary>
        /// 当 devicePath 改变时(重新打开相机),清空 USB 拓扑缓存。
        /// </summary>
        private void InvalidateUsbSpeedCache()
        {
            _cachedUsbSpeedDescription = null;
            Interlocked.Exchange(ref _usbSpeedCacheTicks, 0);
        }

        public double EstimatedMbPerSec
        {
            get
            {
                if (!IsConnected || Width <= 0 || Height <= 0) return 0;
                double activeFps = Fps > 0 ? Fps : 30;
                return Infrastructure.UsbTopologyHelper.CalculateEstimatedMbPerSec((int)Width, (int)Height, activeFps, _preferredFormat.ToString());
            }
        }

        public void UpdateActiveCodecStatus()
        {
            RaisePropertyChanged(nameof(ActiveCodec));
            RaisePropertyChanged(nameof(IsCodecDowngraded));
            RaisePropertyChanged(nameof(UsbSpeedDescription));
            RaisePropertyChanged(nameof(EstimatedMbPerSec));
        }

        #endregion

        #region 事件定义

        public delegate void FrameChangedEventHandler(Mat frame);
        public event FrameChangedEventHandler FrameChanged;

        public event EventHandler<string> DiskSpaceLow;

        #endregion

        #region 摄像头采集格式

        private enum CaptureFormat { MJPG, YUY2, NV12 }
        private CaptureFormat _preferredFormat = CaptureFormat.MJPG;
        private bool _formatChecked = false;

        #endregion

        public CameraService()
        {
            EnableCameraCommand = new DelegateCommand(EnableCamera, () => IsManualDisabled || !IsConnected);
            DisableCameraCommand = new DelegateCommand(DisableCamera, () => !IsManualDisabled && IsConnected);
            InitializeModules();
        }

        private void InitializeModules()
        {
            // 1. 初始化模块参数与流水线
            _claheProcessor = new ClaheProcessor { Enabled = IsGrayHomogenization, Order = 10 };
            _contrastLuminanceProcessor = new ContrastLuminanceProcessor { Contrast = Contrast, Luminance = Luminance, Order = 20 };
            _timestampOverlayProcessor = new TimestampOverlayProcessor { Enabled = false, Order = 30 };
            _recordingProcessor = new RecordingProcessor { Enabled = true, Order = 90 };

            _pipeline = FramePipelineBuilder.Create()
                .AddProcessor(_claheProcessor)
                .AddProcessor(_contrastLuminanceProcessor)
                .AddProcessor(_timestampOverlayProcessor)
                .AddProcessor(_recordingProcessor)
                .Build();

            // 2. 初始化预览控制器
            _previewController = new PreviewController
            {
                TargetFps = PreviewFps,
                MaxWidth = MaxPreviewWidth,
                IsHighDefinition = IsHighDefinition,
                IsEnabled = true
            };

            // 订阅预览帧回调(只写共享槽位,不调 BeginInvoke)
            _previewController.PreviewFrameReady += (s, previewFrame) =>
            {
                if (Application.Current == null)
                {
                    if (previewFrame != null) Infrastructure.MatPool.Return(previewFrame);
                    return;
                }

                // 用 Interlocked.Exchange 替换共享槽位中的 Mat。
                // 若上一帧还没被 UI 线程拉走,直接同步回收(MatPool 线程安全)。
                var previous = Interlocked.Exchange(ref _latestFrame, previewFrame);
                if (previous != null)
                {
                    try { Infrastructure.MatPool.Return(previous); }
                    catch (Exception ex) { Logger.LogError($"[CameraService #{Index}] 回收预览旧帧异常", ex); }
                }
            };

            // 共享帧拉取定时器由 MainWindowViewModel 统一管理(CameraService 不再单独创建 DispatcherTimer)。
            // 原 4 个 per-camera 定时器 × 30 fps = 120 DispatcherOperation 入队/秒,
            // 改为 1 个 × PreviewFps = 8-15 DispatcherOperation /秒,显著降低 UI 线程负载。

            // 4. 初始化 UI 水印更新定时器
            _uiTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Normal,
                System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);
            _uiTimer.Interval = TimeSpan.FromSeconds(1);
            _uiTimer.Tick += (s, e) => { CurrentTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); };
            _uiTimer.Start();
            CurrentTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 3. 初始化录像选项及录制器
            _recordingOptions = new RecordingOptions
            {
                CameraIndex = Index,
                RecordFps = RecordFps,
                SaveDirectory = saveDirectory,
                SegmentDuration = SaveInterval
            };
            
            _recorder = new FFmpegVideoRecorder(_recordingOptions);
            _recordingProcessor.Recorder = _recorder;
        }

        private static string DecodeFourCC(double fourcc)
        {
            int val = (int)fourcc;
            if (val == 0) return "NONE";
            char[] chars = new char[4];
            chars[0] = (char)(val & 0xFF);
            chars[1] = (char)((val >> 8) & 0xFF);
            chars[2] = (char)((val >> 16) & 0xFF);
            chars[3] = (char)((val >> 24) & 0xFF);
            return new string(chars).Trim('\0').Trim();
        }

        public int getCameraNum()
        {
            return 1;
        }

        private (int Width, int Height) FindClosestResolutionTo720p()
        {
            int targetW = 1280;
            int targetH = 720;
            int bestW = (int)WidthOrigin;
            int bestH = (int)HeightOrigin;
            double minDistance = double.MaxValue;

            foreach (var resStr in SupportedResolutions)
            {
                var parts = resStr.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                {
                    double dist = Math.Pow(w - targetW, 2) + Math.Pow(h - targetH, 2);
                    
                    // 如果该分辨率尺寸大于 720p（向上取），给予 1.5 倍距离惩罚，优先向下取更节省带宽的分辨率
                    if (w > targetW || h > targetH)
                    {
                        dist *= 1.5;
                    }

                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        bestW = w;
                        bestH = h;
                    }
                }
            }

            return (bestW, bestH);
        }

        private void ShowVideoInfo()
        {
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

            double defaultFps = cap.Get(VideoCaptureProperties.Fps);
            double defaultWidth = cap.Get(VideoCaptureProperties.FrameWidth);
            double defaultHeight = cap.Get(VideoCaptureProperties.FrameHeight);

            Logger.LogInfo($"摄像头默认配置: {defaultWidth}x{defaultHeight} @ {defaultFps}fps");

            // 清理旧的分辨率列表并确保 UI 线程安全
            Application.Current?.Dispatcher.Invoke(() =>
            {
                SupportedResolutions.Clear();
                SupportedRecordResolutions.Clear();
                SupportedRecordResolutions.Add("与采集分辨率一致");
            });

            foreach (var (width, height) in testResolutions)
            {
                try
                {
                    cap.Set(VideoCaptureProperties.FrameWidth, width);
                    cap.Set(VideoCaptureProperties.FrameHeight, height);
                    if (_preferredFormat == CaptureFormat.MJPG)
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                    else if (_preferredFormat == CaptureFormat.YUY2)
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('Y', 'U', 'Y', '2'));
                    else
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('N', 'V', '1', '2'));
                    Thread.Sleep(30);

                    double actualWidth = cap.Get(VideoCaptureProperties.FrameWidth);
                    double actualHeight = cap.Get(VideoCaptureProperties.FrameHeight);
                    double actualFps = cap.Get(VideoCaptureProperties.Fps);

                    if (Math.Abs(actualWidth - width) < 10 && Math.Abs(actualHeight - height) < 10)
                    {
                        string resStr = $"{width}x{height}";
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            if (!SupportedResolutions.Contains(resStr))
                                SupportedResolutions.Add(resStr);
                            if (!SupportedRecordResolutions.Contains(resStr))
                                SupportedRecordResolutions.Add(resStr);
                        });

                        if (bestWidth == 0 || bestHeight == 0)
                        {
                            bestWidth = (int)actualWidth;
                            bestHeight = (int)actualHeight;
                            bestFps = actualFps > 0 ? actualFps : 30;
                            Logger.LogInfo($"摄像头支持的最大分辨率: {bestWidth}x{bestHeight} @ {bestFps}fps");
                        }
                    }
                }
                catch { }
            }

            if (bestWidth == 0 || bestHeight == 0)
            {
                bestWidth = (int)defaultWidth;
                bestHeight = (int)defaultHeight;
                bestFps = defaultFps > 0 ? defaultFps : 30;
                string resStr = $"{bestWidth}x{bestHeight}";
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (!SupportedResolutions.Contains(resStr))
                        SupportedResolutions.Add(resStr);
                    if (!SupportedRecordResolutions.Contains(resStr))
                        SupportedRecordResolutions.Add(resStr);
                });
                Logger.LogInfo($"使用摄像头默认分辨率: {bestWidth}x{bestHeight} @ {bestFps}fps");
            }

            FpsOrigin = bestFps;
            WidthOrigin = bestWidth;
            HeightOrigin = bestHeight;

            // 读取已保存的分辨率配置，如果在支持的列表中，则优先应用
            string savedRes = ConfigurationManager.GetSetting($"Camera_{GetConfigKey()}_Resolution");
            bool appliedSavedRes = false;
            if (!string.IsNullOrEmpty(savedRes))
            {
                var parts = savedRes.Split('x');
                if (parts.Length == 2 && double.TryParse(parts[0], out double w) && double.TryParse(parts[1], out double h))
                {
                    string resStr = $"{(int)w}x{(int)h}";
                    if (SupportedResolutions.Contains(resStr))
                    {
                        Width = w;
                        Height = h;
                        _selectedResolution = resStr;
                        appliedSavedRes = true;
                        Logger.LogInfo($"[CameraService #{Index}] 启动时成功加载并应用已保存分辨率: {resStr}");
                    }
                }
            }

            if (!appliedSavedRes)
            {
                // 1. 优先选择 1280x720 (720p) 以节省 USB 总线带宽
                // 2. 若无 720p，自动寻找与 720p 最接近的分辨率 (优先向下取)
                if (SupportedResolutions.Contains("1280x720"))
                {
                    Width = 1280;
                    Height = 720;
                }
                else
                {
                    var closest = FindClosestResolutionTo720p();
                    Width = closest.Width;
                    Height = closest.Height;
                }
                _selectedResolution = $"{(int)Width}x{(int)Height}";
                Logger.LogInfo($"[CameraService #{Index}] 未发现有效已保存分辨率，默认使用推荐/最接近 720p(优先向下) 的分辨率: {_selectedResolution}");
            }

            RaisePropertyChanged(nameof(SelectedResolution));

            // 读取已保存的录像分辨率配置，如果在支持的列表中，则优先应用
            string savedRecordRes = ConfigurationManager.GetSetting($"Camera_{GetConfigKey()}_RecordResolution");
            if (!string.IsNullOrEmpty(savedRecordRes) && SupportedRecordResolutions.Contains(savedRecordRes))
            {
                _selectedRecordResolution = savedRecordRes;
            }
            else
            {
                _selectedRecordResolution = "与采集分辨率一致";
            }
            RaisePropertyChanged(nameof(SelectedRecordResolution));

            // 读取已保存的录像帧率配置
            string savedRecordFps = ConfigurationManager.GetSetting($"Camera_{GetConfigKey()}_RecordFps");
            if (!string.IsNullOrEmpty(savedRecordFps) && int.TryParse(savedRecordFps, out int rfps))
            {
                RecordFps = rfps;
            }

            // 将摄像头设置回最终确定的分辨率（避免保留探测循环最后一项的值）
            cap.Set(VideoCaptureProperties.FrameWidth, Width);
            cap.Set(VideoCaptureProperties.FrameHeight, Height);
            if (_preferredFormat == CaptureFormat.MJPG)
                cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
            else if (_preferredFormat == CaptureFormat.YUY2)
                cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('Y', 'U', 'Y', '2'));
            else
                cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('N', 'V', '1', '2'));

            double finalW = cap.Get(VideoCaptureProperties.FrameWidth);
            double finalH = cap.Get(VideoCaptureProperties.FrameHeight);
            double finalFps = cap.Get(VideoCaptureProperties.Fps);
            double finalFourcc = cap.Get(VideoCaptureProperties.FourCC);
            Logger.LogInfo($"[CameraService #{Index}] 最终初始化摄像头配置: {finalW}x{finalH} @ {finalFps}fps, 实际格式: {DecodeFourCC(finalFourcc)}");
        }

        public bool StartCapture(int index = 0)
        {
            try
            {
                if (!Infrastructure.DeviceLockManager.TryAcquireDevice(DevicePath, index))
                {
                    Logger.LogWarning($"[CameraService #{index}] 该物理设备已在其它通道中激活，跳过重复打开 (DevicePath: {DevicePath})");
                    _continuousReadFailCount = 6;
                    PushWarningFrame("通道已关闭 / 设备占用");
                    return false;
                }

                Logger.LogInfo($"正在打开摄像头索引 {index}...");
                cap = new VideoCapture(index, VideoCaptureAPIs.DSHOW);

                bool formatSet = false;
                if (!_formatChecked || _preferredFormat == CaptureFormat.MJPG)
                {
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                    Thread.Sleep(50);
                    if (cap.IsOpened())
                    {
                        double fc = cap.Get(VideoCaptureProperties.FourCC);
                        if (DecodeFourCC(fc) == "MJPG")
                        {
                            formatSet = true;
                            _preferredFormat = CaptureFormat.MJPG;
                            Logger.LogInfo($"摄像头 {index} 成功协商并使用 MJPG 格式");
                        }
                    }
                }

                if (!formatSet && (!_formatChecked || _preferredFormat == CaptureFormat.YUY2))
                {
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('Y', 'U', 'Y', '2'));
                    Thread.Sleep(50);
                    if (cap.IsOpened())
                    {
                        double fc = cap.Get(VideoCaptureProperties.FourCC);
                        if (DecodeFourCC(fc) == "YUY2")
                        {
                            formatSet = true;
                            _preferredFormat = CaptureFormat.YUY2;
                            Logger.LogInfo($"摄像头 {index} 成功协商并使用 YUY2 格式");
                        }
                    }
                }

                if (!formatSet && (!_formatChecked || _preferredFormat == CaptureFormat.NV12))
                {
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('N', 'V', '1', '2'));
                    Thread.Sleep(50);
                    if (cap.IsOpened())
                    {
                        double fc = cap.Get(VideoCaptureProperties.FourCC);
                        if (DecodeFourCC(fc) == "NV12")
                        {
                            formatSet = true;
                            _preferredFormat = CaptureFormat.NV12;
                            Logger.LogInfo($"摄像头 {index} 成功协商并使用 NV12 格式");
                        }
                    }
                }

                if (!formatSet)
                {
                    double currentFC = cap.Get(VideoCaptureProperties.FourCC);
                    string currentFCStr = DecodeFourCC(currentFC);
                    if (currentFCStr == "YUY2")
                        _preferredFormat = CaptureFormat.YUY2;
                    else if (currentFCStr == "NV12")
                        _preferredFormat = CaptureFormat.NV12;
                    else
                    {
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                        _preferredFormat = CaptureFormat.MJPG;
                    }
                    Logger.LogInfo($"摄像头 {index} 协商匹配格式失败，采用驱动当前/默认格式: {DecodeFourCC(cap.Get(VideoCaptureProperties.FourCC))}");
                }
                _formatChecked = true;

                this.Index = index;
                this.Name = $"通道 {index}";
                _recordingOptions.CameraIndex = index;

                if (!cap.IsOpened())
                {
                    Logger.LogWarning($"摄像头 {index} 无法打开（IsOpened 返回 false），将作为占位通道并在后台自动重试");
                    Fps = 15;
                    Width = 1280;
                    Height = 720;
                    _previewController.SetCameraFps(Fps);
                    _continuousReadFailCount = 6; // 直接显示警告帧
                    RefreshFrame();
                    Infrastructure.DeviceLockManager.ReleaseDevice(DevicePath, index);
                    return true;
                }

                ShowVideoInfo();
                Fps = FpsOrigin;

                _previewController.SetCameraFps(Fps);

                RefreshFrame();
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"启动摄像头 {index} 时发生错误", ex);
                Infrastructure.DeviceLockManager.ReleaseDevice(DevicePath, index);
                return false;
            }
        }

        private void ReopenCamera()
        {
            try
            {
                if (!string.IsNullOrEmpty(DevicePath))
                {
                    var devices = camera.Infrastructure.CameraDeviceEnumerator.EnumerateCameras();
                    var match = devices.Find(d => d.DevicePath == DevicePath);
                    if (match == null)
                    {
                        Logger.LogWarning($"[CameraService] 尝试自恢复重连摄像头 {Name}，但未在系统列表中找到匹配的物理设备，跳过重连尝试");
                        return;
                    }
                    if (Index != match.Index)
                    {
                        Logger.LogInfo($"[CameraService] 摄像头 {Name} 索引自适应纠正: {Index} -> {match.Index}");
                        Index = match.Index;
                    }
                }

                if (cap != null)
                {
                    if (cap.IsOpened())
                    {
                        cap.Release();
                    }
                    cap.Dispose();
                    cap = null;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Infrastructure.DeviceLockManager.ReleaseDevice(DevicePath, Index);
                }

                Thread.Sleep(200);

                if (!Infrastructure.DeviceLockManager.TryAcquireDevice(DevicePath, Index))
                {
                    Logger.LogWarning($"[CameraService #{Index}] 尝试重连受阻：设备已被其它通道锁定");
                    return;
                }

                cap = new VideoCapture(Index, VideoCaptureAPIs.DSHOW);
                cap.Set(VideoCaptureProperties.FrameWidth, Width);
                cap.Set(VideoCaptureProperties.FrameHeight, Height);
                cap.Set(VideoCaptureProperties.Fps, Fps);

                Thread.Sleep(50);

                if (_preferredFormat == CaptureFormat.MJPG)
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                else if (_preferredFormat == CaptureFormat.YUY2)
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('Y', 'U', 'Y', '2'));
                else
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('N', 'V', '1', '2'));

                if (cap.IsOpened())
                {
                    _continuousReadFailCount = 0;
                    _lastPipelineProcessTime = DateTime.MinValue; // 重连成功，重置节流时间
                    
                    double actualW = cap.Get(VideoCaptureProperties.FrameWidth);
                    double actualH = cap.Get(VideoCaptureProperties.FrameHeight);
                    double actualFps = cap.Get(VideoCaptureProperties.Fps);
                    double actualFourcc = cap.Get(VideoCaptureProperties.FourCC);
                    
                    Logger.LogInfo($"摄像头 {Index} 系统自恢复重置成功。实际参数: {actualW}x{actualH} @ {actualFps}fps, 实际格式: {DecodeFourCC(actualFourcc)}");
                    
                    if (SupportedResolutions.Count == 0)
                    {
                        ShowVideoInfo();
                        Fps = FpsOrigin;
                        Width = WidthOrigin;
                        Height = HeightOrigin;
                        _previewController.SetCameraFps(Fps);
                    }
                    else
                    {
                        if (actualW > 0 && actualH > 0)
                        {
                            Width = actualW;
                            Height = actualH;
                        }
                    }
                }
                else
                {
                    Logger.LogWarning($"[CameraService] 重新打开摄像头 {Index} 失败，重置连续失败计数以进行下一次周期的尝试");
                    _continuousReadFailCount = 0;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"摄像头 {Index} 尝试重新初始化连接时发生异常", ex);
                _continuousReadFailCount = 0;
            }
        }

        private void PushWarningFrame(string errorMsg)
        {
            Mat tempFrame = null;
            try
            {
                int w = Width > 0 ? (int)Width : 1280;
                int h = Height > 0 ? (int)Height : 720;
                tempFrame = Infrastructure.MatPool.Rent(w, h, MatType.CV_8UC3);
                tempFrame.SetTo(new Scalar(20, 20, 20)); // 暗灰底色

                using (var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        g.Clear(System.Drawing.Color.FromArgb(20, 20, 20));
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                        float fontSize = Math.Max(16f, 22f * (w / 640.0f));
                        using (var font = new System.Drawing.Font("Microsoft YaHei", fontSize, System.Drawing.FontStyle.Bold))
                        using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(240, 70, 70)))
                        using (var format = new System.Drawing.StringFormat())
                        {
                            format.Alignment = System.Drawing.StringAlignment.Center;
                            format.LineAlignment = System.Drawing.StringAlignment.Center;

                            var rect = new System.Drawing.RectangleF(0, 0, w, h);
                            g.DrawString(errorMsg ?? "", font, brush, rect, format);
                        }
                    }

                    var rectLock = new System.Drawing.Rectangle(0, 0, w, h);
                    var bmpData = bitmap.LockBits(rectLock, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    try
                    {
                        unsafe
                        {
                            byte* srcPtr = (byte*)bmpData.Scan0;
                            byte* dstPtr = (byte*)tempFrame.Data;
                            int strideSrc = bmpData.Stride;
                            int strideDst = (int)tempFrame.Step();

                            for (int row = 0; row < h; row++)
                            {
                                Buffer.MemoryCopy(srcPtr + row * strideSrc, dstPtr + row * strideDst, strideDst, w * 3);
                            }
                        }
                    }
                    finally
                    {
                        bitmap.UnlockBits(bmpData);
                    }
                }

                _previewController.UpdateFrame(tempFrame, DateTime.Now);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[CameraService #{Index}] 推送警告帧异常", ex);
            }
            finally
            {
                if (tempFrame != null)
                {
                    Infrastructure.MatPool.Return(tempFrame);
                }
            }
        }

        public void HandleDeviceRemoved()
        {
            lock (_lockObject)
            {
                Logger.LogWarning($"[CameraService #{Index}] 设备被拔除，正在执行释放与警示处理...");
                
                _isCapturing = false;
                
                // 等待采集线程结束，以防并发访问 cap 导致 OpenCV 崩溃/死锁
                if (_captureTask != null)
                {
                    try
                    {
                        Logger.LogInfo($"[CameraService #{Index}] 正在等待采集任务 _captureTask 退出...");
                        _captureTask.Wait(1000);
                        Logger.LogInfo($"[CameraService #{Index}] 采集任务 _captureTask 已退出");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"[CameraService #{Index}] 等待采集任务退出异常: {ex.Message}");
                    }
                }
                
                bool wasConnected = IsConnected;
                _continuousReadFailCount = 6; // 设置为 6，确保 IsConnected 返回 false
                if (wasConnected)
                {
                    RaisePropertyChanged(nameof(IsConnected));
                }

                // 立即推送断开警告帧
                string errorMsg = $"Channel {Index} - Connection Disconnected";
                PushWarningFrame(errorMsg);

                // 停止录像（如果开启）
                if (isSave)
                {
                    StopSaving();
                }

                // 同步释放 VideoCapture，确保 DirectShow 资源完全回收后再继续
                // （采集循环已停止并 Wait，不会死锁；同步释放防止其他通道在资源未回收时串读）
                if (cap != null)
                {
                    var capToDispose = cap;
                    cap = null; // 立即置空，防止其他线程继续访问
                    try
                    {
                        Logger.LogInfo($"[CameraService #{Index}] 正在同步释放已拔除设备的 VideoCapture...");
                        if (capToDispose.IsOpened())
                        {
                            capToDispose.Release();
                        }
                        capToDispose.Dispose();
                        Logger.LogInfo($"[CameraService #{Index}] 同步释放已拔除设备的 VideoCapture 完成");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"[CameraService #{Index}] 同步释放已拔除设备的 VideoCapture 异常: {ex.Message}");
                    }
                }
            }
        }

        private void RefreshFrame()
        {
            _isCapturing = true;

            _captureTask = Task.Run(() =>
            {
                Stopwatch stopwatch = new Stopwatch();

                // 本地辅助函数，统一处理设备采集失败时的自愈与通知逻辑
                void HandleCaptureFailure()
                {
                    if (!_isCapturing) return;

                    bool wasConnectedInFail = _continuousReadFailCount <= 5;
                    _continuousReadFailCount++;
                    if (wasConnectedInFail != (_continuousReadFailCount <= 5))
                    {
                        RaisePropertyChanged(nameof(IsConnected));
                    }

                    if (_continuousReadFailCount > 5)
                    {
                        DateTime now = DateTime.Now;
                        if (now - _lastWarningFrameTime >= TimeSpan.FromSeconds(2.0))
                        {
                            _lastWarningFrameTime = now;

                            string errorMsg = (cap == null || !cap.IsOpened())
                                 ? $"Channel {Index} - Device Occupied / Not Ready"
                                 : $"Channel {Index} - Connection Disconnected";
                            PushWarningFrame(errorMsg);
                        }
                    }

                    if (_continuousReadFailCount > 30)
                    {
                        Logger.LogWarning($"摄像头 {Index} 连续读取失败超过 30 次，底层连接断开，尝试自动重新初始化...");
                        Thread.Sleep(1000);
                        ReopenCamera();
                    }
                    else
                    {
                        Thread.Sleep(30);
                    }
                }

                while (_isCapturing)
                {
                    stopwatch.Restart();
                    Mat tempFrame = null;

                    try
                    {
                        if (cap == null || cap.IsDisposed || !cap.IsOpened())
                        {
                            HandleCaptureFailure();
                            continue;
                        }

                        // ═══ 管道节流：基于时间戳（Wall-clock Time）进行判断 ═══
                        int targetPipelineFps = isSave ? Math.Max(PreviewFps, RecordFps) : PreviewFps;
                        double targetIntervalMs = 1000.0 / Math.Max(1, targetPipelineFps);
                        DateTime pipelineNow = DateTime.Now;
                        double elapsedMs = (pipelineNow - _lastPipelineProcessTime).TotalMilliseconds;
                        bool needProcess = elapsedMs >= targetIntervalMs - 2.0;

                        // 从对象池租借 Mat
                        int w = Width > 0 ? (int)Width : 1280;
                        int h = Height > 0 ? (int)Height : 720;
                        tempFrame = Infrastructure.MatPool.Rent(w, h, MatType.CV_8UC3);

                        // 原子读取：读取完即填充到 tempFrame，避免 Grab 和 Retrieve 分步导致的驱动缓冲区竞争撕裂
                        if (!cap.Read(tempFrame) || tempFrame.Empty())
                        {
                            Infrastructure.MatPool.Return(tempFrame);
                            tempFrame = null;
                            HandleCaptureFailure();
                            continue;
                        }

                        if (!_isCapturing)
                        {
                            break;
                        }

                        if (!needProcess)
                        {
                            // 丢弃此帧的解码结果：仅用于清空 USB 缓冲区以防延迟，直接丢弃
                            Infrastructure.MatPool.Return(tempFrame);
                            tempFrame = null;
                            continue;
                        }

                        bool wasConnectedInSuccess = _continuousReadFailCount <= 5;
                        _continuousReadFailCount = 0;
                        _lastWarningFrameTime = DateTime.MinValue;
                        if (wasConnectedInSuccess != (_continuousReadFailCount <= 5))
                        {
                            RaisePropertyChanged(nameof(IsConnected));
                        }

                        // 如果开启了录像且录像服务未就绪/未运行，在首次成功捕获正常画面帧后自动开启录像
                        if (isSave && _recorder != null && !_recorder.IsRecording)
                        {
                            System.Threading.Tasks.Task.Run(() => StartSaving());
                        }

                        _lastPipelineProcessTime = pipelineNow;

                        // 兼容性事件通知
                        FrameChanged?.Invoke(tempFrame);

                        // 执行责任链图像处理管道（含水印叠加 + 录像写入）
                        _pipeline.Process(tempFrame, pipelineNow);

                        // 将处理后的图像传递给预览控制器
                        _previewController.UpdateFrame(tempFrame, pipelineNow);

                        // 磁盘检查计数和采集帧率监控
                        _frameCount++;
                        if (_frameCount % 100 == 0)
                        {
                            if (!_fpsTimer.IsRunning)
                            {
                                _fpsTimer.Start();
                            }
                            else
                            {
                                double sec = _fpsTimer.Elapsed.TotalSeconds;
                                _fpsTimer.Restart();
                                if (sec > 0)
                                {
                                    double actualCaptureFps = 100.0 / sec;
                                    Logger.LogInfo($"[CameraService #{Index}] 过去 100 帧的实际物理采集帧率为: {actualCaptureFps:F2} fps");
                                }
                            }

                            _frameCount = 0;
                            CheckDiskSpace();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"捕获帧异常: {ex.Message}");
                    }
                    finally
                    {
                        if (tempFrame != null) Infrastructure.MatPool.Return(tempFrame);
                    }

                    if (Application.Current == null)
                        break;

                    long elapsedTicks = stopwatch.ElapsedTicks;
                    long frameInterval = (long)(1000.0 / Fps * TimeSpan.TicksPerMillisecond);
                    if (elapsedTicks < frameInterval)
                    {
                        int sleepMs = (int)((frameInterval - elapsedTicks) / TimeSpan.TicksPerMillisecond);
                        if (sleepMs > 0)
                            Thread.Sleep(sleepMs);
                    }
                }
            });
        }

        private void OnIsSaveChanged()
        {
            if (isSave)
            {
                System.Threading.Tasks.Task.Run(() => StartSaving());
            }
            else
            {
                System.Threading.Tasks.Task.Run(() => StopSaving());
            }
        }

        private void StartSaving()
        {
            if (_recorder == null) return;
            if (_recorder.IsRecording) return;

            if (cap == null || cap.IsDisposed || !cap.IsOpened() || !IsConnected)
            {
                Logger.LogInfo($"[CameraService #{Index}] 摄像头 {Name} 尚未成功打开或当前处于离线状态，暂缓开启录像。");
                return;
            }

            long space = CalcDiskSpaceEnough();
            if (space < DiskThreshold)
            {
                CheckDiskSpace();
            }

            if (CalcDiskSpaceEnough() < DiskThreshold)
            {
                Logger.LogWarning($"[CameraService] 磁盘空间低于阈值，拒绝开启录像。");
                isSave = false;
                return;
            }

            // 解析录像分辨率
            int recordW = (int)Width;
            int recordH = (int)Height;
            if (SelectedRecordResolution != "与采集分辨率一致")
            {
                var parts = SelectedRecordResolution.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int rw) && int.TryParse(parts[1], out int rh))
                {
                    recordW = rw;
                    recordH = rh;
                }
            }

            _recordingOptions.InputWidth = (int)Width;
            _recordingOptions.InputHeight = (int)Height;
            _recordingOptions.Width = recordW;
            _recordingOptions.Height = recordH;
            // ⚠️ 关键: 这里设置的是"管道帧率"（= RecordFps），而非相机原始帧率(Fps)。
            // 原因: RefreshFrame 中已按 RecordFps 对管道进行节流，
            //       录制器实际接收到的帧率 ≈ RecordFps，而非 Fps。
            // 若仍填 Fps(如30)，则录制器内部 skipInterval = 30/10 = 3，
            // 会在节流后再跳 2/3 帧，导致实际录制帧率仅约 RecordFps/3 ≈ 3fps。
            // 填 RecordFps(如10) 则 skipInterval = 10/10 = 1，接收什么就录什么。
            _recordingOptions.Fps = Math.Max(1, RecordFps);
            _recordingOptions.RecordFps = RecordFps;
            _recordingOptions.SaveDirectory = saveDirectory;
            _recordingOptions.SegmentDuration = SaveInterval;
            _recordingOptions.CustomText = CustomOverlayText;
            _recordingOptions.OverlayTextSize = OverlayTextSize;
 
            _recorder.StartRecording(null, (int)Width, (int)Height, recordW, recordH, Fps, Quality);

        }

        private void StopSaving()
        {
            _recorder?.StopRecording();
        }

        private void CheckDiskSpace()
        {
            if (!isSave) return;

            long space = CalcDiskSpaceEnough();
            if (space < DiskThreshold)
            {
                Logger.LogWarning($"磁盘空间不足: {space}MB < {DiskThreshold}MB");
                if (OverwriteOldFiles)
                {
                    long targetSpace = DiskThreshold + 500;
                    space = BatchCleanUpDiskSpace(targetSpace, space);

                    if (space < DiskThreshold)
                    {
                        Logger.LogError("清理所有旧文件后空间仍不足，停止录像。");
                        TriggerDiskSpaceLow($"清理所有旧文件后空间仍不足，录像已停止: {space}MB");
                        StopSave();
                    }
                }
                else
                {
                    Logger.LogWarning("磁盘空间不足且未启用覆盖旧文件，停止录像。");
                    TriggerDiskSpaceLow($"磁盘空间不足且未启用覆盖旧文件，录像已停止: {space}MB");
                    StopSave();
                }
            }
        }

        private void TriggerDiskSpaceLow(string message)
        {
            if ((DateTime.Now - _lastDiskSpaceWarningTime).TotalSeconds > 10)
            {
                _lastDiskSpaceWarningTime = DateTime.Now;
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        DiskSpaceLow?.Invoke(this, message);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("触发 DiskSpaceLow 事件发生异常", ex);
                    }
                });
            }
        }

        private void StopSave()
        {
            isSave = false;
        }

        private long CalcDiskSpaceEnough()
        {
            string drive = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
            DriveInfo driveInfo = new DriveInfo(drive);
            long availableSpace = driveInfo.AvailableFreeSpace / (1024 * 1024); // MB
            return availableSpace;
        }

        private long BatchCleanUpDiskSpace(long targetSpace, long currentAvailableSpace)
        {
            lock (_diskCleanupLock)
            {
                currentAvailableSpace = CalcDiskSpaceEnough();
                if (currentAvailableSpace >= targetSpace)
                {
                    return currentAvailableSpace;
                }

                if (!Directory.Exists(saveDirectory))
                {
                    return currentAvailableSpace;
                }

                try
                {
                    var allMp4Files = Directory.GetFiles(saveDirectory, "*.mp4", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .OrderBy(fi => fi.LastWriteTime)
                        .ToList();

                    int deletedCount = 0;
                    long deletedSpaceMB = 0;

                    foreach (var fileInfo in allMp4Files)
                    {
                        if (currentAvailableSpace >= targetSpace)
                            break;

                        try
                        {
                            if (File.Exists(fileInfo.FullName))
                            {
                                long fileSizeMB = fileInfo.Length / (1024 * 1024);
                                if (fileSizeMB < 1) fileSizeMB = 1;

                                fileInfo.Delete();
                                deletedCount++;
                                deletedSpaceMB += fileSizeMB;
                                currentAvailableSpace += fileSizeMB;

                                var parentDir = fileInfo.Directory;
                                if (parentDir != null && parentDir.Exists && parentDir.GetFiles().Length == 0 && parentDir.GetDirectories().Length == 0)
                                {
                                    try { parentDir.Delete(); } catch { }
                                }
                            }
                        }
                        catch (IOException) { }
                        catch (Exception ex)
                        {
                            Logger.LogError($"批清理删除个别文件失败: {fileInfo.FullName}", ex);
                        }
                    }

                    if (deletedCount > 0)
                    {
                        Logger.LogInfo($"磁盘清理完成: 共删除 {deletedCount} 个过期视频文件，释放空间约 {deletedSpaceMB}MB");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("全局批量清理旧视频发生异常", ex);
                }

                return CalcDiskSpaceEnough();
            }
        }

        public void ChangeCameraInfo()
        {
            lock (_lockObject)
            {
                if (cap == null && Index < 0) return;
                
                Logger.LogInfo($"[CameraService #{Index}] 正在修改分辨率为 {Width}x{Height}...");
                
                // 停止采集循环并等待其结束
                _isCapturing = false;
                _lastPipelineProcessTime = DateTime.MinValue; // 重置节流时间，避免切换后残留旧状态

                // 核心修复：如果当前正在录像，需要先临时停止录像以释放编码器和 FFmpeg 进程
                bool wasSaving = isSave;
                if (wasSaving)
                {
                    StopSaving();
                }

                try
                {
                    if (_captureTask != null)
                    {
                        _captureTask.Wait(500);
                        _captureTask = null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"等待旧采集循环退出异常: {ex.Message}");
                }

                try
                {
                    if (cap != null)
                    {
                        if (cap.IsOpened())
                        {
                            cap.Release();
                        }
                        cap.Dispose();
                        cap = null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"释放旧采集器异常: {ex.Message}");
                }

                // 重新创建 VideoCapture
                try
                {
                    if (!string.IsNullOrEmpty(DevicePath))
                    {
                        var devices = camera.Infrastructure.CameraDeviceEnumerator.EnumerateCameras();
                        var match = devices.Find(d => d.DevicePath == DevicePath);
                        if (match == null)
                        {
                            Logger.LogWarning($"[CameraService] 尝试修改摄像头 {Name} 分辨率或重载，但该物理设备已被移除，中止操作");
                            _continuousReadFailCount = 6;
                            RaisePropertyChanged(nameof(IsConnected));
                            RefreshFrame();
                            return;
                        }
                        if (Index != match.Index)
                        {
                            Logger.LogInfo($"[CameraService] 摄像头 {Name} 索引自适应纠正: {Index} -> {match.Index}");
                            Index = match.Index;
                        }
                    }

                    cap = new VideoCapture(Index, VideoCaptureAPIs.DSHOW);
                    cap.Set(VideoCaptureProperties.FrameWidth, Width);
                    cap.Set(VideoCaptureProperties.FrameHeight, Height);
                    cap.Set(VideoCaptureProperties.Fps, Fps);

                    Thread.Sleep(50);

                    if (_preferredFormat == CaptureFormat.MJPG)
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                    else if (_preferredFormat == CaptureFormat.YUY2)
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('Y', 'U', 'Y', '2'));
                    else
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('N', 'V', '1', '2'));

                    // 强行读取一帧哑帧驱动 DirectShow 内部的 Pin 协商与缓冲区重分配
                    if (cap.IsOpened())
                    {
                        using (Mat dummy = new Mat())
                        {
                            cap.Read(dummy);
                        }

                        bool wasConnected = IsConnected;
                        _continuousReadFailCount = 0;
                        _lastPipelineProcessTime = DateTime.MinValue; // 重置节流时间，避免切换后残留旧状态
                        if (!wasConnected)
                        {
                            RaisePropertyChanged(nameof(IsConnected));
                        }
                    }

                    double actualW = cap.Get(VideoCaptureProperties.FrameWidth);
                    double actualH = cap.Get(VideoCaptureProperties.FrameHeight);
                    double actualFps = cap.Get(VideoCaptureProperties.Fps);
                    double actualFourcc = cap.Get(VideoCaptureProperties.FourCC);
                    Logger.LogInfo($"[CameraService #{Index}] 重连配置: {actualW}x{actualH} @ {actualFps}fps, 实际格式: {DecodeFourCC(actualFourcc)}");

                    if (!cap.IsOpened())
                    {
                        Logger.LogError($"[CameraService #{Index}] 重新打开摄像头失败");
                        RefreshFrame();
                        return;
                    }

                    if (actualW > 0 && actualH > 0)
                    {
                        Width = actualW;
                        Height = actualH;
                        
                        string actualResStr = $"{(int)actualW}x{(int)actualH}";

                        // 仅当确定此分辨率被硬件驱动成功应用时，才实时保存配置
                        if (Index >= 0)
                        {
                            ConfigurationManager.SaveSetting($"Camera_{GetConfigKey()}_Resolution", actualResStr);
                        }

                        if (_selectedResolution != actualResStr)
                        {
                            _selectedResolution = actualResStr;
                            RaisePropertyChanged(nameof(SelectedResolution));
                        }
                    }

                    _previewController.SetCameraFps(Fps);
                    RefreshFrame();

                    // 重新开启录像
                    if (wasSaving)
                    {
                        StartSaving();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[CameraService #{Index}] 修改分辨率发生异常", ex);
                    RefreshFrame();
                }
            }
        }

        public void UpdateOverlayDate() { }

        public void StopCapture()
        {
            _isCapturing = false;
            cap?.Release();
        }

        #region IDisposable

        private bool _disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                try
                {
                    lock (_lockObject)
                    {
                        _isCapturing = false;

                        // 1. 等待采集线程结束，确保没有任何线程在执行 C++ 原生 cap.Read()
                        if (_captureTask != null)
                        {
                            try
                            {
                                Logger.LogInfo($"[CameraService #{Index}] 正在等待采集任务 _captureTask 退出...");
                                _captureTask.Wait(1200);
                                Logger.LogInfo($"[CameraService #{Index}] 采集任务 _captureTask 已退出");
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarning($"[CameraService #{Index}] 等待采集任务退出异常: {ex.Message}");
                            }
                        }

                        _uiTimer?.Stop();
                        _uiTimer = null;

                        if (FrameChanged != null)
                        {
                            foreach (var d in FrameChanged.GetInvocationList())
                            {
                                FrameChanged -= (FrameChangedEventHandler)d;
                            }
                        }

                        if (_recorder != null)
                        {
                            try { _recorder.Dispose(); } catch { }
                            _recorder = null;
                        }

                        // 2. 在线程确认退出后顺发、同步安全释放 VideoCapture 句柄，杜绝 AccessViolationException
                        if (cap != null)
                        {
                            try
                            {
                                Logger.LogInfo($"[CameraService #{Index}] 正在释放 VideoCapture...");
                                if (cap.IsOpened())
                                {
                                    cap.Release();
                                }
                                cap.Dispose();
                                Logger.LogInfo($"[CameraService #{Index}] 释放 VideoCapture 完成");
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarning($"[CameraService #{Index}] 释放 VideoCapture 异常: {ex.Message}");
                            }
                            cap = null;
                        }

                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        Infrastructure.DeviceLockManager.ReleaseDevice(DevicePath, Index);

                        _previewController?.Dispose();
                        _claheProcessor?.Dispose();
                        _timestampOverlayProcessor?.Dispose();

                        if (_Frame != null)
                        {
                            _Frame.Dispose();
                            _Frame = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"释放 CameraService 资源异常: {ex.Message}");
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
