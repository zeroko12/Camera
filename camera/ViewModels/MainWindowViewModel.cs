using ImTools;
using MaterialDesignColors;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace camera.ViewModels
{
    public class MainWindowViewModel : BindableBase
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

        #endregion


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
            int index = 0;

            while (true)
            {
                var camera = new CameraService();
                if (!camera.StartCapture(index))
                {
                    break;
                }
                Cameras.Add(camera);
                camera.DiskSpaceLow += Camera_DiskSpaceLow;
                index++;
            }

            if (Cameras.Count == 0)
            {
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
        private void LoadSettings()
        {
            isSave = bool.Parse(ConfigurationManager.GetSetting("isSave") ?? "true");
            Opacity = double.Parse(ConfigurationManager.GetSetting("Opacity") ?? "1.0");
            Title = ConfigurationManager.GetSetting("Title") ?? "监控";
            OverwriteOldFiles = bool.Parse(ConfigurationManager.GetSetting("OverwriteOldFiles") ?? "true");
            DiskSpaceThreshold = int.Parse(ConfigurationManager.GetSetting("DiskSpaceThreshold") ?? "1024");
            Quality = int.Parse(ConfigurationManager.GetSetting("Quality") ?? "30");
            SaveInterval = int.Parse(ConfigurationManager.GetSetting("SaveInterval") ?? "1");

        }

        private void SaveSettings()
        {
            ConfigurationManager.SaveSetting("isSave", isSave.ToString());
            ConfigurationManager.SaveSetting("Opacity", Opacity.ToString());
            ConfigurationManager.SaveSetting("Title", Title);
            ConfigurationManager.SaveSetting("OverwriteOldFiles", OverwriteOldFiles.ToString());
            ConfigurationManager.SaveSetting("DiskSpaceThreshold", this.DiskSpaceThreshold.ToString());
            ConfigurationManager.SaveSetting("Quality", this.Quality.ToString());
            ConfigurationManager.SaveSetting("SaveInterval", this.SaveInterval.ToString());
        }

        #endregion
    }
}
