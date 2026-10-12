using ImTools;
using MaterialDesignColors;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using camera.Recording;
using camera.Encoding;

namespace camera.ViewModels
{
    public class MainWindowViewModel : BindableBase, IDisposable
    {

        private string _title = "监控";
        public string Title
        {
            get { return _title; }
            set { SetProperty(ref _title, value); }
        }

        private bool _isSave = false;
        public bool isSave
        {
            get { return _isSave; }
            set
            {
                SetProperty(ref _isSave, value);
                OnIsSaveChanged();
            }
        }

        private double _Opacity = 1.0;
        public double Opacity
        {
            get { return _Opacity; }
            set { SetProperty(ref _Opacity, value); }
        }

        private void OnIsSaveChanged()
        {
            if (isSave)
            {
                foreach (var camera in Cameras)
                {
                    camera.isSave = true;
                }
            }
            else
            {
                foreach (var camera in Cameras)
                {
                    camera.isSave = false;
                }
            }
            UpdateDiskRecordingStatus();
            UpdateActiveCodecStatus();
        }

        private bool _overwriteOldFiles = true;
        public bool OverwriteOldFiles
        {
            get { return _overwriteOldFiles; }
            set
            {
                if (_overwriteOldFiles != value)
                {
                    SetProperty(ref _overwriteOldFiles, value);
                    OnSaveModeChanged();
                }
            }
        }


        void OnSaveModeChanged()
        {
            StorageManager.Instance.OverwriteOldFiles = OverwriteOldFiles;
            AppSettingsService.Instance.Update(s => s.OverwriteOldFiles = OverwriteOldFiles);
            foreach (var camera in Cameras)
            {
                camera.OverwriteOldFiles = OverwriteOldFiles;
            }
            UpdateDiskRecordingStatus();
        }



        private int _DiskSpaceThresholdInput = 10240;
        public int DiskSpaceThresholdInput
        {
            get { return _DiskSpaceThresholdInput; }
            set
            {
                if (SetProperty(ref _DiskSpaceThresholdInput, value))
                {
                    UpdateDiskSpaceThresholdStatus();
                }
            }
        }

        private string _diskSpaceThresholdStatusText = string.Empty;
        public string DiskSpaceThresholdStatusText
        {
            get => _diskSpaceThresholdStatusText;
            set => SetProperty(ref _diskSpaceThresholdStatusText, value);
        }

        private string _diskSpaceThresholdStatusColor = "#2E7D32";
        public string DiskSpaceThresholdStatusColor
        {
            get => _diskSpaceThresholdStatusColor;
            set => SetProperty(ref _diskSpaceThresholdStatusColor, value);
        }

        private void UpdateDiskSpaceThresholdStatus()
        {
            if (_DiskSpaceThresholdInput != _DiskSpaceThreshold)
            {
                DiskSpaceThresholdStatusText = $"待确认: {_DiskSpaceThresholdInput} MB (按回车或点确定)";
                DiskSpaceThresholdStatusColor = "#E65100"; // 橙色提示待确认
            }
            else
            {
                DiskSpaceThresholdStatusText = $"✓ 已生效: {_DiskSpaceThreshold} MB";
                DiskSpaceThresholdStatusColor = "#2E7D32"; // 绿色正常
            }
        }

        private DelegateCommand _ConfirmDiskThresholdCommand;
        public DelegateCommand ConfirmDiskThresholdCommand =>
            _ConfirmDiskThresholdCommand ?? (_ConfirmDiskThresholdCommand = new DelegateCommand(ExecuteConfirmDiskThresholdCommand));

        private void ExecuteConfirmDiskThresholdCommand()
        {
            DiskSpaceThreshold = DiskSpaceThresholdInput;
            StorageManager.Instance.DiskSpaceThresholdMb = DiskSpaceThreshold;
            
            // 立即保存至 JSON 与 App.config 双重持久化
            AppSettingsService.Instance.Update(s => s.DiskSpaceThreshold = DiskSpaceThreshold);
            AppSettingsService.Instance.SaveSync();
            ConfigurationManager.SaveSetting("DiskSpaceThreshold", DiskSpaceThreshold.ToString());

            UpdateDiskSpaceThresholdStatus();
            UpdateDiskRecordingStatus();
            Logger.LogInfo($"[VM] 磁盘空间策略阈值已生效并持久化: {DiskSpaceThreshold} MB");
        }

        private int _DiskSpaceThreshold = 10240;
        public int DiskSpaceThreshold
        {
            get { return _DiskSpaceThreshold; }
            set
            {
                if (SetProperty(ref _DiskSpaceThreshold, value))
                {
                    OnDiskSpaceThresholdChanged();
                }
            }
        }

        void OnDiskSpaceThresholdChanged()
        {
            StorageManager.Instance.DiskSpaceThresholdMb = DiskSpaceThreshold;
            AppSettingsService.Instance.Update(s => s.DiskSpaceThreshold = DiskSpaceThreshold);
            foreach (var camera in Cameras)
            {
                camera.DiskThreshold = DiskSpaceThreshold;
            }
            UpdateDiskRecordingStatus();
        }

        private int _Quality = 30;
        public int Quality
        {
            get { return _Quality; }
            set { SetProperty(ref _Quality, value); OnQualityChanged(); }
        }

        private void OnQualityChanged()
        {
            foreach (var camera in Cameras)
            {
                camera.Quality = Quality;
            }
            UpdateDiskRecordingStatus();
        }

        private bool _forceCpuEncoding = false;
        public bool ForceCpuEncoding
        {
            get => _forceCpuEncoding;
            set
            {
                if (SetProperty(ref _forceCpuEncoding, value))
                {
                    VideoEncoderFactory.ForceCpuEncoding = value;
                    if (!value)
                    {
                        VideoEncoderFactory.ResetFailedEncoders();
                    }
                    AppSettingsService.Instance.Update(s => s.ForceCpuEncoding = value);
                    AppSettingsService.Instance.SaveSync();
                    ConfigurationManager.SaveSetting("ForceCpuEncoding", value.ToString());

                    // 全链路联动控制优化项 1 (预览下采样), 2 (图像增强与硬件变换), 3 (录像编码与回放解码)
                    foreach (var cam in Cameras)
                    {
                        cam.UseGpuAcceleration = !value;
                    }

                    Logger.LogInfo($"[VM] 全局硬件加速模式切换: {(value ? "全部使用 CPU (已关闭 GPU 硬件加速: 1. CPU 预览双线性下采样; 2. CPU 内存矩阵翻转与图像变换; 3. CPU libx264 软编与 CPU 软件回放)" : "全链路启用 GPU 硬件加速 (1. GPU 纹理硬件下采样; 2. GPU Direct3D 硬件变换; 3. GPU 硬件编解码与硬件回放)")}");

                    if (isSave)
                    {
                        foreach (var cam in Cameras)
                        {
                            System.Threading.Tasks.Task.Run(() => cam.RestartSaving());
                        }
                    }
                    UpdateActiveCodecStatus();
                }
            }
        }

        private bool _ConsoleStatus = false;
        public bool ConsoleStatus
        {
            get { return _ConsoleStatus; }
            set { SetProperty(ref _ConsoleStatus, value); }
        }



        private int _SaveInterval = 1;
        public int SaveInterval
        {
            get { return _SaveInterval; }
            set { SetProperty(ref _SaveInterval, value); OnSaveIntervalChanged(); }
        }

        private void OnSaveIntervalChanged()
        {
            foreach (var camera in Cameras)
            {
                camera.SaveInterval = TimeSpan.FromMinutes(SaveInterval);
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
            set
            {
                SetProperty(ref contrast, value);
                OnContrastChanged();
            }
        }

        private void OnContrastChanged()
        {
            foreach (var camera in Cameras)
            {
                camera.Contrast = Contrast;
            }
        }

        /// <summary>
        /// 亮度
        /// </summary>
        private double luminance = 0.0;
        public double Luminance
        {
            get { return luminance; }
            set
            {
                SetProperty(ref luminance, value);
                OnLuminanceChanged();
            }
        }

        private void OnLuminanceChanged()
        {
            foreach (var camera in Cameras)
            {
                camera.Luminance = Luminance;
            }
        }

        /// <summary>
        /// 全局水印大小比例
        /// </summary>
        private double _overlayTextSize = 0.3;
        public double OverlayTextSize
        {
            get { return _overlayTextSize; }
            set
            {
                if (SetProperty(ref _overlayTextSize, value))
                {
                    OnOverlayTextSizeChanged();
                }
            }
        }

        private void OnOverlayTextSizeChanged()
        {
            foreach (var camera in Cameras)
            {
                camera.OverlayTextSize = OverlayTextSize;
            }
        }

        private bool _IsGrayHomogenization = false;
        public bool IsGrayHomogenization
        {
            get { return _IsGrayHomogenization; }
            set
            {
                SetProperty(ref _IsGrayHomogenization, value);
                OnIsGrayHomogenizationChanged();
            }
        }

        private void OnIsGrayHomogenizationChanged()
        {
            foreach (var camera in Cameras)
            {
                camera.IsGrayHomogenization = IsGrayHomogenization;
            }
        }

        private int _PreviewFps = 15;
        public int PreviewFps
        {
            get { return _PreviewFps; }
            set { SetProperty(ref _PreviewFps, value); OnPreviewFpsChanged(); }
        }

        private void OnPreviewFpsChanged()
        {
            foreach (var camera in Cameras)
            {
                camera.PreviewFps = PreviewFps;
            }
        }

        private int _RecordFps = 15;
        public int RecordFps
        {
            get { return _RecordFps; }
            set { SetProperty(ref _RecordFps, value); OnRecordFpsChanged(); }
        }

        private void OnRecordFpsChanged()
        {
            // 移到通道配置，不再全局覆盖各个相机的独立配置项
        }

        private int _MaxPreviewWidth = 640;
        public int MaxPreviewWidth
        {
            get { return _MaxPreviewWidth; }
            set { SetProperty(ref _MaxPreviewWidth, value); OnMaxPreviewWidthChanged(); RaisePropertyChanged(nameof(SelectedPreviewWidth)); }
        }

        public ObservableCollection<string> PreviewWidthOptions { get; } = new();

        /// <summary>
        /// 从所有摄像头的 SupportedResolutions 中提取宽度，构建预览宽度选项列表。
        /// 格式与采集分辨率一致，如 "1920x1080"、"1280x720"。
        /// </summary>
        public void RefreshPreviewWidthOptions()
        {
            if (Application.Current == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                var allResolutions = new HashSet<string>();
                foreach (var cam in Cameras)
                {
                    foreach (var res in cam.SupportedResolutions)
                    {
                        allResolutions.Add(res);
                    }
                }

                PreviewWidthOptions.Clear();
                // 按宽度降序排列
                var sorted = allResolutions
                    .Select(r => { var p = r.Split('x'); return (Label: r, Width: int.TryParse(p[0], out int w) ? w : 0); })
                    .Where(x => x.Width > 0)
                    .OrderByDescending(x => x.Width)
                    .ToList();

                foreach (var item in sorted)
                {
                    PreviewWidthOptions.Add(item.Label);
                }

                // 如果当前选中值不在列表中，自动选择最接近的
                if (!string.IsNullOrEmpty(SelectedPreviewWidth) && !PreviewWidthOptions.Contains(SelectedPreviewWidth))
                {
                    RaisePropertyChanged(nameof(SelectedPreviewWidth));
                }
            });
        }

