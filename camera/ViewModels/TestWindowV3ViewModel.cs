using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Prism.Commands;
using Prism.Mvvm;

namespace camera.ViewModels
{
    using camera.CameraV3.Core;
    using OpenCvSharp;
    using OpenCvSharp.WpfExtensions;

    public class TestWindowV3ViewModel : BindableBase
    {
        private CameraServiceV3? _cameraService;
        private string _cameraStatus = "未启动";
        private string _recordingStatus = "未录制";
        private string _currentRecordingPath = "";
        private string _resolution = "0x0";
        private string _fps = "0";
        private BitmapSource? _currentFrame;
        private Action? _closeWindowAction;

        public ObservableCollection<string> Logs { get; } = new ObservableCollection<string>();

        public string CameraStatus
        {
            get => _cameraStatus;
            set => SetProperty(ref _cameraStatus, value);
        }

        public string RecordingStatus
        {
            get => _recordingStatus;
            set => SetProperty(ref _recordingStatus, value);
        }

        public string CurrentRecordingPath
        {
            get => _currentRecordingPath;
            set => SetProperty(ref _currentRecordingPath, value);
        }

        public string Resolution
        {
            get => _resolution;
            set => SetProperty(ref _resolution, value);
        }

        public string Fps
        {
            get => _fps;
            set => SetProperty(ref _fps, value);
        }

        public BitmapSource? CurrentFrame
        {
            get => _currentFrame;
            set => SetProperty(ref _currentFrame, value);
        }

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand StartRecordingCommand { get; }
        public ICommand StopRecordingCommand { get; }
        public ICommand CloseCommand { get; }

        public TestWindowV3ViewModel(Action closeWindowAction)
        {
            _closeWindowAction = closeWindowAction;
            StartCommand = new DelegateCommand(StartCamera);
            StopCommand = new DelegateCommand(StopCamera);
            StartRecordingCommand = new DelegateCommand(StartRecording);
            StopRecordingCommand = new DelegateCommand(StopRecording);
            CloseCommand = new DelegateCommand(CloseWindow);

            AddLog("TestWindowV3 已初始化");
            AddLog("点击「启动摄像头」开始测试");
        }

        private void AddLog(string message)
        {
            string logMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                Logs.Insert(0, logMessage);
                if (Logs.Count > 100)
                {
                    Logs.RemoveAt(Logs.Count - 1);
                }
            });
        }

        private void StartCamera()
        {
            try
            {
                AddLog("正在启动摄像头...");

                var saveDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    "CameraTestV3");

                if (!Directory.Exists(saveDirectory))
                {
                    Directory.CreateDirectory(saveDirectory);
                    AddLog($"创建保存目录: {saveDirectory}");
                }

                _cameraService = CameraServiceBuilder.Create()
                    .ConfigureOptions(opt =>
                    {
                        opt.CameraIndex = 0;
                        opt.Width = 1280;
                        opt.Height = 720;
                        opt.Fps = 30;
                        opt.PreviewFps = 15;
                        opt.RecordFps = 15;
                        opt.SaveDirectory = saveDirectory;
                        opt.EnablePreview = true;
                    })
                    .UseAutoCapture(0)
                    .UseDefaultPreview()
                    .UseDefaultRecorder()
                    .Build();

                _cameraService.Started += (s, e) =>
                {
                    AddLog("摄像头已启动");
                    CameraStatus = "运行中";
                    Resolution = $"{_cameraService.Width}x{_cameraService.Height}";
                    Fps = $"{_cameraService.Fps}";
                };

                _cameraService.Stopped += (s, e) =>
                {
                    AddLog("摄像头已停止");
                    CameraStatus = "已停止";
                };

                _cameraService.Error += (s, ex) =>
                {
                    AddLog($"错误: {ex.Message}");
                };

                _cameraService.FrameReady += (s, frame) =>
                {
                    try
                    {
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            CurrentFrame = frame.ToBitmapSource();
                        });
                    }
                    catch (Exception ex)
                    {
                        AddLog($"帧更新错误: {ex.Message}");
                    }
                };

                _cameraService.RecordingStarted += (s, e) =>
                {
                    AddLog("录制已开始");
                    RecordingStatus = "录制中";
                    CurrentRecordingPath = _cameraService.CurrentRecordingPath ?? "";
                };

                _cameraService.RecordingStopped += (s, e) =>
                {
                    AddLog("录制已停止");
                    RecordingStatus = "已停止";
                };

                bool success = _cameraService.Start();
                if (success)
                {
                    AddLog("启动命令已发送");
                }
                else
                {
                    AddLog("启动失败");
                }
            }
            catch (Exception ex)
            {
                AddLog($"启动异常: {ex.Message}");
                MessageBox.Show($"启动摄像头失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopCamera()
        {
            try
            {
                AddLog("正在停止摄像头...");
                _cameraService?.Stop();
                _cameraService?.Dispose();
                _cameraService = null;
                AddLog("摄像头已释放");
            }
            catch (Exception ex)
            {
                AddLog($"停止异常: {ex.Message}");
            }
        }

        private void StartRecording()
        {
            try
            {
                AddLog("正在开始录制...");
                if (_cameraService?.StartRecording() == true)
                {
                    AddLog("录制命令已发送");
                }
                else
                {
                    AddLog("录制启动失败，请先启动摄像头");
                }
            }
            catch (Exception ex)
            {
                AddLog($"录制异常: {ex.Message}");
            }
        }

        private void StopRecording()
        {
            try
            {
                AddLog("正在停止录制...");
                _cameraService?.StopRecording();
                CurrentRecordingPath = "";
            }
            catch (Exception ex)
            {
                AddLog($"停止录制异常: {ex.Message}");
            }
        }

        private void CloseWindow()
        {
            try
            {
                AddLog("正在关闭窗口...");
                StopCamera();
                _closeWindowAction?.Invoke();
            }
            catch (Exception ex)
            {
                AddLog($"关闭异常: {ex.Message}");
            }
        }
    }
}
