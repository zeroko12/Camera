using ImTools;
using MaterialDesignColors;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
            foreach (var camera in Cameras)
            {
                camera.RecordFps = RecordFps;
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
        public ObservableCollection<CameraService> Cameras
        {
            get { return _Cameras; }
            set { SetProperty(ref _Cameras, value); }
        }

        private CameraService _Cam = new();
        public CameraService Cam
        {
            get { return _Cam; }
            set { SetProperty(ref _Cam, value); }
        }
        public MainWindowViewModel()
        {
            init();
            LoadSettings();


        }


        void init()
        {
            Logger.LogInfo("开始初始化摄像头...");
            int index = 0;

            while (true)
            {
                try
                {
                    Logger.LogInfo($"尝试打开摄像头 #{index}...");
                    var camera = new CameraService();

                    if (!camera.StartCapture(index))
                    {
                        Logger.LogInfo($"摄像头 #{index} 打开失败或不存在，停止扫描");
                        camera.Dispose(); // 释放失败的摄像头
                        break;
                    }

                    Cameras.Add(camera);
                    camera.DiskSpaceLow += Camera_DiskSpaceLow;
                    Logger.LogInfo($"✓ 摄像头 #{index} 初始化成功");
                    index++;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"初始化摄像头 #{index} 时发生异常", ex);
                    break;
                }
            }

            Logger.LogInfo($"摄像头初始化完成，共找到 {Cameras.Count} 个摄像头");

            // 从第一个摄像头获取实际帧率作为滑块上限
            if (Cameras.Count > 0)
            {
                MaxFps = (int)Cameras[0].Fps;
                if (MaxFps <= 0) MaxFps = 30;
                Logger.LogInfo($"摄像头最大帧率: {MaxFps}fps");
            }

            if (Cameras.Count == 0)
            {
                Logger.LogWarning("未检测到可用的摄像头，程序将退出");
                MessageBox.Show("未检测到可用的摄像头");
                Application.Current.Shutdown();//退出自身
            }
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
            Cam.ChangeCameraInfo();
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
            SaveSettings();
            Dispose(); // 释放所有资源
        }

        private DelegateCommand _ViewPlayback;
        public DelegateCommand ViewPlayback =>
            _ViewPlayback ?? (_ViewPlayback = new DelegateCommand(ExecuteViewPlayback));

        void ExecuteViewPlayback()
        {
            if (Cameras.Count == 0)
                MessageBox.Show("未连接上摄像头");

            string saveDirectory = Cameras[0].saveDirectory;
            if (!string.IsNullOrEmpty(saveDirectory))
            {
                Process.Start("explorer.exe", saveDirectory);
            }
        }


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

            if (disposing)
            {
                try
                {
                    // 取消所有事件订阅并释放所有 CameraService
                    if (Cameras != null)
                    {
                        foreach (var camera in Cameras)
                        {
                            if (camera != null)
                            {
                                camera.DiskSpaceLow -= Camera_DiskSpaceLow;
                                camera.Dispose();
                            }
                        }
                        Cameras.Clear();
                    }

                    // 释放单独的 Cam 实例（如果使用）
                    Cam?.Dispose();

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
}
