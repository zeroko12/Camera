using MaterialDesignColors;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;

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

        private bool _isSave;
        public bool isSave
        {
            get { return _isSave; }
            set
            {
                SetProperty(ref _isSave, value);
                OnIsSaveChanged();
            }
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
                }
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

            int index = 0;
            while (true)
            {
                var camera = new CameraService();
                if (!camera.StartCapture(index))
                {
                    break;
                }
                Cameras.Add(camera);
                index++;
            }
            isSave = false;

            if (Cameras.Count == 0)
            {
                MessageBox.Show("未检测到可用的摄像头");
            }
            else
            {
                MessageBox.Show($"成功检测到 {Cameras.Count} 个摄像头");
            }

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

        private DelegateCommand _SetSaveMode;
        public DelegateCommand SetSaveMode =>
            _SetSaveMode ?? (_SetSaveMode = new DelegateCommand(ExecuteSetSaveMode));

        void ExecuteSetSaveMode()
        {
            // 交换SaveModeCheck中的两个元素的值
            OverwriteOldFiles = !OverwriteOldFiles;
            OnSaveModeChanged();
        }

        void OnSaveModeChanged()
        {
            if (OverwriteOldFiles)
            {
                foreach (var camera in Cameras)
                {
                    camera.OverwriteOldFiles = true;
                }
                //Debug.WriteLine("保存模式设置为覆盖旧文件");
            }
            else
            {
                foreach (var camera in Cameras)
                {
                    camera.OverwriteOldFiles = false;
                }
                //Debug.WriteLine("保存模式设置为停止录像");
            }
        }

        private DelegateCommand<string> _SetSave;
        public DelegateCommand<string> SetSave =>
            _SetSave ?? (_SetSave = new DelegateCommand<string>(ExecuteSetSave));

        void ExecuteSetSave(string saveModeString)
        {
            if (bool.TryParse(saveModeString, out bool saveMode))
            {
                if (isSave != saveMode)
                {
                    isSave = saveMode;
                    Debug.WriteLine($"设置是否保存为{saveMode}");
                }
            }
            else
            {
                Debug.WriteLine($"无效的保存模式值: {saveModeString}");
            }
        }
        ~MainWindowViewModel()
        {
            MessageBox.Show("析构窗口");
        }
    }
}