        public string SelectedPreviewWidth
        {
            get
            {
                // 找到宽度最接近 MaxPreviewWidth 的选项
                string best = null;
                int bestDiff = int.MaxValue;
                foreach (var opt in PreviewWidthOptions)
                {
                    var parts = opt.Split('x');
                    if (parts.Length >= 1 && int.TryParse(parts[0], out int w))
                    {
                        int diff = Math.Abs(w - _MaxPreviewWidth);
                        if (diff < bestDiff) { bestDiff = diff; best = opt; }
                    }
                }
                return best;
            }
            set
            {
                if (value != null)
                {
                    var parts = value.Split('x');
                    if (parts.Length >= 1 && int.TryParse(parts[0], out int w))
                    {
                        MaxPreviewWidth = w;
                    }
                }
            }
        }

        private void OnMaxPreviewWidthChanged()
        {
            foreach (var camera in Cameras)
            {
                camera.MaxPreviewWidth = MaxPreviewWidth;
            }
        }

        #endregion

        /// <summary>
        /// 摄像头最大帧率，用于滑块上限
        /// </summary>
        private int _MaxFps = 30;
        public int MaxFps
        {
            get { return _MaxFps; }
            set { SetProperty(ref _MaxFps, value); }
        }

        //摄像头集合
        private ObservableCollection<CameraService> _Cameras = new();
        private bool _isInitializing = false;

        // 热插拔防抖：将短时间内连发的多条 WM_DEVICECHANGE 消息合并为一次处理
        private System.Threading.Timer _deviceChangeDebounceTimer;
        private readonly object _deviceChangeLock = new object();
        private volatile bool _isProcessingDeviceChange = false;
        public bool IsInitializing
        {
            get { return _isInitializing; }
            set { SetProperty(ref _isInitializing, value); }
        }

        private string _loadingStatusText = "正在检测摄像头设备...";
        public string LoadingStatusText
        {
            get { return _loadingStatusText; }
            set { SetProperty(ref _loadingStatusText, value); }
        }

        public ObservableCollection<CameraService> Cameras
        {
            get { return _Cameras; }
            set { SetProperty(ref _Cameras, value); }
        }

        private bool _IsSingleCameraMode = false;
        public bool IsSingleCameraMode
        {
            get { return _IsSingleCameraMode; }
            set { SetProperty(ref _IsSingleCameraMode, value); }
        }

        private CameraService _SelectedCamera;
        public CameraService SelectedCamera
        {
            get { return _SelectedCamera; }
            set { SetProperty(ref _SelectedCamera, value); }
        }

        private CameraService _Cam;
        public CameraService Cam
        {
            get => _Cam ?? SelectedConfigCamera;
            set => SetProperty(ref _Cam, value);
        }

        private DelegateCommand<CameraService> _ToggleMaximizeCommand;
        public DelegateCommand<CameraService> ToggleMaximizeCommand =>
            _ToggleMaximizeCommand ?? (_ToggleMaximizeCommand = new DelegateCommand<CameraService>(ExecuteToggleMaximizeCommand));

        private void ExecuteToggleMaximizeCommand(CameraService camera)
        {
            if (camera == null) return;

            if (IsSingleCameraMode && SelectedCamera == camera)
            {
                ExecuteBackToGridCommand();
            }
            else
            {
                if (SelectedCamera != null) SelectedCamera.IsHighDefinition = false;
                SelectedCamera = camera;
                SelectedCamera.IsHighDefinition = true;
                IsSingleCameraMode = true;
            }
        }

        private DelegateCommand _BackToGridCommand;
        public DelegateCommand BackToGridCommand =>
            _BackToGridCommand ?? (_BackToGridCommand = new DelegateCommand(ExecuteBackToGridCommand));

        private void ExecuteBackToGridCommand()
        {
            if (SelectedCamera != null) SelectedCamera.IsHighDefinition = false;
            IsSingleCameraMode = false;
            SelectedCamera = null;
        }

        private DelegateCommand _escapeKeyCommand;
        public DelegateCommand EscapeKeyCommand =>
            _escapeKeyCommand ?? (_escapeKeyCommand = new DelegateCommand(() =>
            {
                if (IsPlaybackMode)
                {
                    IsPlaybackMode = false;
                }
                else if (IsSingleCameraMode)
                {
                    ExecuteBackToGridCommand();
                }
                else if (ConsoleStatus)
                {
                    ConsoleStatus = false;
                }
            }));

        #region 主题切换 (Day/Night Theme)

        private bool _isDarkTheme;
        public bool IsDarkTheme
        {
            get => _isDarkTheme;
            set
            {
                if (SetProperty(ref _isDarkTheme, value))
                {
                    ApplyTheme(value);
                    AppSettingsService.Instance.Update(s => s.IsDarkTheme = value);
                    ConfigurationManager.SaveSetting("IsDarkTheme", value.ToString());
                }
            }
        }

        public void ApplyTheme(bool isDark)
        {
            try
            {
                void DoApply()
                {
                    var skinType = isDark ? HandyControl.Data.SkinType.Dark : HandyControl.Data.SkinType.Default;
                    var skin = HandyControl.Tools.ResourceHelper.GetSkin(skinType);
                    if (Application.Current != null)
                    {
                        var appResources = Application.Current.Resources;
                        bool replaced = false;
                        for (int i = 0; i < appResources.MergedDictionaries.Count; i++)
                        {
                            var dict = appResources.MergedDictionaries[i];
                            if (dict.Source != null && (dict.Source.OriginalString.Contains("SkinDefault") || dict.Source.OriginalString.Contains("SkinDark")))
                            {
                                appResources.MergedDictionaries[i] = skin;
                                replaced = true;
                                break;
                            }
                        }
                        if (!replaced && appResources.MergedDictionaries.Count > 0)
                        {
                            appResources.MergedDictionaries[0] = skin;
                        }

                        // 纠正 HandyControl 皮肤中 BackgroundBrush 始终为 #EEEEEE (浅灰白) 的库设计缺陷:
                        // 在深色模式下显式赋予纯正深邃黑底，白天浅色赋予现代清爽浅色
                        var bgBrush = new SolidColorBrush(isDark ? Color.FromRgb(0x12, 0x12, 0x14) : Color.FromRgb(0xF3, 0xF4, 0xF6));
                        var regionBrush = new SolidColorBrush(isDark ? Color.FromRgb(0x1C, 0x1C, 0x20) : Color.FromRgb(0xFF, 0xFF, 0xFF));
                        var borderBrush = new SolidColorBrush(isDark ? Color.FromRgb(0x3F, 0x3F, 0x46) : Color.FromRgb(0xD0, 0xD7, 0xDE));
                        var textBrush = new SolidColorBrush(isDark ? Color.FromRgb(0xFD, 0xFD, 0xFD) : Color.FromRgb(0x21, 0x21, 0x21));

                        appResources["BackgroundBrush"] = bgBrush;
                        appResources["RegionBrush"] = regionBrush;
                        appResources["BorderBrush"] = borderBrush;
                        appResources["PrimaryTextBrush"] = textBrush;

                        if (Application.Current.MainWindow != null)
                        {
                            HandyControl.Themes.Theme.SetSkin(Application.Current.MainWindow, skinType);
                            Application.Current.MainWindow.Resources["BackgroundBrush"] = bgBrush;
                            Application.Current.MainWindow.Resources["RegionBrush"] = regionBrush;
                            Application.Current.MainWindow.Resources["BorderBrush"] = borderBrush;
                            Application.Current.MainWindow.Resources["PrimaryTextBrush"] = textBrush;
                        }
                    }
                    Logger.LogInfo($"[Theme] 切换主题为: {(isDark ? "夜间深色模式 (Dark)" : "白天浅色模式 (Light)")}");
                }

                if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.Invoke(DoApply);
                }
                else
                {
                    DoApply();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[Theme] 切换主题失败", ex);
            }
        }

        #endregion

        #region 后台挂机渲染暂停 (最小化时零 GPU/CPU 渲染开销)

