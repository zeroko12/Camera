using OpenCvSharp;
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
        private Mat _captureMat = new();
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
                        ConfigurationManager.SaveSetting($"Camera_{Index}_RecordFps", clamped.ToString());
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
                var oldFrame = _Frame;
                if (SetProperty(ref _Frame, value))
                {
                    if (oldFrame != null && oldFrame != value)
                    {
                        Infrastructure.MatPool.Return(oldFrame);
                    }
                }
            }
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
            set => SetProperty(ref _devicePath, value);
        }

        private int _Index = -1;
        public int Index
        {
            get => _Index;
            set => SetProperty(ref _Index, value);
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
                        ConfigurationManager.SaveSetting($"Camera_{Index}_RecordResolution", value);
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

        private string _selectedResolution = "";
        public string SelectedResolution
        {
            get => _selectedResolution;
            set
            {
                if (SetProperty(ref _selectedResolution, value) && !string.IsNullOrEmpty(value))
                {
                    var parts = value.Split('x');
                    if (parts.Length == 2 && double.TryParse(parts[0], out double w) && double.TryParse(parts[1], out double h))
                    {
                        Width = w;
                        Height = h;
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

                    // 实时保存水印字体大小
                    if (Index >= 0)
                    {
                        ConfigurationManager.SaveSetting($"Camera_{Index}_OverlayTextSize", value.ToString());
                    }
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

                    // 实时保存自定义水印文字内容
                    if (Index >= 0)
                    {
                        ConfigurationManager.SaveSetting($"Camera_{Index}_CustomText", value ?? "");
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

        public bool IsConnected => _continuousReadFailCount <= 5;

        public void UpdateActiveCodecStatus()
        {
            RaisePropertyChanged(nameof(ActiveCodec));
            RaisePropertyChanged(nameof(IsCodecDowngraded));
        }

        #endregion

        #region 事件定义

        public delegate void FrameChangedEventHandler(Mat frame);
        public event FrameChangedEventHandler FrameChanged;

        public event EventHandler<string> DiskSpaceLow;

        #endregion

        #region 摄像头采集格式

        private enum CaptureFormat { YUY2, NV12, MJPG }
        private static CaptureFormat _preferredFormat = CaptureFormat.YUY2;
        private static bool _formatChecked = false;

        #endregion

        public CameraService()
        {
            InitializeModules();
        }

        private void InitializeModules()
        {
            // 1. 初始化模块参数与流水线
            _claheProcessor = new ClaheProcessor { Enabled = IsGrayHomogenization, Order = 10 };
            _contrastLuminanceProcessor = new ContrastLuminanceProcessor { Contrast = Contrast, Luminance = Luminance, Order = 20 };
            _timestampOverlayProcessor = new TimestampOverlayProcessor { Enabled = true, Order = 30 };
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

            // 订阅预览帧回调
            _previewController.PreviewFrameReady += (s, previewFrame) =>
            {
                if (Application.Current == null)
                {
                    if (previewFrame != null) Infrastructure.MatPool.Return(previewFrame);
                    return;
                }

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    Frame = previewFrame; // 自动处理旧帧到 MatPool
                });
            };

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

        public int getCameraNum()
        {
            return 1;
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
            string savedRes = ConfigurationManager.GetSetting($"Camera_{Index}_Resolution");
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
                Width = bestWidth;
                Height = bestHeight;
                _selectedResolution = $"{bestWidth}x{bestHeight}";
                Logger.LogInfo($"[CameraService #{Index}] 未发现有效已保存分辨率，默认使用最大分辨率: {bestWidth}x{bestHeight}");
            }

            RaisePropertyChanged(nameof(SelectedResolution));

            // 读取已保存的录像分辨率配置，如果在支持的列表中，则优先应用
            string savedRecordRes = ConfigurationManager.GetSetting($"Camera_{Index}_RecordResolution");
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
            string savedRecordFps = ConfigurationManager.GetSetting($"Camera_{Index}_RecordFps");
            if (!string.IsNullOrEmpty(savedRecordFps) && int.TryParse(savedRecordFps, out int rfps))
            {
                RecordFps = rfps;
            }

            // 将摄像头设置回最终确定的分辨率（避免保留探测循环最后一项的值）
            cap.Set(VideoCaptureProperties.FrameWidth, Width);
            cap.Set(VideoCaptureProperties.FrameHeight, Height);

            Logger.LogInfo($"[CameraService #{Index}] 最终初始化摄像头配置: {Width}x{Height} @ {FpsOrigin}fps");
        }

        public bool StartCapture(int index = 0)
        {
            try
            {
                Logger.LogInfo($"正在打开摄像头索引 {index}...");
                cap = new VideoCapture(index, VideoCaptureAPIs.DSHOW);

                bool formatSet = false;
                if (!_formatChecked || _preferredFormat == CaptureFormat.YUY2)
                {
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
                    cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                    _preferredFormat = CaptureFormat.MJPG;
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
                }

                cap = new VideoCapture(Index, VideoCaptureAPIs.DSHOW);
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
                    _lastPipelineProcessTime = DateTime.MinValue; // 重连成功，重置节流时间
                    Logger.LogInfo($"摄像头 {Index} 系统自恢复重置成功。");
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
                        double actualW = cap.Get(VideoCaptureProperties.FrameWidth);
                        double actualH = cap.Get(VideoCaptureProperties.FrameHeight);
                        if (actualW > 0 && actualH > 0)
                        {
                            Width = actualW;
                            Height = actualH;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"摄像头 {Index} 尝试重新初始化连接时发生异常", ex);
            }
        }

        private void RefreshFrame()
        {
            _isCapturing = true;

            Task.Run(() =>
            {
                Stopwatch stopwatch = new Stopwatch();
                
                while (_isCapturing)
                {
                    stopwatch.Restart();
                    Mat tempFrame = null;

                    try
                    {
                        if (cap == null || cap.IsDisposed || !cap.IsOpened() || !cap.Read(_captureMat) || _captureMat.Empty())
                        {
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

                                    // 生成并推送断开连接的警示帧
                                    tempFrame = Infrastructure.MatPool.Rent((int)Width, (int)Height, MatType.CV_8UC3);
                                    tempFrame.SetTo(new Scalar(20, 20, 20)); // 暗灰底色
                                    string errorMsg = (cap == null || !cap.IsOpened())
                                         ? $"Channel {Index} - Device Occupied / Not Ready"
                                         : $"Channel {Index} - Connection Disconnected";
                                    const HersheyFonts font = HersheyFonts.HersheySimplex;
                                    double fontScale = Math.Max(0.4, 0.6 * (Width / 640.0));
                                    int thickness = Math.Max(1, (int)(2 * (Width / 640.0)));
                                    OpenCvSharp.Size textSize = Cv2.GetTextSize(errorMsg, font, fontScale, thickness, out _);
                                    var textOrg = new OpenCvSharp.Point((Width - textSize.Width) / 2, (Height + textSize.Height) / 2);
                                    Cv2.PutText(tempFrame, errorMsg, textOrg, font, fontScale, new Scalar(0, 0, 255), thickness, LineTypes.AntiAlias);

                                    // 推送至预览，使 UI 渲染显示
                                    _previewController.UpdateFrame(tempFrame, DateTime.Now);
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
                            continue;
                        }

                        bool wasConnectedInSuccess = _continuousReadFailCount <= 5;
                        _continuousReadFailCount = 0;
                        _lastWarningFrameTime = DateTime.MinValue;
                        if (wasConnectedInSuccess != (_continuousReadFailCount <= 5))
                        {
                            RaisePropertyChanged(nameof(IsConnected));
                        }


                        // ═══ 管道节流：基于时间戳（Wall-clock Time）进行跳帧 ═══
                        // 相机以 Fps(如30fps)读帧，目的是清空USB缓冲区避免积压；
                        // 如果录像开启，管道节流帧率取 PreviewFps 和 RecordFps 之间的最大值，确保预览依然流畅
                        int targetPipelineFps = isSave ? Math.Max(PreviewFps, RecordFps) : PreviewFps;
                        double targetIntervalMs = 1000.0 / Math.Max(1, targetPipelineFps);
                        DateTime pipelineNow = DateTime.Now;
                        double elapsedMs = (pipelineNow - _lastPipelineProcessTime).TotalMilliseconds;

                        // 允许 2.0ms 的极小容差（Margin）来抵消操作系统的调度抖动
                        if (elapsedMs < targetIntervalMs - 2.0)
                        {
                            // 丢弃此帧：仅用于清空 USB 缓冲区，直接跳过处理，不进行对象池分配与拷贝
                            continue;
                        }
                        else
                        {
                            _lastPipelineProcessTime = pipelineNow;

                            // 仅在需要处理时从对象池租借并拷贝图像，大幅减少无用帧的分配与拷贝开销
                            tempFrame = Infrastructure.MatPool.Rent(_captureMat.Width, _captureMat.Height, _captureMat.Type());
                            _captureMat.CopyTo(tempFrame);

                            // 兼容性事件通知
                            FrameChanged?.Invoke(tempFrame);

                            // 执行责任链图像处理管道（含水印叠加 + 录像写入）
                            _pipeline.Process(tempFrame, pipelineNow);

                            // 将处理后的图像传递给预览控制器
                            _previewController.UpdateFrame(tempFrame, pipelineNow);

                            // 磁盘检查计数
                            _frameCount++;
                            if (_frameCount % 100 == 0)
                            {
                                _frameCount = 0;
                                CheckDiskSpace();
                            }
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
                StartSaving();
            }
            else
            {
                StopSaving();
            }
        }

        private void StartSaving()
        {
            if (_recorder == null) return;

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

            _recorder.StartRecording(null, recordW, recordH, Fps, Quality);

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
                if (cap == null) return;
                
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

                Thread.Sleep(200); 

                try
                {
                    if (cap.IsOpened())
                    {
                        cap.Release();
                    }
                    cap.Dispose();
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
                    if (_preferredFormat == CaptureFormat.YUY2)
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('Y', 'U', 'Y', '2'));
                    else if (_preferredFormat == CaptureFormat.NV12)
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('N', 'V', '1', '2'));
                    else
                        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));

                    Thread.Sleep(50);

                    cap.Set(VideoCaptureProperties.FrameWidth, Width);
                    cap.Set(VideoCaptureProperties.FrameHeight, Height);
                    cap.Set(VideoCaptureProperties.Fps, Fps);

                    // 强行读取一帧哑帧驱动 DirectShow 内部的 Pin 协商与缓冲区重分配
                    if (cap.IsOpened())
                    {
                        using (Mat dummy = new Mat())
                        {
                            cap.Read(dummy);
                        }
                    }

                    double actualW = cap.Get(VideoCaptureProperties.FrameWidth);
                    double actualH = cap.Get(VideoCaptureProperties.FrameHeight);
                    double actualFps = cap.Get(VideoCaptureProperties.Fps);
                    Logger.LogInfo($"[CameraService #{Index}] 重连配置: {actualW}x{actualH} @ {actualFps}fps");

                    if (!cap.IsOpened())
                    {
                        Logger.LogError($"[CameraService #{Index}] 重新打开摄像头失败");
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
                            ConfigurationManager.SaveSetting($"Camera_{Index}_Resolution", actualResStr);
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
                    _isCapturing = false;

                    if (FrameChanged != null)
                    {
                        foreach (var d in FrameChanged.GetInvocationList())
                        {
                            FrameChanged -= (FrameChangedEventHandler)d;
                        }
                    }

                    _recorder?.Dispose();
                    _previewController?.Dispose();
                    _claheProcessor?.Dispose();
                    _timestampOverlayProcessor?.Dispose();

                    if (cap != null)
                    {
                        if (cap.IsOpened())
                        {
                            cap.Release();
                        }
                        cap.Dispose();
                        cap = null;
                    }

                    if (_Frame != null)
                    {
                        _Frame.Dispose();
                        _Frame = null;
                    }

                    if (_captureMat != null)
                    {
                        _captureMat.Dispose();
                        _captureMat = null;
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
