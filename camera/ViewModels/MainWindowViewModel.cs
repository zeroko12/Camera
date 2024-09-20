using Prism.Commands;
using Prism.Mvvm;
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

        private bool _ConsoleStatus = false;
        public bool ConsoleStatus
        {
            get { return _ConsoleStatus; }
            set { SetProperty(ref _ConsoleStatus, value); }
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

        ~MainWindowViewModel()
        {
            MessageBox.Show("析构窗口");
        }
    }
}
