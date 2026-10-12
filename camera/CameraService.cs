using OpenCvSharp;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
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
            if (pending != null)
            {
                if (_disposed)
                {
                    try { Infrastructure.MatPool.Return(pending); } catch { }
                    return;
                }
                Frame = pending;
            }
        }

        // 管道内特定的图像处理算子
        private ClaheProcessor _claheProcessor;
        private ContrastLuminanceProcessor _contrastLuminanceProcessor;
        private FlipProcessor _flipProcessor;
        private TimestampOverlayProcessor _timestampOverlayProcessor;
        private RecordingProcessor _recordingProcessor;

        #endregion

        #region 锁和时间统计

        private readonly object _lockObject = new();
        private readonly Stopwatch _fpsTimer = new();
        private volatile bool _isStartingSaving = false;
        private volatile bool _isFirstFrame = true;

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
                        SaveCameraSetting(s => s.RecordFps = clamped);
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
                    try
                    {
                        if (oldFrame.Width > 0 && oldFrame.Height > 0)
                            Infrastructure.MatPool.Return(oldFrame);
                        else
                            oldFrame.Dispose();
                    }
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

        public string saveDirectory
        {
            get => StorageManager.Instance.SaveDirectory;
            set => StorageManager.Instance.SaveDirectory = value;
        }

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

        public long DiskThreshold
        {
            get => StorageManager.Instance.DiskSpaceThresholdMb;
            set => StorageManager.Instance.DiskSpaceThresholdMb = value;
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

        public bool OverwriteOldFiles
        {
            get => StorageManager.Instance.OverwriteOldFiles;
            set => StorageManager.Instance.OverwriteOldFiles = value;
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
                        SaveCameraSetting(s => s.RecordResolution = value);
                        ConfigurationManager.SaveSetting($"Camera_{GetConfigKey()}_RecordResolution", value);
                    }
                    if (isSave)
                    {
                        System.Threading.Tasks.Task.Run(() => RestartSaving());
                    }
                }
            }
        }

        public void RestartSaving()
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
                        // 仅当底层采集器已打开运行时才异步触发 ChangeCameraInfo 动态重载；
                        // 在初始化阶段（cap == null）仅更新目标参数，避免冗余创建并销毁设备
                        if (cap != null && cap.IsOpened())
                        {
                            System.Threading.Tasks.Task.Run(() => ChangeCameraInfo());
                        }
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
                        SaveCameraSetting(s => s.CustomText = value);
                        ConfigurationManager.SaveSetting($"Camera_{GetConfigKey()}_CustomText", value ?? "");
                    }

                    if (isSave)
                    {
                        System.Threading.Tasks.Task.Run(() => RestartSaving());
                    }
                }
            }
        }

        public bool IsRecording => _recorder?.IsRecording ?? false;

        public string ActiveCodec => _recorder?.ActiveCodec ?? "None";

        public bool IsCodecDowngraded
        {
            get
            {
                if (_recorder == null || !isSave || !IsRecording) return false;
                string codec = ActiveCodec.ToLower();
                if (string.IsNullOrEmpty(codec) || codec == "none") return false;
                return codec.Contains("libx264") || codec.Contains("mpeg4");
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
                    if (Index >= 0)
                    {
                        SaveCameraSetting(s => s.IsManualDisabled = value);
                        ConfigurationManager.SaveSetting($"Camera_{GetConfigKey()}_IsManualDisabled", value.ToString());
                    }

                    RaisePropertyChanged(nameof(IsConnected));
                    EnableCameraCommand?.RaiseCanExecuteChanged();
                    DisableCameraCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        private bool _isFlipHorizontal = false;
        public bool IsFlipHorizontal
        {
            get => _isFlipHorizontal;
            set
            {
                if (SetProperty(ref _isFlipHorizontal, value))
                {
                    if (_flipProcessor != null)
                    {
                        _flipProcessor.FlipHorizontal = value;
                    }
                    string key = GetConfigKey();
                    if (!string.IsNullOrEmpty(key))
                    {
                        SaveCameraSetting(s => s.IsFlipHorizontal = value);
                        ConfigurationManager.SaveSetting($"Camera_{key}_IsFlipHorizontal", value.ToString());
                    }
                    Logger.LogInfo($"[CameraService #{Index}] 设置水平翻转: {value}");
                }
            }
        }

        private bool _isFlipVertical = false;
        public bool IsFlipVertical
        {
            get => _isFlipVertical;
            set
            {
                if (SetProperty(ref _isFlipVertical, value))
                {
                    if (_flipProcessor != null)
                    {
                        _flipProcessor.FlipVertical = value;
                    }
                    string key = GetConfigKey();
                    if (!string.IsNullOrEmpty(key))
                    {
                        SaveCameraSetting(s => s.IsFlipVertical = value);
                        ConfigurationManager.SaveSetting($"Camera_{key}_IsFlipVertical", value.ToString());
                    }
                    Logger.LogInfo($"[CameraService #{Index}] 设置垂直翻转: {value}");
                }
            }
        }

        private Stretch _videoStretch = Stretch.Uniform;
        public Stretch VideoStretch
        {
            get => _videoStretch;
            set
            {
                if (SetProperty(ref _videoStretch, value))
                {
                    string key = GetConfigKey();
                    if (!string.IsNullOrEmpty(key))
                    {
                        SaveCameraSetting(s => s.StretchMode = value.ToString());
                        ConfigurationManager.SaveSetting($"Camera_{key}_StretchMode", value.ToString());
                    }
                    RaisePropertyChanged(nameof(IsStretchUniform));
                    RaisePropertyChanged(nameof(IsStretchFill));
                    RaisePropertyChanged(nameof(IsStretchUniformToFill));
                    Logger.LogInfo($"[CameraService #{Index}] 设置画面比例模式: {value}");
                }
            }
        }

        public bool IsStretchUniform => VideoStretch == Stretch.Uniform;
        public bool IsStretchFill => VideoStretch == Stretch.Fill;
        public bool IsStretchUniformToFill => VideoStretch == Stretch.UniformToFill;

        private void SaveCameraSetting(Action<AppSettings.CameraSettings> mutator)
        {
            try
            {
                string key = GetConfigKey();
                if (string.IsNullOrEmpty(key)) return;

                AppSettingsService.Instance.Update(settings =>
                {
                    if (!settings.Cameras.TryGetValue(key, out var camSetting))
                    {
                        camSetting = new AppSettings.CameraSettings();
                        settings.Cameras[key] = camSetting;
                    }
                    mutator(camSetting);
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[CameraService] 保存摄像头配置失败: {ex.Message}");
            }
        }

        public bool IsConnected => !_isManualDisabled && _continuousReadFailCount <= 5;

        public DelegateCommand EnableCameraCommand { get; private set; }
        public DelegateCommand DisableCameraCommand { get; private set; }
        public DelegateCommand ToggleFlipHorizontalCommand { get; private set; }
        public DelegateCommand ToggleFlipVerticalCommand { get; private set; }
        public DelegateCommand<string> SetStretchCommand { get; private set; }

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
            RaisePropertyChanged(nameof(IsRecording));
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
            ToggleFlipHorizontalCommand = new DelegateCommand(() => IsFlipHorizontal = !IsFlipHorizontal);
            ToggleFlipVerticalCommand = new DelegateCommand(() => IsFlipVertical = !IsFlipVertical);
            SetStretchCommand = new DelegateCommand<string>(mode =>
            {
                if (Enum.TryParse<Stretch>(mode, true, out var s))
                {
                    VideoStretch = s;
                }
            });
            StorageManager.Instance.StorageWarning += OnStorageWarningForward;
            InitializeModules();
        }

        private void OnStorageWarningForward(object sender, string message)
        {
            DiskSpaceLow?.Invoke(this, message);
        }

        private void InitializeModules()
        {
            // 1. 初始化模块参数与流水线
            _claheProcessor = new ClaheProcessor { Enabled = IsGrayHomogenization, Order = 10 };
            _contrastLuminanceProcessor = new ContrastLuminanceProcessor { Contrast = Contrast, Luminance = Luminance, Order = 20 };
            _flipProcessor = new FlipProcessor { FlipHorizontal = IsFlipHorizontal, FlipVertical = IsFlipVertical, Order = 25 };
            _timestampOverlayProcessor = new TimestampOverlayProcessor { Enabled = false, Order = 30 };
            _recordingProcessor = new RecordingProcessor { Enabled = true, Order = 90 };

            _pipeline = FramePipelineBuilder.Create()
                .AddProcessor(_claheProcessor)
                .AddProcessor(_contrastLuminanceProcessor)
                .AddProcessor(_flipProcessor)
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
                if (Application.Current == null || camera.ViewModels.MainWindowViewModel.IsUIRenderingPaused)
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

                // 首帧零延迟直通：在拿到首个预览帧的瞬间立即通知 UI 线程拉帧呈现，消灭 100ms 周期等待
                if (_isFirstFrame)
                {
                    _isFirstFrame = false;
                    Application.Current?.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Send,
                        new Action(() => PullAndApplyLatestFrame()));
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
            _recorder.RecordingStarted += (s, e) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateActiveCodecStatus();
                }));
            };
            _recorder.RecordingStopped += (s, e) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateActiveCodecStatus();
                }));
            };
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

        private List<string> _hardwareResolutions;

        /// <summary>
        /// 注入 DirectShow 底层枚举阶段捕获的真实硬件分辨率能力集（含 4K/2K 等全部规格，0 毫秒耗时）
        /// </summary>
        public void SetHardwareResolutions(List<string> resolutions)
        {
            if (resolutions != null && resolutions.Count > 0)
            {
                _hardwareResolutions = new List<string>(resolutions);
            }
        }

        private void ShowVideoInfo()
        {
            double defaultFps = cap.Get(VideoCaptureProperties.Fps);
            double defaultWidth = cap.Get(VideoCaptureProperties.FrameWidth);
            double defaultHeight = cap.Get(VideoCaptureProperties.FrameHeight);

            Logger.LogInfo($"摄像头默认硬件报告: {defaultWidth}x{defaultHeight} @ {defaultFps}fps");

            string configKey = GetConfigKey();
            AppSettings.CameraSettings camConfig = null;
            AppSettingsService.Instance.Current.Cameras.TryGetValue(configKey, out camConfig);

            List<string> resList = new List<string>();

            // 1. 优先读取 DirectShow 驱动原生探测到的全部硬件支持分辨率（含 4K、2K、1080p 等全部规格）
            if (_hardwareResolutions != null && _hardwareResolutions.Count > 0)
            {
                resList.AddRange(_hardwareResolutions);
                Logger.LogInfo($"[CameraService #{Index}] 成功载入 DirectShow 原生硬件分辨率能力 (共 {resList.Count} 档，含最高档: {resList[0]}): {string.Join(", ", resList)}");
                SaveCameraSetting(s => s.SupportedResolutions = new List<string>(resList));
            }
            // 2. 其次读取持久化缓存
            else if (camConfig?.SupportedResolutions != null && camConfig.SupportedResolutions.Count > 0)
            {
                resList.AddRange(camConfig.SupportedResolutions);
                Logger.LogInfo($"[CameraService #{Index}] 成功命中持久化分辨率缓存: {string.Join(", ", resList)}");
            }
            else
            {
                // 无原生探测与缓存保底：探测核心主流分辨率集（含 4K、2K、1080p、720p、480p）
                var candidateResolutions = new (int Width, int Height)[]
                {
                    (3840, 2160), // 4K
                    (2560, 1440), // 2K / QHD
                    (1920, 1080), // 1080p
                    (1280, 720),  // 720p
                    (640, 480),   // VGA
                };

                // 若默认分辨率有效且不在候选集中，插入首位优先验证
                var testList = new List<(int Width, int Height)>();
                if (defaultWidth > 0 && defaultHeight > 0)
                {
                    testList.Add(((int)defaultWidth, ((int)defaultHeight)));
                }
                foreach (var c in candidateResolutions)
                {
                    if (!testList.Any(t => t.Width == c.Width && t.Height == c.Height))
                        testList.Add(c);
                }

                foreach (var (w, h) in testList)
                {
                    try
                    {
                        cap.Set(VideoCaptureProperties.FrameWidth, w);
                        cap.Set(VideoCaptureProperties.FrameHeight, h);
                        if (_preferredFormat == CaptureFormat.MJPG)
                            cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                        else if (_preferredFormat == CaptureFormat.YUY2)
                            cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('Y', 'U', 'Y', '2'));
                        else
                            cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('N', 'V', '1', '2'));

                        double actualW = cap.Get(VideoCaptureProperties.FrameWidth);
                        double actualH = cap.Get(VideoCaptureProperties.FrameHeight);

                        if (Math.Abs(actualW - w) < 10 && Math.Abs(actualH - h) < 10)
                        {
                            string resStr = $"{w}x{h}";
                            if (!resList.Contains(resStr))
                                resList.Add(resStr);
                        }
                    }
                    catch { }
                }

                if (resList.Count == 0)
                {
                    int fallbackW = defaultWidth > 0 ? (int)defaultWidth : 1280;
                    int fallbackH = defaultHeight > 0 ? (int)defaultHeight : 720;
                    resList.Add($"{fallbackW}x{fallbackH}");
                }

                // 异步落盘缓存，保证下一次启动 0 毫秒即时载入
                SaveCameraSetting(s => s.SupportedResolutions = new List<string>(resList));
            }

            // 一次性更新 UI 分辨率下拉列表，避免多次 Dispatcher 上下文切换
            Application.Current?.Dispatcher.Invoke(() =>
            {
                SupportedResolutions.Clear();
                SupportedRecordResolutions.Clear();
                SupportedRecordResolutions.Add("与采集分辨率一致");
                foreach (var r in resList)
                {
                    SupportedResolutions.Add(r);
                    SupportedRecordResolutions.Add(r);
                }
            });

            // 2. 确定初始运行分辨率
            int targetWidth = 0;
            int targetHeight = 0;
            double targetFps = defaultFps > 0 ? defaultFps : 30;

            string savedRes = camConfig?.Resolution ?? ConfigurationManager.GetSetting($"Camera_{configKey}_Resolution");
            if (string.IsNullOrEmpty(savedRes) && Width > 0 && Height > 0)
            {
                savedRes = $"{(int)Width}x{(int)Height}";
            }

            bool appliedSavedRes = false;
            if (!string.IsNullOrEmpty(savedRes))
            {
                var parts = savedRes.Split('x');
                if (parts.Length == 2 && double.TryParse(parts[0], out double sw) && double.TryParse(parts[1], out double sh))
                {
                    string resStr = $"{(int)sw}x{(int)sh}";
                    if (resList.Contains(resStr))
                    {
                        targetWidth = (int)sw;
                        targetHeight = (int)sh;
                        _selectedResolution = resStr;
                        appliedSavedRes = true;
                        Logger.LogInfo($"[CameraService #{Index}] 应用已保存/预置分辨率: {resStr}");
                    }
                }
            }

            if (!appliedSavedRes)
            {
                if (resList.Contains("1280x720"))
                {
                    targetWidth = 1280;
                    targetHeight = 720;
                }
                else
                {
                    var closest = FindClosestResolutionTo720p();
                    targetWidth = closest.Width > 0 ? closest.Width : 1280;
                    targetHeight = closest.Height > 0 ? closest.Height : 720;
                }
                _selectedResolution = $"{targetWidth}x{targetHeight}";
                Logger.LogInfo($"[CameraService #{Index}] 采用推荐/默认分辨率: {_selectedResolution}");
            }

            WidthOrigin = targetWidth;
            HeightOrigin = targetHeight;
            FpsOrigin = targetFps;
            Width = targetWidth;
            Height = targetHeight;
            Fps = targetFps;

            RaisePropertyChanged(nameof(SelectedResolution));

            // 读取已保存的录像分辨率配置
            string savedRecordRes = camConfig?.RecordResolution ?? ConfigurationManager.GetSetting($"Camera_{configKey}_RecordResolution");
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
            if (camConfig?.RecordFps.HasValue == true)
            {
                RecordFps = camConfig.RecordFps.Value;
            }
            else
            {
                string savedRecordFps = ConfigurationManager.GetSetting($"Camera_{configKey}_RecordFps");
                if (!string.IsNullOrEmpty(savedRecordFps) && int.TryParse(savedRecordFps, out int rfps))
                {
                    RecordFps = rfps;
                }
            }

            // 读取已保存的水印文字配置
            string savedCustomText = camConfig?.CustomText ?? ConfigurationManager.GetSetting($"Camera_{configKey}_CustomText");
            if (!string.IsNullOrEmpty(savedCustomText))
            {
                CustomOverlayText = savedCustomText;
            }

            // 读取已保存的停用状态
            bool? savedDisabled = camConfig?.IsManualDisabled;
            if (!savedDisabled.HasValue && bool.TryParse(ConfigurationManager.GetSetting($"Camera_{configKey}_IsManualDisabled"), out bool dis))
            {
                savedDisabled = dis;
            }
            if (savedDisabled == true)
            {
                _isManualDisabled = true;
            }

            // 读取已保存的翻转配置
            bool? savedFlipH = camConfig?.IsFlipHorizontal;
            if (!savedFlipH.HasValue && bool.TryParse(ConfigurationManager.GetSetting($"Camera_{configKey}_IsFlipHorizontal"), out bool sfh))
            {
                savedFlipH = sfh;
            }
            if (savedFlipH == true)
            {
                _isFlipHorizontal = true;
                if (_flipProcessor != null) _flipProcessor.FlipHorizontal = true;
                RaisePropertyChanged(nameof(IsFlipHorizontal));
            }

            bool? savedFlipV = camConfig?.IsFlipVertical;
            if (!savedFlipV.HasValue && bool.TryParse(ConfigurationManager.GetSetting($"Camera_{configKey}_IsFlipVertical"), out bool sfv))
            {
                savedFlipV = sfv;
            }
            if (savedFlipV == true)
            {
                _isFlipVertical = true;
                if (_flipProcessor != null) _flipProcessor.FlipVertical = true;
                RaisePropertyChanged(nameof(IsFlipVertical));
            }

            // 读取已保存的画面比例拉伸设置（默认保持比例 Uniform）
            string savedStretch = camConfig?.StretchMode ?? ConfigurationManager.GetSetting($"Camera_{configKey}_StretchMode");
            if (!string.IsNullOrEmpty(savedStretch) && Enum.TryParse<Stretch>(savedStretch, true, out var st))
            {
                _videoStretch = st;
            }
            else
            {
                _videoStretch = Stretch.Uniform;
            }
            RaisePropertyChanged(nameof(VideoStretch));
            RaisePropertyChanged(nameof(IsStretchUniform));
            RaisePropertyChanged(nameof(IsStretchFill));
            RaisePropertyChanged(nameof(IsStretchUniformToFill));

            // 将摄像头设置回最终确定的分辨率
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

                string configKey = GetConfigKey();
                AppSettings.CameraSettings camConfig = null;
                AppSettingsService.Instance.Current.Cameras.TryGetValue(configKey, out camConfig);

                bool formatSet = false;
                // 如果配置中缓存了该设备的最优格式，优先直接应用，消除试探
                if (!string.IsNullOrEmpty(camConfig?.PreferredFormat) && Enum.TryParse<CaptureFormat>(camConfig.PreferredFormat, out var savedFormat))
                {
                    _preferredFormat = savedFormat;
                    if (_preferredFormat == CaptureFormat.MJPG)
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                    else if (_preferredFormat == CaptureFormat.YUY2)
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('Y', 'U', 'Y', '2'));
                    else if (_preferredFormat == CaptureFormat.NV12)
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('N', 'V', '1', '2'));

                    if (cap.IsOpened() && DecodeFourCC(cap.Get(VideoCaptureProperties.FourCC)) == _preferredFormat.ToString())
                    {
                        formatSet = true;
                        Logger.LogInfo($"摄像头 {index} 快速命中缓存格式: {_preferredFormat}");
                    }
                }

                if (!formatSet && (!_formatChecked || _preferredFormat == CaptureFormat.MJPG))
                {
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                    if (cap.IsOpened() && DecodeFourCC(cap.Get(VideoCaptureProperties.FourCC)) == "MJPG")
                    {
                        formatSet = true;
                        _preferredFormat = CaptureFormat.MJPG;
                        Logger.LogInfo($"摄像头 {index} 成功协商并使用 MJPG 格式");
                    }
                }

                if (!formatSet && (!_formatChecked || _preferredFormat == CaptureFormat.YUY2))
                {
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('Y', 'U', 'Y', '2'));
                    if (cap.IsOpened() && DecodeFourCC(cap.Get(VideoCaptureProperties.FourCC)) == "YUY2")
                    {
                        formatSet = true;
                        _preferredFormat = CaptureFormat.YUY2;
                        Logger.LogInfo($"摄像头 {index} 成功协商并使用 YUY2 格式");
                    }
                }

                if (!formatSet && (!_formatChecked || _preferredFormat == CaptureFormat.NV12))
                {
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('N', 'V', '1', '2'));
                    if (cap.IsOpened() && DecodeFourCC(cap.Get(VideoCaptureProperties.FourCC)) == "NV12")
                    {
                        formatSet = true;
                        _preferredFormat = CaptureFormat.NV12;
                        Logger.LogInfo($"摄像头 {index} 成功协商并使用 NV12 格式");
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
                SaveCameraSetting(s => s.PreferredFormat = _preferredFormat.ToString());

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
                    _isFirstFrame = true;
                    RefreshFrame();
                    Infrastructure.DeviceLockManager.ReleaseDevice(DevicePath, index);
                    return true;
                }

                ShowVideoInfo();
                Fps = FpsOrigin;

                _previewController.SetCameraFps(Fps);

                _isFirstFrame = true;
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
                }
                Infrastructure.DeviceLockManager.ReleaseDevice(DevicePath, Index);

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

                // 拔除设备后及时释放设备占用锁，避免后续热插拔重连被死锁
                Infrastructure.DeviceLockManager.ReleaseDevice(DevicePath, Index);
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

                        // 如果开启了录像且录像服务未就绪/未运行，在首次成功捕获正常画面帧后自动开启录像（增加 _isStartingSaving 防抖，防止多帧并发风暴）
                        if (isSave && _recorder != null && !_recorder.IsRecording && !_isStartingSaving)
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

                        // 采集帧率监控 (已彻底移除实时帧循环中的同步磁盘 IO 检查)
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
            if (_isStartingSaving) return;
            _isStartingSaving = true;

            try
            {
                if (_recorder == null) return;
                if (_recorder.IsRecording) return;

                if (cap == null || cap.IsDisposed || !cap.IsOpened() || !IsConnected)
                {
                    Logger.LogInfo($"[CameraService #{Index}] 摄像头 {Name} 尚未成功打开或当前处于离线状态，暂缓开启录像。");
                    return;
                }

                if (!StorageManager.Instance.CanStartRecording(out string failureReason))
                {
                    Logger.LogWarning($"[CameraService #{Index}] 无法开启录像: {failureReason}");
                    isSave = false;
                    return;
                }

                // 解析录像分辨率与输入分辨率（安全保底）
                int inputW = Width > 0 ? (int)Width : 1280;
                int inputH = Height > 0 ? (int)Height : 720;
                int recordW = inputW;
                int recordH = inputH;

                if (SelectedRecordResolution != "与采集分辨率一致")
                {
                    var parts = SelectedRecordResolution.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int rw) && int.TryParse(parts[1], out int rh) && rw > 0 && rh > 0)
                    {
                        recordW = rw;
                        recordH = rh;
                    }
                }

                // 偶数保护 (YUV420p 色彩空间必须使用偶数分辨率，防止 FFmpeg 初始化报错)
                if (inputW % 2 != 0) inputW--;
                if (inputH % 2 != 0) inputH--;
                if (recordW % 2 != 0) recordW--;
                if (recordH % 2 != 0) recordH--;

                _recordingOptions.InputWidth = inputW;
                _recordingOptions.InputHeight = inputH;
                _recordingOptions.Width = recordW;
                _recordingOptions.Height = recordH;
                // ⚠️ 关键: 这里设置的是"管道帧率"（= RecordFps），而非相机原始帧率(Fps)。
                // 原因: RefreshFrame 中已按 RecordFps 对管道进行节流，
                //       录制器实际接收到的帧率 ≈ RecordFps，而非 Fps。
                _recordingOptions.Fps = Math.Max(1, RecordFps);
                _recordingOptions.RecordFps = RecordFps;
                _recordingOptions.SaveDirectory = saveDirectory;
                _recordingOptions.SegmentDuration = SaveInterval;
                _recordingOptions.CustomText = CustomOverlayText;
                _recordingOptions.OverlayTextSize = OverlayTextSize;
     
                _recorder.StartRecording(null, inputW, inputH, recordW, recordH, RecordFps, Quality);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[CameraService #{Index}] 启动录像异常: {ex.Message}", ex);
            }
            finally
            {
                _isStartingSaving = false;
            }
        }

        private void StopSaving()
        {
            _recorder?.StopRecording();
        }

        // ═══════════════════════════════════════════════════════════
        // 核心重构说明:
        // 原 CheckDiskSpace()、BatchCleanUpDiskSpace()、CalcDiskSpaceEnough() 
        // 曾导致严重跨盘误删 D 盘 788 个历史录像并冻结实时采集管线，
        // 现已全部重构升级并迁移至独立的全局后台服务 StorageManager.cs。
        // ═══════════════════════════════════════════════════════════

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
                finally
                {
                    Infrastructure.DeviceLockManager.ReleaseDevice(DevicePath, Index);
                }

                // 重新创建 VideoCapture
                try
                {
                    if (!string.IsNullOrEmpty(DevicePath))
                    {
                        var devices = camera.Infrastructure.CameraDeviceEnumerator.EnumerateCameras();
                        var match = devices.Find(d => string.Equals(d.DevicePath, DevicePath, StringComparison.OrdinalIgnoreCase))
                                    ?? devices.Find(d => d.Name == Name);
                        if (match == null)
                        {
                            Logger.LogWarning($"[CameraService] 尝试修改摄像头 {Name} 分辨率或重载，但该物理设备已被移除，中止操作");
                            _continuousReadFailCount = 6;
                            RaisePropertyChanged(nameof(IsConnected));
                            RefreshFrame();
                            return;
                        }
                        if (DevicePath != match.DevicePath)
                        {
                            Logger.LogInfo($"[CameraService] 摄像头 {Name} 路径自适应纠正: {DevicePath} -> {match.DevicePath}");
                            DevicePath = match.DevicePath;
                        }
                        if (Index != match.Index)
                        {
                            Logger.LogInfo($"[CameraService] 摄像头 {Name} 索引自适应纠正: {Index} -> {match.Index}");
                            Index = match.Index;
                        }
                    }

                    if (!Infrastructure.DeviceLockManager.TryAcquireDevice(DevicePath, Index))
                    {
                        Logger.LogWarning($"[CameraService #{Index}] 设备已被其它通道锁定，无法打开 (DevicePath: {DevicePath})");
                        _continuousReadFailCount = 6;
                        RaisePropertyChanged(nameof(IsConnected));
                        RefreshFrame();
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
                            SaveCameraSetting(s => s.Resolution = actualResStr);
                            ConfigurationManager.SaveSetting($"Camera_{GetConfigKey()}_Resolution", actualResStr);
                        }

                        if (_selectedResolution != actualResStr)
                        {
                            _selectedResolution = actualResStr;
                            RaisePropertyChanged(nameof(SelectedResolution));
                        }
                    }

                    _previewController.SetCameraFps(Fps);
                    _isFirstFrame = true;
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
                    StorageManager.Instance.StorageWarning -= OnStorageWarningForward;
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
                        _previewController = null;  // ★ Fix P1-1: 置 null 帮助 GC 回收循环引用

                        // ★ Fix P1-2: 显式 Dispose pipeline 所有 processor 并置 null
                        // 原代码只 Dispose _claheProcessor 和 _timestampOverlayProcessor,
                        // _contrastLuminanceProcessor 和 _recordingProcessor 未 Dispose,
                        // _pipeline 字段本身未置 null,4 路 camera × 4 个 processor 累积。
                        if (_pipeline != null)
                        {
                            var processors = _pipeline.Processors;
                            for (int i = 0; i < processors.Count; i++)
                            {
                                if (processors[i] is IDisposable disp)
                                {
                                    try { disp.Dispose(); } catch { }
                                }
                            }
                            _pipeline = null;
                        }
                        _claheProcessor = null;
                        _contrastLuminanceProcessor = null;
                        _flipProcessor = null;
                        _timestampOverlayProcessor = null;
                        _recordingProcessor = null;

                        if (_Frame != null)
                        {
                            _Frame.Dispose();
                            _Frame = null;
                        }

                        // ★ Fix P0-1: 清理 _latestFrame 槽位中可能滞留的帧,避免被 finalize 漏掉
                        var pendingLatestFrame = Interlocked.Exchange(ref _latestFrame, null);
                        if (pendingLatestFrame != null && !pendingLatestFrame.IsDisposed)
                        {
                            try { Infrastructure.MatPool.Return(pendingLatestFrame); }
                            catch { /* Dispose 路径不再抛 */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"释放 CameraService 资源异常: {ex.Message}");
                }
            }
            else
            {
                // 终结器 (Finalizer) 兜底保护原生资源
                try
                {
                    Infrastructure.DeviceLockManager.ReleaseDevice(DevicePath, Index);
                    if (cap != null)
                    {
                        if (cap.IsOpened()) cap.Release();
                        cap.Dispose();
                        cap = null;
                    }
                }
                catch { }
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
