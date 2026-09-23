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
using System.Windows.Threading;

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
            foreach (var camera in Cameras)
            {
                camera.OverwriteOldFiles = OverwriteOldFiles;
            }
        }



        private int _DiskSpaceThresholdInput = 1024;
        public int DiskSpaceThresholdInput
        {
            get { return _DiskSpaceThresholdInput; }
            set { SetProperty(ref _DiskSpaceThresholdInput, value); }
        }

        private DelegateCommand _ConfirmDiskThresholdCommand;
        public DelegateCommand ConfirmDiskThresholdCommand =>
            _ConfirmDiskThresholdCommand ?? (_ConfirmDiskThresholdCommand = new DelegateCommand(ExecuteConfirmDiskThresholdCommand));

        private void ExecuteConfirmDiskThresholdCommand()
        {
            DiskSpaceThreshold = DiskSpaceThresholdInput;
            // ★ 根因修复:用户点"确认"后必须立即落盘,
            // 否则 app 崩溃 / 强制关闭 → 下次启动值丢失,看起来像"重置"。
            AppSettingsService.Instance.RequestSave();
            Logger.LogInfo($"[VM] 磁盘阈值更新为 {DiskSpaceThreshold} MB,已调度自动保存");
        }

        private int _DiskSpaceThreshold = 1024;
        public int DiskSpaceThreshold
        {
            get { return _DiskSpaceThreshold; }
            set
            {
                SetProperty(ref _DiskSpaceThreshold, value);
                OnDiskSpaceThresholdChanged();
            }
        }

        void OnDiskSpaceThresholdChanged()
        {
            foreach (var camera in Cameras)
            {
                camera.DiskThreshold = DiskSpaceThreshold;
            }
            //Debug.WriteLine("修改空间阈值");
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

        public MainWindowViewModel()
        {
            init();
            LoadSettings();
            InitializePeriodicTimer();

            // ★ 自动保存:任何"持久化属性"被修改后,1 秒防抖落盘。
            // 此前必须等 SaveSettings() 在 Close 时才统一保存,期间崩溃/强杀 → 配置丢失。
            // 现在 setter 改值 → PropertyChanged 触发 → AppSettingsService.RequestSave() 调度落盘。
            this.PropertyChanged += OnPersistentPropertyChanged;
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
                // MinWindowWidth/MinWindowHeight 属于 MainWindow,在 MainWindow 自己处理
            };

        private void OnPersistentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == null) return;
            if (!_persistKeys.Contains(e.PropertyName)) return;
            // 防抖由 AppSettingsService.RequestSave 内部 Timer 提供,无需此处再 debounce
            AppSettingsService.Instance.RequestSave();
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

                                    ApplySettingsToCamera(camera);
                                    Cameras.Add(camera);
                                    camera.DiskSpaceLow += Camera_DiskSpaceLow;

                                    Logger.LogInfo($"✓ 摄像头 #{device.Index} ({device.Name}) 加载成功 (当前在线: {camera.IsConnected})");
                                });

                                // 分时初始化：若成功开启，为防抢带宽延时 600ms，否则仅 50ms 
                                if (camera.IsConnected)
                                {
                                    System.Threading.Thread.Sleep(600);
                                }
                                else
                                {
                                    System.Threading.Thread.Sleep(50);
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

        private void Camera_DiskSpaceLow(object sender, string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                isSave = false;
                MessageBox.Show(message, "磁盘空间警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
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
        }

        private DelegateCommand _speedDownCommand;
        public DelegateCommand SpeedDownCommand =>
            _speedDownCommand ?? (_speedDownCommand = new DelegateCommand(ExecuteSpeedDownCommand));

        private void ExecuteSpeedDownCommand()
        {
            if (PlaybackSpeed > 16.0) PlaybackSpeed = 16.0;
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

        public bool IsGpuEncodingActive => SelectedConfigCamera != null && isSave && !SelectedConfigCamera.IsCodecDowngraded;
        public bool IsCpuEncodingActive => SelectedConfigCamera != null && isSave && SelectedConfigCamera.IsCodecDowngraded;
        public bool IsEncodingInactive => !isSave;

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

        private long _lastPressureCollectTicks;
        private const long PressureCollectIntervalTicks = 30L * 60 * TimeSpan.TicksPerSecond;
        private const long PressureThresholdBytes = 1024L * 1024 * 1024; // 1 GB

        /// <summary>
        /// 进程占用 > 1 GB 时,每隔 ≥ 30 分钟最多触发一次 Gen 2 GC,
        /// 主动回收 WPF / DirectShow 持有的 native 资源。
        /// 实际数据(counters):Process.PrivateBytes 每 17 小时累积到 ~850 MB → GC → 回落 ~400 MB。
        /// 主动 GC 可缩短这个周期,避免 1 次 850 MB 的尖峰。
        /// </summary>
        private void TryMemoryPressureCollect()
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastPressureCollectTicks);
            if (now - last < PressureCollectIntervalTicks && last != 0) return;

            long bytes = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
            if (bytes < PressureThresholdBytes) return;

            // 抢占:防止多个线程同时进入 GC.Collect
            if (Interlocked.CompareExchange(ref _lastPressureCollectTicks, now, last) != last) return;

            Logger.LogInfo($"[VM] 内存占用 {bytes / 1024 / 1024} MB,主动触发 GC");
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        private void PeriodicTimer_Tick(object sender, EventArgs e)
        {
            UpdateDiskRecordingStatus();
            UpdateActiveCodecStatus();
        }

        private void UpdateDiskRecordingStatus()
        {
            try
            {
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

                string drive = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
                DriveInfo driveInfo = new DriveInfo(drive);
                double availableGB = driveInfo.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                double availableMB = driveInfo.AvailableFreeSpace / (1024.0 * 1024.0);

                if (isSave && activeSavingCamerasCount > 0 && totalMbPerSec > 0)
                {
                    double hoursLeft = availableMB / (totalMbPerSec * 3600.0);
                    DiskSpaceWarningText = $"磁盘剩余: {availableGB:F1} GB, 预计录制: {hoursLeft:F1} 小时";
                }
                else
                {
                    double hoursLeft = availableMB / (potentialMbPerSec * 3600.0);
                    DiskSpaceWarningText = $"录像未开启 (开启后预计录制: {hoursLeft:F1} 小时)";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"计算磁盘预计时间异常: {ex.Message}");
                DiskSpaceWarningText = "磁盘计算异常";
            }
        }

        private void UpdateActiveCodecStatus()
        {
            foreach (var cam in Cameras)
            {
                cam.UpdateActiveCodecStatus();
            }
            
            RaisePropertyChanged(nameof(IsGpuEncodingActive));
            RaisePropertyChanged(nameof(IsCpuEncodingActive));
            RaisePropertyChanged(nameof(IsEncodingInactive));
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

                _deviceChangeDebounceTimer = new System.Threading.Timer(
                    _ => ExecuteDeviceChangeHandler(),
                    null,
                    800,
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

                    var match = devices.Find(d => d.DevicePath == cam.DevicePath);
                    if (match != null)
                    {
                        // 物理设备在线，检查索引是否发生漂移
                        if (cam.Index != match.Index)
                        {
                            Logger.LogInfo($"[OnDeviceChange] 摄像头 {cam.Name} 索引纠正: {cam.Index} -> {match.Index}");
                            cam.Index = match.Index;
                        }

                        if (!cam.IsConnected)
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

                                ApplySettingsToCamera(newCam);
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
            isSave = bool.Parse(ConfigurationManager.GetSetting("isSave") ?? "true");
            Opacity = double.Parse(ConfigurationManager.GetSetting("Opacity") ?? "1.0");
            Title = ConfigurationManager.GetSetting("Title") ?? "监控";
            SystemTitle = ConfigurationManager.GetSetting("SystemTitle") ?? "季华恒一离子注入机监控";
            OverwriteOldFiles = bool.Parse(ConfigurationManager.GetSetting("OverwriteOldFiles") ?? "true");
            DiskSpaceThreshold = int.Parse(ConfigurationManager.GetSetting("DiskSpaceThreshold") ?? "1024");
            DiskSpaceThresholdInput = DiskSpaceThreshold;
            Quality = int.Parse(ConfigurationManager.GetSetting("Quality") ?? "30");
            SaveInterval = int.Parse(ConfigurationManager.GetSetting("SaveInterval") ?? "1");
            MaxPreviewWidth = int.Parse(ConfigurationManager.GetSetting("MaxPreviewWidth") ?? "640");
            PreviewFps = int.Parse(ConfigurationManager.GetSetting("PreviewFps") ?? "15");
            RecordFps = int.Parse(ConfigurationManager.GetSetting("RecordFps") ?? "15");
            Contrast = double.Parse(ConfigurationManager.GetSetting("Contrast") ?? "1.0");
            Luminance = double.Parse(ConfigurationManager.GetSetting("Luminance") ?? "0.0");
            IsGrayHomogenization = bool.Parse(ConfigurationManager.GetSetting("IsGrayHomogenization") ?? "false");
            OverlayTextSize = double.Parse(ConfigurationManager.GetSetting("OverlayTextSize") ?? "0.3");

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

        private void ApplySettingsToCamera(CameraService cam)
        {
            if (cam == null) return;
            string configKey = cam.GetConfigKey();
            cam.CustomOverlayText = ConfigurationManager.GetSetting($"Camera_{configKey}_CustomText") ?? cam.Name;
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

            string savedRes = ConfigurationManager.GetSetting($"Camera_{configKey}_Resolution");
            if (!string.IsNullOrEmpty(savedRes))
            {
                cam.SelectedResolution = savedRes;
            }
            string savedRecordRes = ConfigurationManager.GetSetting($"Camera_{configKey}_RecordResolution");
            if (!string.IsNullOrEmpty(savedRecordRes))
            {
                cam.SelectedRecordResolution = savedRecordRes;
            }
            string savedRecordFps = ConfigurationManager.GetSetting($"Camera_{configKey}_RecordFps");
            if (!string.IsNullOrEmpty(savedRecordFps) && int.TryParse(savedRecordFps, out int rfps))
            {
                cam.RecordFps = rfps;
            }
        }

        private void SaveSettings()
        {
            ConfigurationManager.SaveSetting("isSave", isSave.ToString());
            ConfigurationManager.SaveSetting("Opacity", Opacity.ToString());
            ConfigurationManager.SaveSetting("Title", Title);
            ConfigurationManager.SaveSetting("SystemTitle", SystemTitle);
            ConfigurationManager.SaveSetting("OverwriteOldFiles", OverwriteOldFiles.ToString());
            ConfigurationManager.SaveSetting("DiskSpaceThreshold", this.DiskSpaceThreshold.ToString());
            ConfigurationManager.SaveSetting("Quality", this.Quality.ToString());
            ConfigurationManager.SaveSetting("SaveInterval", this.SaveInterval.ToString());
            ConfigurationManager.SaveSetting("MaxPreviewWidth", this.MaxPreviewWidth.ToString());
            ConfigurationManager.SaveSetting("PreviewFps", this.PreviewFps.ToString());
            ConfigurationManager.SaveSetting("RecordFps", this.RecordFps.ToString());
            ConfigurationManager.SaveSetting("Contrast", this.Contrast.ToString());
            ConfigurationManager.SaveSetting("Luminance", this.Luminance.ToString());
            ConfigurationManager.SaveSetting("IsGrayHomogenization", this.IsGrayHomogenization.ToString());
            ConfigurationManager.SaveSetting("OverlayTextSize", this.OverlayTextSize.ToString());

            foreach (var cam in Cameras)
            {
                 string configKey = cam.GetConfigKey();
                 ConfigurationManager.SaveSetting($"Camera_{configKey}_CustomText", cam.CustomOverlayText ?? "");
                 ConfigurationManager.SaveSetting($"Camera_{configKey}_Resolution", cam.SelectedResolution ?? "");
                 ConfigurationManager.SaveSetting($"Camera_{configKey}_RecordResolution", cam.SelectedRecordResolution ?? "与采集分辨率一致");
                 ConfigurationManager.SaveSetting($"Camera_{configKey}_RecordFps", cam.RecordFps.ToString());
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
                // ★ Fix P1-4: 先解绑所有事件订阅,避免闭包循环引用阻止 GC
                // 关键:this.PropertyChanged += OnPersistentPropertyChanged 在 #474 行
                // lambda 闭包 this 阻止 VM 被回收
                this.PropertyChanged -= OnPersistentPropertyChanged;

                try
                {
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