        public static bool IsUIRenderingPaused { get; set; } = false;

        #endregion

        #region 警示状态与托盘通知

        public event Action<string, string, bool>? TrayBalloonNotificationRequested;

        private bool _hasActiveAlert;
        public bool HasActiveAlert
        {
            get => _hasActiveAlert;
            set => SetProperty(ref _hasActiveAlert, value);
        }

        private string _alertMessage = string.Empty;
        public string AlertMessage
        {
            get => _alertMessage;
            set => SetProperty(ref _alertMessage, value);
        }

        private DelegateCommand _dismissAlertCommand;
        public DelegateCommand DismissAlertCommand =>
            _dismissAlertCommand ?? (_dismissAlertCommand = new DelegateCommand(() => HasActiveAlert = false));

        public void TriggerAlert(string title, string message, bool isWarning = true)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                HasActiveAlert = true;
                AlertMessage = message;
                TrayBalloonNotificationRequested?.Invoke(title, message, isWarning);
            });
        }

        public void CheckAndClearAlert()
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                bool diskOk = !StorageManager.Instance.IsStorageCritical;
                bool camerasOk = Cameras.All(c => c.IsManualDisabled || c.IsConnected);
                if (diskOk && camerasOk)
                {
                    HasActiveAlert = false;
                    AlertMessage = string.Empty;
                }
            });
        }

        #endregion

        #region 摄像头自愈看门狗

        private DispatcherTimer _cameraWatchdogTimer;
        private void InitializeWatchdogTimer()
        {
            _cameraWatchdogTimer = new DispatcherTimer(DispatcherPriority.Background);
            _cameraWatchdogTimer.Interval = TimeSpan.FromMilliseconds(1500);
            _cameraWatchdogTimer.Tick += (s, e) =>
            {
                if (_isInitializing || _disposed) return;
                // 仅当存在非手动禁用且离线的摄像头时，触发自愈重连检查
                if (Cameras.Any(c => !c.IsManualDisabled && !c.IsConnected))
                {
                    ExecuteDeviceChangeHandler();
                }
            };
            _cameraWatchdogTimer.Start();
        }

        #endregion

        public MainWindowViewModel()
        {
            init();
            LoadSettings();
            InitializePeriodicTimer();
            InitializeWatchdogTimer();

            // 订阅存储管理器非模态告警与状态更新
            StorageManager.Instance.StorageWarning += OnStorageWarningTriggered;
            StorageManager.Instance.StorageStatusUpdated += (s, e) => UpdateDiskRecordingStatus();

            Cameras.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                {
                    foreach (CameraService cam in e.NewItems)
                    {
                        cam.PropertyChanged += OnCameraPropertyChanged;
                    }
                }
                if (e.OldItems != null)
                {
                    foreach (CameraService cam in e.OldItems)
                    {
                        cam.PropertyChanged -= OnCameraPropertyChanged;
                    }
                }
                UpdateDiskRecordingStatus();
            };

            // ★ 自动保存:任何"持久化属性"被修改后,1 秒防抖落盘。
            // 此前必须等 SaveSettings() 在 Close 时才统一保存,期间崩溃/强杀 → 配置丢失。
            // 现在 setter 改值 → PropertyChanged 触发 → AppSettingsService.RequestSave() 调度落盘。
            this.PropertyChanged += OnPersistentPropertyChanged;
        }

        private void OnCameraPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CameraService.isSave) ||
                e.PropertyName == nameof(CameraService.RecordFps) ||
                e.PropertyName == nameof(CameraService.SelectedRecordResolution) ||
                e.PropertyName == nameof(CameraService.SelectedResolution) ||
                e.PropertyName == nameof(CameraService.Quality) ||
                e.PropertyName == nameof(CameraService.Width) ||
                e.PropertyName == nameof(CameraService.Height))
            {
                UpdateDiskRecordingStatus();
            }
            else if (e.PropertyName == nameof(CameraService.ActiveCodec) ||
                     e.PropertyName == nameof(CameraService.IsCodecDowngraded) ||
                     e.PropertyName == nameof(CameraService.IsRecording))
            {
                RaisePropertyChanged(nameof(IsGpuEncodingActive));
                RaisePropertyChanged(nameof(IsCpuEncodingActive));
                RaisePropertyChanged(nameof(IsEncodingInactive));
            }
            else if (e.PropertyName == nameof(CameraService.IsConnected))
            {
                var camera = sender as CameraService;
                if (camera != null)
                {
                    if (!camera.IsConnected && !camera.IsManualDisabled)
                    {
                        TriggerAlert("摄像头断开警告", $"⚠️ 摄像头 [{camera.Name}] 物理连接断开！", true);
                    }
                    else if (camera.IsConnected)
                    {
                        CheckAndClearAlert();
                        TrayBalloonNotificationRequested?.Invoke("设备恢复在线", $"摄像头 [{camera.Name}] 已自愈重连恢复正常！", false);
                    }
                }
            }
        }

        /// <summary>
        /// 需要自动落盘的属性名集合(同步原 SaveSettings() 写入的 key 列表)。
        /// 修改任何 setter 会经由 SetProperty 触发 PropertyChanged,
        /// 这里过滤出"持久化属性"再调度 RequestSave,1 秒防抖合并连续改动。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _persistKeys =
            new(System.StringComparer.Ordinal)
            {
                nameof(isSave),
                nameof(Opacity),
                nameof(Title),
                nameof(SystemTitle),
                nameof(OverwriteOldFiles),
                nameof(DiskSpaceThreshold),
                nameof(Quality),
                nameof(SaveInterval),
                nameof(MaxPreviewWidth),
                nameof(PreviewFps),
                nameof(RecordFps),
                nameof(Contrast),
                nameof(Luminance),
                nameof(IsGrayHomogenization),
                nameof(OverlayTextSize),
                nameof(MaxFps),
                nameof(ForceCpuEncoding),
                nameof(IsDarkTheme),
                // MinWindowWidth/MinWindowHeight 属于 MainWindow,在 MainWindow 自己处理
            };

        private void OnPersistentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == null) return;
            if (!_persistKeys.Contains(e.PropertyName)) return;

            AppSettingsService.Instance.Update(s =>
            {
                s.IsSave = isSave;
                s.Opacity = Opacity;
                s.Title = Title;
                s.SystemTitle = SystemTitle;
                s.OverwriteOldFiles = OverwriteOldFiles;
                s.DiskSpaceThreshold = DiskSpaceThreshold;
                s.Quality = Quality;
                s.SaveInterval = SaveInterval;
                s.MaxPreviewWidth = MaxPreviewWidth;
                s.PreviewFps = PreviewFps;
                s.RecordFps = RecordFps;
                s.Contrast = Contrast;
                s.Luminance = Luminance;
                s.IsGrayHomogenization = IsGrayHomogenization;
                s.OverlayTextSize = OverlayTextSize;
                s.MaxFps = MaxFps;
                s.ForceCpuEncoding = ForceCpuEncoding;
                s.IsDarkTheme = IsDarkTheme;
            });
        }


        void init()
        {
            Logger.LogInfo("开始后台异步初始化摄像头 (使用 DirectShow 设备枚举)...");
            IsInitializing = true;
            LoadingStatusText = "正在探测摄像头设备...";

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var devices = camera.Infrastructure.CameraDeviceEnumerator.EnumerateCameras();

                    // 为了防止 USB 探测顺序颠倒，按 DevicePath 排序保证排序一致性
                    devices.Sort((a, b) => string.Compare(a.DevicePath, b.DevicePath, StringComparison.OrdinalIgnoreCase));

                    Logger.LogInfo($"物理探测完成，共找到 {devices.Count} 个接入的摄像头设备");

                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        LoadingStatusText = $"检测到 {devices.Count} 个设备，正在依次加载通道...";
                    });

                    foreach (var device in devices)
                    {
                        if (_disposed)
                        {
                            Logger.LogInfo("检测到 ViewModel 已释放，中止摄像头加载。");
                            break;
                        }

                        try
                        {
                            Logger.LogInfo($"开始初始化物理通道: #{device.Index} - {device.Name} (唯一路径: {device.DevicePath})");
                            Application.Current?.Dispatcher.Invoke(() =>
                            {
                                LoadingStatusText = $"正在加载摄像头: {device.Name}...";
                            });

                            var camera = new CameraService();
                            camera.DevicePath = device.DevicePath; // 绑定唯一硬件路径
                            camera.Name = device.Name; // 使用物理设备名称显示
                            if (device.SupportedResolutions != null && device.SupportedResolutions.Count > 0)
                            {
                                camera.SetHardwareResolutions(device.SupportedResolutions);
                            }

                            // 在启动硬件采集前预先应用已持久化的参数（分辨率、帧率、水印等），一步到位，杜绝二次重新打开
                            ApplySettingsToCamera(camera);

                            // StartCapture 启动该相机，即使被占用也总是返回 true
                            if (camera.StartCapture(device.Index))
                            {
                                if (_disposed)
                                {
                                    camera.Dispose();
                                    break;
                                }

                                Application.Current?.Dispatcher.Invoke(() =>
                                {
                                    if (_disposed)
                                    {
                                        camera.Dispose();
                                        return;
                                    }

                                    Cameras.Add(camera);
                                    camera.DiskSpaceLow += Camera_DiskSpaceLow;

                                    Logger.LogInfo($"✓ 摄像头 #{device.Index} ({device.Name}) 加载成功 (当前在线: {camera.IsConnected})");
                                });

                                // 轻量错峰缓冲：仅需 60ms（原 600ms），给 USB Host Controller 端点分配提供缓冲
                                if (camera.IsConnected)
                                {
                                    System.Threading.Thread.Sleep(60);
                                }
                                else
                                {
                                    System.Threading.Thread.Sleep(30);
                                }
                            }
                            else
                            {
                                camera.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"加载物理摄像头 #{device.Index} 时发生异常", ex);
                        }
                    }

                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (_disposed) return;

                        // 按照用户之前自定义拖拽保存的顺序重新排列摄像头
                        ApplyCameraOrder();

                        Logger.LogInfo($"摄像头初始化完成，共找到 {Cameras.Count} 个活动/占位摄像头");
                        IsInitializing = false;

                        // 从第一个摄像头获取实际帧率作为滑块上限
                        if (Cameras.Count > 0)
                        {
                            MaxFps = (int)Cameras[0].Fps;
                            if (MaxFps <= 0) MaxFps = 30;
                            Logger.LogInfo($"摄像头最大帧率: {MaxFps}fps");
                            SelectedConfigCamera = Cameras[0];
                            RefreshPreviewWidthOptions();
                        }

                        if (Cameras.Count == 0)
                        {
                            Logger.LogWarning("未检测到可用的摄像头，程序将退出");
                            MessageBox.Show("未检测到可用的摄像头");
                            Application.Current.Shutdown();//退出自身
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError("异步初始化摄像头主任务发生异常", ex);
                    Application.Current?.Dispatcher.Invoke(() => { IsInitializing = false; });
                }
            });
        }

        private void OnStorageWarningTriggered(object? sender, string message)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                Logger.LogWarning($"[VM-Storage] 存储空间警告: {message}");
                DiskSpaceWarningText = $"⚠️ 磁盘警告: {message}";
                DiskSpaceWarningColor = "#C62828";
                if (StorageManager.Instance.IsStorageCritical)
                {
                    if (!OverwriteOldFiles)
                    {
                        isSave = false;
                    }
                    TriggerAlert("磁盘空间严重告警", $"⚠️ 磁盘余量低于安全阈值({DiskSpaceThreshold} MB)，已自动停止录像以保护数据！", true);
                }
            });
        }

        private void Camera_DiskSpaceLow(object? sender, string message)
        {
            OnStorageWarningTriggered(sender, message);
        }


        private DelegateCommand _Set;
        public DelegateCommand Set =>
            _Set ?? (_Set = new DelegateCommand(ExecuteSet));

        void ExecuteSet()
        {
            (SelectedConfigCamera ?? Cam)?.ChangeCameraInfo();
        }

        private DelegateCommand _ResetImageSettings;
        public DelegateCommand ResetImageSettings =>
            _ResetImageSettings ?? (_ResetImageSettings = new DelegateCommand(ExecuteResetImageSettings));

        void ExecuteResetImageSettings()
        {
            Contrast = 1.0;
            Luminance = 0.0;
        }

        private DelegateCommand _ShowConsole;
        public DelegateCommand ShowConsole =>
            _ShowConsole ?? (_ShowConsole = new DelegateCommand(ExecuteShowConsole));
        void ExecuteShowConsole()
        {
            if (ConsoleStatus)
            {
                Debug.WriteLine("关闭控制台");
                ConsoleStatus = false;
            }
            else
            {
                Debug.WriteLine("打开控制台");
                ConsoleStatus = true;
            }

        }


        //~MainWindowViewModel()
        //{
        //    MessageBox.Show("析构窗口");
        //}


        private DelegateCommand _ClosingCommand;
        public DelegateCommand ClosingCommand =>
            _ClosingCommand ?? (_ClosingCommand = new DelegateCommand(ExecuteClosingCommand));

        void ExecuteClosingCommand()
        {
            try { System.Windows.Application.Current?.MainWindow?.Hide(); } catch { }
            SaveSettings();
            Dispose(); // 释放所有资源
        }

        private DelegateCommand _ViewPlayback;
        public DelegateCommand ViewPlayback =>
            _ViewPlayback ?? (_ViewPlayback = new DelegateCommand(ExecuteViewPlayback));

        void ExecuteViewPlayback()
        {
            if (Cameras.Count == 0)
            {
                MessageBox.Show("当前未连接摄像头，无法查看回放。");
                return;
            }

            IsPlaybackMode = true;
            if (PlaybackSelectedCamera == null)
            {
                PlaybackSelectedCamera = Cameras[0];
            }
            LoadPlaybackFiles();
        }

        private DelegateCommand _backToMonitoringCommand;
        public DelegateCommand BackToMonitoringCommand =>
            _backToMonitoringCommand ?? (_backToMonitoringCommand = new DelegateCommand(() => IsPlaybackMode = false));

        private DelegateCommand _openRecordingFolderCommand;
        public DelegateCommand OpenRecordingFolderCommand =>
            _openRecordingFolderCommand ?? (_openRecordingFolderCommand = new DelegateCommand(() =>
            {
                try
                {
                    string baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedVideos");
                    if (!Directory.Exists(baseDir))
                    {
                        Directory.CreateDirectory(baseDir);
                    }
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = baseDir,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError($"打开录像文件夹失败: {ex.Message}", ex);
                }
            }));

        #region 回放功能相关的属性与命令

        private bool _isPlaybackMode = false;
        public bool IsPlaybackMode
        {
            get => _isPlaybackMode;
            set
            {
                if (SetProperty(ref _isPlaybackMode, value))
                {
                    if (value)
                    {
                        foreach (var cam in Cameras)
                        {
                            cam.IsPreviewEnabled = false;
                        }
                    }
                    else
                    {
                        foreach (var cam in Cameras)
                        {
                            cam.IsPreviewEnabled = true;
                        }
                        PlaybackUrl = null;
                    }
                }
            }
        }

        private DateTime _playbackSelectedDate = DateTime.Today;
        public DateTime PlaybackSelectedDate
        {
            get => _playbackSelectedDate;
            set
            {
                if (SetProperty(ref _playbackSelectedDate, value))
                {
                    LoadPlaybackFiles();
                }
            }
        }

        private CameraService _playbackSelectedCamera;
        public CameraService PlaybackSelectedCamera
        {
            get => _playbackSelectedCamera;
            set
            {
                if (SetProperty(ref _playbackSelectedCamera, value))
                {
                    LoadPlaybackFiles();
                }
            }
        }

        private double _playbackSpeed = 1.0;
        public double PlaybackSpeed
        {
            get => _playbackSpeed;
            set => SetProperty(ref _playbackSpeed, value);
        }

        private string _playbackUrl;
        public string PlaybackUrl
        {
            get => _playbackUrl;
            set => SetProperty(ref _playbackUrl, value);
        }

        private string _playbackState = "Stopped";
        public string PlaybackState
        {
            get => _playbackState;
            set => SetProperty(ref _playbackState, value);
        }

        public ObservableCollection<PlaybackFileModel> PlaybackFiles { get; } = new();

        public event Action<TimeSpan> PlaybackPositionRequest;

        private DelegateCommand<double?> _timelineClickCommand;
        public DelegateCommand<double?> TimelineClickCommand =>
            _timelineClickCommand ?? (_timelineClickCommand = new DelegateCommand<double?>(ExecuteTimelineClickCommand));

        private void ExecuteTimelineClickCommand(double? percent)
        {
            if (percent == null || PlaybackFiles.Count == 0) return;
            double totalSecondsInDay = 24.0 * 3600.0;
            double clickedSeconds = percent.Value * totalSecondsInDay;
            TimeSpan clickedTime = TimeSpan.FromSeconds(clickedSeconds);

            PlaybackFileModel targetFile = null;
            foreach (var file in PlaybackFiles)
            {
                TimeSpan fileStart = file.StartTime.TimeOfDay;
                TimeSpan fileEnd = file.EndTime.TimeOfDay;
                if (clickedTime >= fileStart && clickedTime <= fileEnd)
                {
                    targetFile = file;
                    break;
                }
            }

            if (targetFile != null)
            {
                if (PlaybackUrl != targetFile.FilePath)
                {
                    PlaybackUrl = targetFile.FilePath;
                }
                TimeSpan offset = clickedTime - targetFile.StartTime.TimeOfDay;
                PlaybackPositionRequest?.Invoke(offset);
            }
            else
            {
                PlaybackFileModel nextFile = null;
                foreach (var file in PlaybackFiles)
                {
                    if (file.StartTime.TimeOfDay >= clickedTime)
                    {
                        if (nextFile == null || file.StartTime.TimeOfDay < nextFile.StartTime.TimeOfDay)
                        {
                            nextFile = file;
                        }
                    }
                }
                if (nextFile != null)
                {
                    PlaybackUrl = nextFile.FilePath;
                    PlaybackPositionRequest?.Invoke(TimeSpan.Zero);
                }
            }
        }

        public void PlayNextFile()
        {
            if (PlaybackFiles.Count == 0 || string.IsNullOrEmpty(PlaybackUrl))
            {
                PlaybackState = "Stopped";
                return;
            }

            int currentIndex = -1;
            for (int i = 0; i < PlaybackFiles.Count; i++)
            {
                if (PlaybackFiles[i].FilePath == PlaybackUrl)
                {
                    currentIndex = i;
                    break;
                }
            }

            if (currentIndex >= 0 && currentIndex + 1 < PlaybackFiles.Count)
            {
                PlaybackUrl = PlaybackFiles[currentIndex + 1].FilePath;
                PlaybackState = "Playing";
            }
            else
            {
                PlaybackState = "Stopped";
            }
        }

        private DelegateCommand<string> _setPlaybackSpeedCommand;
        public DelegateCommand<string> SetPlaybackSpeedCommand =>
            _setPlaybackSpeedCommand ?? (_setPlaybackSpeedCommand = new DelegateCommand<string>(speedStr =>
            {
                if (double.TryParse(speedStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double speed))
                    PlaybackSpeed = speed;
            }));

        private DelegateCommand _speedUpCommand;
        public DelegateCommand SpeedUpCommand =>
            _speedUpCommand ?? (_speedUpCommand = new DelegateCommand(ExecuteSpeedUpCommand));

        private void ExecuteSpeedUpCommand()
        {
            if (PlaybackSpeed < 1.0) PlaybackSpeed = 1.0;
            else if (PlaybackSpeed < 2.0) PlaybackSpeed = 2.0;
            else if (PlaybackSpeed < 4.0) PlaybackSpeed = 4.0;
            else if (PlaybackSpeed < 8.0) PlaybackSpeed = 8.0;
            else if (PlaybackSpeed < 16.0) PlaybackSpeed = 16.0;
            else if (PlaybackSpeed < 32.0) PlaybackSpeed = 32.0;
            else if (PlaybackSpeed < 64.0) PlaybackSpeed = 64.0;
        }

        private DelegateCommand _speedDownCommand;
        public DelegateCommand SpeedDownCommand =>
            _speedDownCommand ?? (_speedDownCommand = new DelegateCommand(ExecuteSpeedDownCommand));

        private void ExecuteSpeedDownCommand()
        {
            if (PlaybackSpeed > 32.0) PlaybackSpeed = 32.0;
            else if (PlaybackSpeed > 16.0) PlaybackSpeed = 16.0;
            else if (PlaybackSpeed > 8.0) PlaybackSpeed = 8.0;
            else if (PlaybackSpeed > 4.0) PlaybackSpeed = 4.0;
            else if (PlaybackSpeed > 2.0) PlaybackSpeed = 2.0;
            else if (PlaybackSpeed > 1.0) PlaybackSpeed = 1.0;
            else if (PlaybackSpeed > 0.5) PlaybackSpeed = 0.5;
        }

        private void LoadPlaybackFiles()
        {
            PlaybackFiles.Clear();
            if (PlaybackSelectedCamera == null) return;

            string camDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PlaybackSelectedCamera.saveDirectory, PlaybackSelectedCamera.Index.ToString(), PlaybackSelectedDate.ToString("yyyyMMdd"));
            if (!Directory.Exists(camDir))
            {
                RaisePropertyChanged(nameof(PlaybackFiles));
                PlaybackUrl = null;
                return;
            }

            var files = Directory.GetFiles(camDir, "*.mp4");
            var list = new System.Collections.Generic.List<PlaybackFileModel>();
            foreach (var file in files)
            {
                try
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    var parts = fileName.Split('-');
                    if (parts.Length == 3 && int.TryParse(parts[0], out int hh) && int.TryParse(parts[1], out int mm) && int.TryParse(parts[2], out int ss))
                    {
                        var startTime = new DateTime(PlaybackSelectedDate.Year, PlaybackSelectedDate.Month, PlaybackSelectedDate.Day, hh, mm, ss);
                        TimeSpan duration = TimeSpan.FromMinutes(SaveInterval);
                        try
                        {
                            using (var cap = new OpenCvSharp.VideoCapture(file))
                            {
                                if (cap.IsOpened())
                                {
                                    double fc = cap.Get(OpenCvSharp.VideoCaptureProperties.FrameCount);
                                    double fps = cap.Get(OpenCvSharp.VideoCaptureProperties.Fps);
                                    if (fps > 0 && fc > 0)
                                    {
                                        duration = TimeSpan.FromSeconds(fc / fps);
                                    }
                                }
                            }
                        }
                        catch { }

                        list.Add(new PlaybackFileModel
                        {
                            FilePath = file,
                            StartTime = startTime,
                            Duration = duration
                        });
                    }
                }
                catch { }
            }

            list.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            foreach (var item in list)
            {
                PlaybackFiles.Add(item);
            }

            RaisePropertyChanged(nameof(PlaybackFiles));

            if (PlaybackFiles.Count > 0)
            {
                PlaybackUrl = PlaybackFiles[0].FilePath;
            }
            else
            {
                PlaybackUrl = null;
            }
        }

        #endregion

        #region 通道配置与状态

        private CameraService _selectedConfigCamera;
        public CameraService SelectedConfigCamera
        {
            get => _selectedConfigCamera;
            set
            {
                if (SetProperty(ref _selectedConfigCamera, value))
                {
                    RaisePropertyChanged(nameof(IsGpuEncodingActive));
                    RaisePropertyChanged(nameof(IsCpuEncodingActive));
                    RaisePropertyChanged(nameof(IsEncodingInactive));
                }
            }
        }

        public bool IsGpuEncodingActive =>
            SelectedConfigCamera != null && isSave &&
            SelectedConfigCamera.IsRecording &&
            !SelectedConfigCamera.IsCodecDowngraded &&
            !string.IsNullOrEmpty(SelectedConfigCamera.ActiveCodec) &&
            !SelectedConfigCamera.ActiveCodec.Equals("None", StringComparison.OrdinalIgnoreCase);

        public bool IsCpuEncodingActive =>
            SelectedConfigCamera != null && isSave &&
            SelectedConfigCamera.IsRecording &&
            SelectedConfigCamera.IsCodecDowngraded;

        public bool IsEncodingInactive =>
            !isSave || SelectedConfigCamera == null || !SelectedConfigCamera.IsRecording ||
            string.IsNullOrEmpty(SelectedConfigCamera.ActiveCodec) ||
            SelectedConfigCamera.ActiveCodec.Equals("None", StringComparison.OrdinalIgnoreCase);

        #endregion

        #region 运维报警与定时器

        private DispatcherTimer _periodicTimer;
        // 共享帧拉取定时器:1 个定时器统一处理所有 camera 的 UI 帧拉取,
        // 替代 CameraService 中原本的 per-camera DispatcherTimer。
        private DispatcherTimer _sharedFrameTimer;
        private int _sharedFrameIntervalMs = 100; // 10 fps,与 PreviewFps 默认值对齐

        private string _diskSpaceWarningText = "等待计算...";
        public string DiskSpaceWarningText
        {
            get => _diskSpaceWarningText;
            set => SetProperty(ref _diskSpaceWarningText, value);
        }

        private string _diskSpaceWarningColor = "#2E7D32";
        public string DiskSpaceWarningColor
        {
            get => _diskSpaceWarningColor;
            set => SetProperty(ref _diskSpaceWarningColor, value);
        }

        private void InitializePeriodicTimer()
        {
            _periodicTimer = new DispatcherTimer();
            // ★ Fix A: Period 10s → 60s
            // 证据:counters 数据 methods-jitted +9664 + PeriodicTimer × 4 cameras × 每10s × 86400次/天 = 34560次/天
            // 的反射 + 驱动调用。降低频率减少 CPU、reflection、P/Invoke 调用量,以及随附的内存 churn。
            _periodicTimer.Interval = TimeSpan.FromSeconds(60);
            _periodicTimer.Tick += PeriodicTimer_Tick;
            _periodicTimer.Start();

            // 立即执行一次
            UpdateDiskRecordingStatus();
            UpdateActiveCodecStatus();

            // 初始化共享帧拉取定时器(Background 优先级,与 WPF 渲染管线让开)
            _sharedFrameTimer = new DispatcherTimer(DispatcherPriority.Background);
            _sharedFrameTimer.Interval = TimeSpan.FromMilliseconds(_sharedFrameIntervalMs);
            _sharedFrameTimer.Tick += SharedFrameTimer_Tick;
            _sharedFrameTimer.Start();
        }

        /// <summary>
        /// 共享帧拉取:每个 Tick 遍历所有 camera,拉一次最新帧。
        /// 替代原先每个 CameraService 各自 DispatcherTimer 的方案,
        /// 从 4 个 timer × 30 fps = 120 op/s 入队,降为 1 个 × 10 fps = 10 op/s。
        /// </summary>
        private void SharedFrameTimer_Tick(object sender, EventArgs e)
        {
            if (IsUIRenderingPaused) return;

            var cameras = Cameras;
            if (cameras == null) return;

            // 内存压力监测:当本进程占用超过阈值时,触发 Gen 2 GC 释放 native 资源
            // (WPF 渲染缓存、DirectShow filter graph 等 native 内存需 GC 才会触发 finalize)
            // 阈值设 1 GB,避免高频 GC 影响 UI 流畅性;最少间隔 30 分钟。
            TryMemoryPressureCollect();

            for (int i = 0, n = cameras.Count; i < n; i++)
            {
                var cam = cameras[i];
                if (cam == null) continue;
                // CameraService.PullLatestFrame 内部 Interlocked.Exchange(_latestFrame),
                // 无新帧时返回 null,UI 线程零开销跳过。
                cam.PullAndApplyLatestFrame();
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        private long _lastPressureCollectTicks;
        private int _isCollectingMemory = 0;

        /// <summary>
        /// 自适应多级内存压力调控与工作集修剪。
        /// 根因分析（45小时长跑数据）：
        /// 1. 原策略硬编码 30 分钟间隔，导致非托管 OpenCV Mat 内存及 DirectShow 缓冲区在 30 分钟窗口内
        ///    自由累积至 4GB~5.56GB，直到每 30 分钟触发一次 GC 才能剧烈回落 2~3GB。
        /// 2. 现改为分级动态节流策略：
        ///    - 常态区 (< 800 MB): 不主动干预，避免无意义的 CPU 波动。
        ///    - 关注区 (800 MB ~ 1200 MB): 最少间隔 3 分钟触发一次温和回收。
        ///    - 高压区 (1200 MB ~ 1600 MB): 最少间隔 45 秒触发回收，防止内存持续冲高。
        ///    - 紧急区 (> 1600 MB): 最少间隔 15 秒触发强力回收与 LOH 内存紧缩。
        /// 3. 每次回收完成后启用 LOH 紧缩 (CompactOnce)、清理 MatPool 冗余缓存并调用 SetProcessWorkingSetSize(-1, -1) 修剪
        ///    工作集，立即将释放的物理内存归还给操作系统，彻底消除底线漂移 (Baseline Drift)。
        /// </summary>
        private void TryMemoryPressureCollect()
        {
            long bytes = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
            if (bytes < 800L * 1024 * 1024) return; // 800MB 以下零开销跳过

            long requiredIntervalTicks = bytes switch
            {
                >= 1600L * 1024 * 1024 => 15L * TimeSpan.TicksPerSecond,       // 紧急区: 15 秒
                >= 1200L * 1024 * 1024 => 45L * TimeSpan.TicksPerSecond,       // 高压区: 45 秒
                _ => 180L * TimeSpan.TicksPerSecond                            // 关注区: 3 分钟
            };

            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastPressureCollectTicks);
            if (now - last < requiredIntervalTicks && last != 0) return;

            // 抢占锁：避免多线程并发执行
            if (Interlocked.CompareExchange(ref _isCollectingMemory, 1, 0) != 0) return;

            try
            {
                Interlocked.Exchange(ref _lastPressureCollectTicks, now);
                long mbBefore = bytes / (1024 * 1024);

                // 1. 修剪 MatPool 对象池中的冗余缓存
                Infrastructure.MatPool.TrimExcess(8);

                // 2. 紧缩 LOH 并触发 Full GC 回收非托管终结器挂起的对象
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

                // 3. 通知操作系统修剪工作集，归还物理页面
                try
                {
                    SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
                }
                catch { }

                long mbAfter = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024);
                Logger.LogInfo($"[VM] 内存压力自适应回收完成: {mbBefore} MB -> {mbAfter} MB (释放 {mbBefore - mbAfter} MB)");
            }
            catch (Exception ex)
            {
                Logger.LogError("[VM] 执行自适应内存回收异常", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _isCollectingMemory, 0);
            }
        }

        private void PeriodicTimer_Tick(object sender, EventArgs e)
        {
            UpdateDiskRecordingStatus();
            UpdateActiveCodecStatus();
        }

        private bool _isUpdatingDiskStatus = false;
        private void UpdateDiskRecordingStatus()
        {
            if (_isUpdatingDiskStatus) return;
            try
            {
                _isUpdatingDiskStatus = true;
                double totalMbPerSec = 0.0;
                double potentialMbPerSec = 0.0;
                int activeSavingCamerasCount = 0;

                foreach (var cam in Cameras)
                {
                    double w = cam.Width > 0 ? cam.Width : 1920;
                    double h = cam.Height > 0 ? cam.Height : 1080;
                    double fps = cam.RecordFps > 0 ? cam.RecordFps : 10;
                    double pixelsPerSec = w * h * fps;
                    
                    double crfFactor = (51 - cam.Quality) / 21.0;
                    if (crfFactor < 0.05) crfFactor = 0.05;
                    
                    double camMbPerSec = (pixelsPerSec / (1920.0 * 1080.0 * 10.0)) * 0.25 * crfFactor;
                    
                    potentialMbPerSec += camMbPerSec;
                    if (isSave && cam.isSave)
                    {
                        totalMbPerSec += camMbPerSec;
                        activeSavingCamerasCount++;
                    }
                }

                // 核心修复：精准获取视频存储目标盘符与容量，杜绝与 BaseDirectory 割裂
                StorageManager.Instance.UpdateDriveInfo();
                string drive = StorageManager.Instance.StorageDriveRoot;
                double availableGB = StorageManager.Instance.AvailableStorageGb;
                double availableMB = StorageManager.Instance.AvailableStorageMb;

                double thresholdMb = DiskSpaceThreshold;
                double thresholdGb = thresholdMb / 1024.0;
                double usableMb = Math.Max(0.0, availableMB - thresholdMb);

                if (isSave && activeSavingCamerasCount > 0 && totalMbPerSec > 0)
                {
                    if (!OverwriteOldFiles)
                    {
                        // 策略：停止录像
                        if (availableMB <= thresholdMb)
                        {
                            DiskSpaceWarningText = $"⚠️ 磁盘剩余: {availableGB:F1} GB ({drive})\n已达到阈值: {thresholdGb:F1} GB\n空间不足已停止录像";
                            DiskSpaceWarningColor = "#C62828";
                        }
                        else
                        {
                            double hoursLeft = usableMb / (totalMbPerSec * 3600.0);
                            DiskSpaceWarningText = $"磁盘剩余: {availableGB:F1} GB ({drive})\n阈值保留: {thresholdGb:F1} GB\n预计可录: {hoursLeft:F1} 小时\n(达阈值即停止)";
                            DiskSpaceWarningColor = hoursLeft < 2.0 ? "#E65100" : "#2E7D32";
                        }
                    }
                    else
                    {
                        // 策略：覆盖旧文件
                        if (availableMB <= thresholdMb)
                        {
                            DiskSpaceWarningText = $"磁盘剩余: {availableGB:F1} GB ({drive})\n低水位阈值: {thresholdGb:F1} GB\n循环覆盖旧文件中\n(持续录制)";
                            DiskSpaceWarningColor = "#E65100";
                        }
                        else
                        {
                            double hoursLeft = usableMb / (totalMbPerSec * 3600.0);
                            DiskSpaceWarningText = $"磁盘剩余: {availableGB:F1} GB ({drive})\n低水位保留: {thresholdGb:F1} GB\n直录预计: {hoursLeft:F1} 小时\n(达阈值后循环覆盖)";
                            DiskSpaceWarningColor = "#2E7D32";
                        }
                    }
                }
                else
                {
                    double rate = potentialMbPerSec > 0 ? potentialMbPerSec : 1.0;
                    double hoursLeft = usableMb / (rate * 3600.0);

                    if (!OverwriteOldFiles)
                    {
                        if (availableMB <= thresholdMb)
                        {
                            DiskSpaceWarningText = $"录像未开启\n(剩余 {availableGB:F1} GB ≤ 阈值 {thresholdGb:F1} GB\n开启后将无法录制)";
                            DiskSpaceWarningColor = "#C62828";
                        }
                        else
                        {
                            DiskSpaceWarningText = $"录像未开启\n(剩余: {availableGB:F1} GB, 保留: {thresholdGb:F1} GB\n开启后预计可录: {hoursLeft:F1} 小时 (达阈值即停止))";
                            DiskSpaceWarningColor = "#555555";
                        }
                    }
                    else
                    {
                        if (availableMB <= thresholdMb)
                        {
                            DiskSpaceWarningText = $"录像未开启\n(剩余 {availableGB:F1} GB ≤ 阈值 {thresholdGb:F1} GB\n开启将循环覆盖旧录像)";
                            DiskSpaceWarningColor = "#E65100";
                        }
                        else
                        {
                            DiskSpaceWarningText = $"录像未开启\n(剩余: {availableGB:F1} GB, 保留: {thresholdGb:F1} GB\n直录预计: {hoursLeft:F1} 小时, 达阈值后循环覆盖)";
                            DiskSpaceWarningColor = "#555555";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"计算磁盘预计时间异常: {ex.Message}");
                DiskSpaceWarningText = "磁盘计算异常";
                DiskSpaceWarningColor = "#C62828";
            }
            finally
            {
                _isUpdatingDiskStatus = false;
            }
        }

        private bool _isUpdatingActiveCodec = false;
        private void UpdateActiveCodecStatus()
        {
            if (_isUpdatingActiveCodec) return;
            try
            {
                _isUpdatingActiveCodec = true;
                foreach (var cam in Cameras)
                {
                    cam.UpdateActiveCodecStatus();
                }
                
                RaisePropertyChanged(nameof(IsGpuEncodingActive));
                RaisePropertyChanged(nameof(IsCpuEncodingActive));
                RaisePropertyChanged(nameof(IsEncodingInactive));
            }
            finally
            {
                _isUpdatingActiveCodec = false;
            }
        }

        public void OnDeviceChangeNotification()
        {
            if (_isInitializing)
            {
                Logger.LogInfo("检测到正在进行初始化加载，忽略此次 USB 设备变动通知。");
                return;
            }

            // 防抖：每次收到通知后重置 800ms 定时器，只有最后一条消息后 800ms 才真正执行
            lock (_deviceChangeLock)
            {
                // ★ Fix P1-3: 同步等待旧 Timer 释放,避免 callback 持有 this 闭包泄漏
                var oldTimer = _deviceChangeDebounceTimer;
                _deviceChangeDebounceTimer = null;
                if (oldTimer != null)
                {
                    try { oldTimer.Dispose(); } catch { }
                }

                // 防抖：降至 150ms，实现拔插 USB 毫秒级自愈响应
                _deviceChangeDebounceTimer = new System.Threading.Timer(
                    _ => ExecuteDeviceChangeHandler(),
                    null,
                    150,
                    System.Threading.Timeout.Infinite);
            }
        }

        private void ExecuteDeviceChangeHandler()
        {
            // 互斥：如果上一次处理还未完成，跳过本次
            if (_isProcessingDeviceChange)
            {
                Logger.LogInfo("[防抖] 上一次设备变动处理尚未完成，跳过本次。");
                return;
            }
            _isProcessingDeviceChange = true;

            try
            {
                Logger.LogInfo("[防抖] 防抖定时器到期，开始执行设备状态检查...");

                // 在 UI 线程快速拷贝当前已有的 Camera 实例快照，避免多线程并发修改集合异常
                List<CameraService> existingCamerasSnapshot = null;
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    existingCamerasSnapshot = Cameras.ToList();
                });

                if (existingCamerasSnapshot == null) return;

                var devices = camera.Infrastructure.CameraDeviceEnumerator.EnumerateCameras();
                // 按 DevicePath 排序保持与 init() 一致的策略
                devices.Sort((a, b) => string.Compare(a.DevicePath, b.DevicePath, StringComparison.OrdinalIgnoreCase));

                // 1. 检查和更新已有摄像头状态
                foreach (var cam in existingCamerasSnapshot)
                {
                    if (string.IsNullOrEmpty(cam.DevicePath)) continue;

                    var match = devices.Find(d => string.Equals(d.DevicePath, cam.DevicePath, StringComparison.OrdinalIgnoreCase))
                                ?? devices.Find(d => d.Name == cam.Name && !existingCamerasSnapshot.Any(c => c != cam && c.DevicePath == d.DevicePath));
                    if (match != null)
                    {
                        if (cam.DevicePath != match.DevicePath)
                        {
                            Logger.LogInfo($"[OnDeviceChange] 摄像头 {cam.Name} 路径重定向: {cam.DevicePath} -> {match.DevicePath}");
                            cam.DevicePath = match.DevicePath;
                        }
                        // 物理设备在线，检查索引是否发生漂移
                        if (cam.Index != match.Index)
                        {
                            Logger.LogInfo($"[OnDeviceChange] 摄像头 {cam.Name} 索引纠正: {cam.Index} -> {match.Index}");
                            cam.Index = match.Index;
                        }

                        if (!cam.IsConnected && !cam.IsManualDisabled)
                        {
                            Logger.LogInfo($"尝试自动恢复断开后重新插回的摄像头 {cam.Name} (索引: {cam.Index})...");
                            Task.Run(() => cam.ChangeCameraInfo());
                        }
                    }
                    else
                    {
                        Logger.LogWarning($"[OnDeviceChange] 摄像头 {cam.Name} (路径: {cam.DevicePath}) 处于离线状态");
                        if (cam.IsConnected)
                        {
                            Logger.LogWarning($"[OnDeviceChange] 检测到摄像头 {cam.Name} 被物理拔除，执行离线释放处理");
                            cam.HandleDeviceRemoved();
                        }
                    }
                }

                // 2. 检查是否有新插入的摄像头
                foreach (var device in devices)
                {
                    // 关键防重复：实时检查 Cameras 集合（而非使用旧快照），防止并发添加同一设备
                    bool exists = false;
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        exists = Cameras.Any(c => c.DevicePath == device.DevicePath);
                    });

                    if (!exists)
                    {
                        // 限制最多 8 路监控通道
                        int currentCount = 0;
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            currentCount = Cameras.Count;
                        });
                        
                        if (currentCount >= 8) break;

                        Logger.LogInfo($"检测到全新接入的摄像头: {device.Name} (唯一路径: {device.DevicePath})，尝试添加...");
                        var newCam = new CameraService();
                        newCam.DevicePath = device.DevicePath;
                        newCam.Name = device.Name;
                        if (device.SupportedResolutions != null && device.SupportedResolutions.Count > 0)
                        {
                            newCam.SetHardwareResolutions(device.SupportedResolutions);
                        }

                        // 开启前预先应用已持久化的参数，一步到位
                        ApplySettingsToCamera(newCam);

                        if (newCam.StartCapture(device.Index))
                        {
                            Application.Current?.Dispatcher.Invoke(() =>
                            {
                                // 二次检查：在 UI 线程内再次确认没有被并发添加
                                if (Cameras.Any(c => c.DevicePath == device.DevicePath))
                                {
                                    Logger.LogWarning($"[OnDeviceChange] 摄像头 {device.Name} 已被并发添加，跳过重复项");
                                    newCam.Dispose();
                                    return;
                                }

                                Cameras.Add(newCam);
                                newCam.DiskSpaceLow += Camera_DiskSpaceLow;

                                if (SelectedConfigCamera == null)
                                {
                                    SelectedConfigCamera = newCam;
                                }
                            });
                            Logger.LogInfo($"✓ 全新接入的摄像头 {device.Name} 已成功加载并加入网格");
                            RefreshPreviewWidthOptions();
                        }
                        else
                        {
                            newCam.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("自适应更新热拔插设备状态发生异常", ex);
            }
            finally
            {
                _isProcessingDeviceChange = false;
            }
        }

        #endregion


        #region 配置相关
        private string _SystemTitle = "季华恒一离子注入机监控";
        public string SystemTitle
        {
            get { return _SystemTitle; }
            set { SetProperty(ref _SystemTitle, value); }
        }

        private string _LogoPath;
        public string LogoPath
        {
            get { return _LogoPath; }
            set { SetProperty(ref _LogoPath, value); }
        }

        private Visibility _LogoVisibility = Visibility.Collapsed;
        public Visibility LogoVisibility
        {
            get { return _LogoVisibility; }
            set { SetProperty(ref _LogoVisibility, value); }
        }

        private void LoadSettings()
        {
            var appSettings = AppSettingsService.Instance.Current;
            isSave = appSettings.IsSave;
            Opacity = appSettings.Opacity;
            Title = appSettings.Title;
            SystemTitle = appSettings.SystemTitle;
            OverwriteOldFiles = appSettings.OverwriteOldFiles;
            DiskSpaceThreshold = appSettings.DiskSpaceThreshold;
            DiskSpaceThresholdInput = DiskSpaceThreshold;
            UpdateDiskSpaceThresholdStatus();
            Quality = appSettings.Quality;
            SaveInterval = appSettings.SaveInterval;
            MaxPreviewWidth = appSettings.MaxPreviewWidth;
            PreviewFps = appSettings.PreviewFps;
            RecordFps = appSettings.RecordFps;
            Contrast = appSettings.Contrast;
            Luminance = appSettings.Luminance;
            IsGrayHomogenization = appSettings.IsGrayHomogenization;
            OverlayTextSize = appSettings.OverlayTextSize;
            ForceCpuEncoding = appSettings.ForceCpuEncoding;
            VideoEncoderFactory.ForceCpuEncoding = ForceCpuEncoding;
            IsDarkTheme = appSettings.IsDarkTheme;
            ApplyTheme(IsDarkTheme);

            // 初始化存储管理器
            StorageManager.Instance.SaveDirectory = appSettings.SaveDirectory;
            StorageManager.Instance.DiskSpaceThresholdMb = DiskSpaceThreshold;
            StorageManager.Instance.OverwriteOldFiles = OverwriteOldFiles;

            // 摄像头特定配置改为在 init 后台线程/热插拔线程添加摄像头时，通过 ApplySettingsToCamera 异步应用。

            // 探测根目录下可能存在的 LOGO 图片文件
            string possibleLogoPng = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.png");
            string possibleLogoJpg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.jpg");
            string possibleLogoIco = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.ico");
            if (File.Exists(possibleLogoPng))
            {
                LogoPath = possibleLogoPng;
                LogoVisibility = Visibility.Visible;
            }
            else if (File.Exists(possibleLogoJpg))
            {
                LogoPath = possibleLogoJpg;
                LogoVisibility = Visibility.Visible;
            }
            else if (File.Exists(possibleLogoIco))
            {
                LogoPath = possibleLogoIco;
                LogoVisibility = Visibility.Visible;
            }
            else
            {
                LogoVisibility = Visibility.Collapsed;
            }
        }

        public void SaveCameraOrder()
        {
            try
            {
                var order = Cameras.Select(c => c.GetConfigKey()).Where(k => !string.IsNullOrEmpty(k)).ToList();
                AppSettingsService.Instance.Update(s => s.CameraOrder = order);
                Logger.LogInfo($"[VM] 已保存摄像头排列顺序: {string.Join(", ", order)}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[VM] 保存摄像头排列顺序失败: {ex.Message}");
            }
        }

        private void ApplyCameraOrder()
        {
            try
            {
                var savedOrder = AppSettingsService.Instance.Current.CameraOrder;
                if (savedOrder == null || savedOrder.Count == 0 || Cameras.Count <= 1) return;

                var ordered = Cameras.OrderBy(c =>
                {
                    int idx = savedOrder.IndexOf(c.GetConfigKey());
                    return idx >= 0 ? idx : int.MaxValue;
                }).ToList();

                for (int i = 0; i < ordered.Count; i++)
                {
                    int oldIndex = Cameras.IndexOf(ordered[i]);
                    if (oldIndex != i)
                    {
                        Cameras.Move(oldIndex, i);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[VM] 恢复摄像头排序失败: {ex.Message}");
            }
        }

        private void ApplySettingsToCamera(CameraService cam)
        {
            if (cam == null) return;
            string configKey = cam.GetConfigKey();
            AppSettings.CameraSettings camConfig = null;
            AppSettingsService.Instance.Current.Cameras.TryGetValue(configKey, out camConfig);

            cam.CustomOverlayText = camConfig?.CustomText 
                ?? ConfigurationManager.GetSetting($"Camera_{configKey}_CustomText") 
                ?? cam.Name;
            cam.OverlayTextSize = OverlayTextSize;
            cam.isSave = isSave;
            cam.OverwriteOldFiles = OverwriteOldFiles;
            cam.DiskThreshold = DiskSpaceThreshold;
            cam.Quality = Quality;
            cam.SaveInterval = TimeSpan.FromMinutes(SaveInterval);
            cam.Contrast = Contrast;
            cam.Luminance = Luminance;
            cam.IsGrayHomogenization = IsGrayHomogenization;
            cam.PreviewFps = PreviewFps;
            cam.MaxPreviewWidth = MaxPreviewWidth;
            cam.UseGpuAcceleration = !ForceCpuEncoding;

            string savedRes = camConfig?.Resolution ?? ConfigurationManager.GetSetting($"Camera_{configKey}_Resolution");
            if (!string.IsNullOrEmpty(savedRes))
            {
                cam.SelectedResolution = savedRes;
            }
            string savedRecordRes = camConfig?.RecordResolution ?? ConfigurationManager.GetSetting($"Camera_{configKey}_RecordResolution");
            if (!string.IsNullOrEmpty(savedRecordRes))
            {
                cam.SelectedRecordResolution = savedRecordRes;
            }
            if (camConfig?.RecordFps.HasValue == true)
            {
                cam.RecordFps = camConfig.RecordFps.Value;
            }
            else
            {
                string savedRecordFps = ConfigurationManager.GetSetting($"Camera_{configKey}_RecordFps");
                if (!string.IsNullOrEmpty(savedRecordFps) && int.TryParse(savedRecordFps, out int rfps))
                {
                    cam.RecordFps = rfps;
                }
            }
            if (camConfig?.IsManualDisabled == true)
            {
                cam.IsManualDisabled = true;
            }

            bool? savedFlipH = camConfig?.IsFlipHorizontal;
            if (!savedFlipH.HasValue && bool.TryParse(ConfigurationManager.GetSetting($"Camera_{configKey}_IsFlipHorizontal"), out bool sfh))
            {
                savedFlipH = sfh;
            }
            if (savedFlipH == true)
            {
                cam.IsFlipHorizontal = true;
            }

            bool? savedFlipV = camConfig?.IsFlipVertical;
            if (!savedFlipV.HasValue && bool.TryParse(ConfigurationManager.GetSetting($"Camera_{configKey}_IsFlipVertical"), out bool sfv))
            {
                savedFlipV = sfv;
            }
            if (savedFlipV == true)
            {
                cam.IsFlipVertical = true;
            }

            string savedStretch = camConfig?.StretchMode ?? ConfigurationManager.GetSetting($"Camera_{configKey}_StretchMode");
            if (!string.IsNullOrEmpty(savedStretch) && Enum.TryParse<System.Windows.Media.Stretch>(savedStretch, true, out var st))
            {
                cam.VideoStretch = st;
            }
            else
            {
                cam.VideoStretch = System.Windows.Media.Stretch.Uniform;
            }
        }

        private void SaveSettings()
        {
            AppSettingsService.Instance.Update(s =>
            {
                s.IsSave = isSave;
                s.Opacity = Opacity;
                s.Title = Title;
                s.SystemTitle = SystemTitle;
                s.OverwriteOldFiles = OverwriteOldFiles;
                s.DiskSpaceThreshold = this.DiskSpaceThreshold;
                s.SaveDirectory = StorageManager.Instance.SaveDirectory;
                s.Quality = this.Quality;
                s.SaveInterval = this.SaveInterval;
                s.MaxPreviewWidth = this.MaxPreviewWidth;
                s.PreviewFps = this.PreviewFps;
                s.RecordFps = this.RecordFps;
                s.Contrast = this.Contrast;
                s.Luminance = this.Luminance;
                s.IsGrayHomogenization = this.IsGrayHomogenization;
                s.OverlayTextSize = this.OverlayTextSize;
                s.ForceCpuEncoding = this.ForceCpuEncoding;
                s.IsDarkTheme = this.IsDarkTheme;
                s.CameraOrder = Cameras.Select(c => c.GetConfigKey()).Where(k => !string.IsNullOrEmpty(k)).ToList();

                foreach (var cam in Cameras)
                {
                    string key = cam.GetConfigKey();
                    if (string.IsNullOrEmpty(key)) continue;

                    if (!s.Cameras.TryGetValue(key, out var camSetting))
                    {
                        camSetting = new AppSettings.CameraSettings();
                        s.Cameras[key] = camSetting;
                    }

                    camSetting.CustomText = cam.CustomOverlayText;
                    camSetting.Resolution = cam.SelectedResolution;
                    camSetting.RecordResolution = cam.SelectedRecordResolution;
                    camSetting.RecordFps = cam.RecordFps;
                    camSetting.IsManualDisabled = cam.IsManualDisabled;
                    camSetting.IsFlipHorizontal = cam.IsFlipHorizontal;
                    camSetting.IsFlipVertical = cam.IsFlipVertical;
                    camSetting.StretchMode = cam.VideoStretch.ToString();
                }
            });
            AppSettingsService.Instance.SaveSync();

            ConfigurationManager.SaveSetting("isSave", isSave.ToString());
            ConfigurationManager.SaveSetting("Opacity", Opacity.ToString());
            ConfigurationManager.SaveSetting("Title", Title);
            ConfigurationManager.SaveSetting("SystemTitle", SystemTitle);
            ConfigurationManager.SaveSetting("OverwriteOldFiles", OverwriteOldFiles.ToString());
            ConfigurationManager.SaveSetting("DiskSpaceThreshold", this.DiskSpaceThreshold.ToString());
            ConfigurationManager.SaveSetting("SaveDirectory", StorageManager.Instance.SaveDirectory);
            ConfigurationManager.SaveSetting("Quality", this.Quality.ToString());
            ConfigurationManager.SaveSetting("SaveInterval", this.SaveInterval.ToString());
            ConfigurationManager.SaveSetting("MaxPreviewWidth", this.MaxPreviewWidth.ToString());
            ConfigurationManager.SaveSetting("PreviewFps", this.PreviewFps.ToString());
            ConfigurationManager.SaveSetting("RecordFps", this.RecordFps.ToString());
            ConfigurationManager.SaveSetting("Contrast", this.Contrast.ToString());
            ConfigurationManager.SaveSetting("Luminance", this.Luminance.ToString());
            ConfigurationManager.SaveSetting("IsGrayHomogenization", this.IsGrayHomogenization.ToString());
            ConfigurationManager.SaveSetting("OverlayTextSize", this.OverlayTextSize.ToString());
            ConfigurationManager.SaveSetting("ForceCpuEncoding", this.ForceCpuEncoding.ToString());
            ConfigurationManager.SaveSetting("IsDarkTheme", this.IsDarkTheme.ToString());

            foreach (var cam in Cameras)
            {
                 string configKey = cam.GetConfigKey();
                 ConfigurationManager.SaveSetting($"Camera_{configKey}_CustomText", cam.CustomOverlayText ?? "");
                 ConfigurationManager.SaveSetting($"Camera_{configKey}_Resolution", cam.SelectedResolution ?? "");
                 ConfigurationManager.SaveSetting($"Camera_{configKey}_RecordResolution", cam.SelectedRecordResolution ?? "与采集分辨率一致");
                 ConfigurationManager.SaveSetting($"Camera_{configKey}_RecordFps", cam.RecordFps.ToString());
                 ConfigurationManager.SaveSetting($"Camera_{configKey}_IsManualDisabled", cam.IsManualDisabled.ToString());
                 ConfigurationManager.SaveSetting($"Camera_{configKey}_IsFlipHorizontal", cam.IsFlipHorizontal.ToString());
                 ConfigurationManager.SaveSetting($"Camera_{configKey}_IsFlipVertical", cam.IsFlipVertical.ToString());
                 ConfigurationManager.SaveSetting($"Camera_{configKey}_StretchMode", cam.VideoStretch.ToString());
            }
        }

        #endregion

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
            _disposed = true;  // ★ 提到最前面,任何并发访问立即可见

            if (disposing)
            {
                StorageManager.Instance.StorageWarning -= OnStorageWarningTriggered;

                // ★ Fix P1-4: 先解绑所有事件订阅,避免闭包循环引用阻止 GC
                // 关键:this.PropertyChanged += OnPersistentPropertyChanged 在 #474 行
                // lambda 闭包 this 阻止 VM 被回收
                this.PropertyChanged -= OnPersistentPropertyChanged;

                try
                {
                    if (_cameraWatchdogTimer != null)
                    {
                        _cameraWatchdogTimer.Stop();
                        _cameraWatchdogTimer = null;
                    }

                    if (_periodicTimer != null)
                    {
                        _periodicTimer.Stop();
                        _periodicTimer = null;
                    }

                    if (_sharedFrameTimer != null)
                    {
                        _sharedFrameTimer.Stop();
                        _sharedFrameTimer = null;
                    }

                    // 释放设备热插拔防抖定时器
                    lock (_deviceChangeLock)
                    {
                        if (_deviceChangeDebounceTimer != null)
                        {
                            try { _deviceChangeDebounceTimer.Dispose(); } catch { }
                            _deviceChangeDebounceTimer = null;
                        }
                    }

                    // 取消所有事件订阅并并行的释放所有 CameraService，缩短退出时间
                    if (Cameras != null)
                    {
                        var disposeTasks = new System.Collections.Generic.List<System.Threading.Tasks.Task>();
                        foreach (var camera in Cameras)
                        {
                            if (camera != null)
                            {
                                camera.DiskSpaceLow -= Camera_DiskSpaceLow;
                                var camToDispose = camera;
                                disposeTasks.Add(System.Threading.Tasks.Task.Run(() =>
                                {
                                    try { camToDispose.Dispose(); } catch { }
                                }));
                            }
                        }

                        if (_Cam != null && !Cameras.Contains(_Cam))
                        {
                            var camToDispose = _Cam;
                            _Cam = null;
                            disposeTasks.Add(System.Threading.Tasks.Task.Run(() =>
                            {
                                try { camToDispose.Dispose(); } catch { }
                            }));
                        }

                        if (disposeTasks.Count > 0)
                        {
                            System.Threading.Tasks.Task.WaitAll(disposeTasks.ToArray(), 4000);
                        }
                        Cameras.Clear();
                    }

                    Debug.WriteLine("MainWindowViewModel 已释放资源");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"释放 MainWindowViewModel 资源时发生错误: {ex.Message}");
                }
            }

            _disposed = true;
        }

        #endregion
    }

    public class PlaybackFileModel
    {
        public string FilePath { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime EndTime => StartTime + Duration;
    }
}
