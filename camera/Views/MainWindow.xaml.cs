using FastHotKeyForWPF;
using ImTools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Threading;
using System.Configuration;
using System.ComponentModel;
using camera.ViewModels;
namespace camera.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        private const double TitleBarHeight = 30; // 设置一个合适的标题栏高度

        private double _normalWidth = 900;
        private double _normalHeight = 480;

        public bool CanClickThrough { get; set; } = false;

        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private bool _isRealExit = false;
        private HwndSource _hwndSource;

        // 时间轴缩放与拖拽平移相关私有字段
        private double _zoomLevel = 1.0;          // 缩放级别 (1.0 到 48.0)
        private double _zoomCenterPct = 0.5;      // 缩放中心在 24h 时间轴中的比例 (0.0 到 1.0)
        private bool _isDraggingTimeline = false; // 是否正在被拖拽
        private Point _timelineDragStartPoint;    // 拖动起始点坐标
        private double _timelineDragStartCenterPct;// 拖动起始的 _zoomCenterPct 值
        public MainWindow()
        {
            // 检查是否已有实例在运行
            if (!EnsureSingleInstance())
            {
                try
                {
                    var currentProc = Process.GetCurrentProcess();
                    foreach (var p in Process.GetProcessesByName(currentProc.ProcessName))
                    {
                        if (p.Id != currentProc.Id && p.MainWindowHandle != IntPtr.Zero)
                        {
                            SwitchToThisWindow(p.MainWindowHandle, true);
                            SetForegroundWindow(p.MainWindowHandle);
                            break;
                        }
                    }
                }
                catch { }
                MessageBox.Show("程序已经在运行中，已为您激活并切换到前台显示。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Application.Current.Shutdown();
                return;
            }

            InitializeComponent();
            LoadSettings();
            InitializeTitleBar();

            Loaded += MainWindow_Loaded;
            StateChanged += MainWindow_StateChanged;

            // ★ 自动保存:窗口几何变化 → 立即调度落盘。
            // 原 SaveSettings() 仅在 OnClosing 触发,期间崩溃 → 窗口尺寸/位置丢失。
            SizeChanged     += MainWindow_SizeOrLocationChanged;
            LocationChanged += MainWindow_SizeOrLocationChanged;
        }

        /// <summary>
        /// 主窗口几何变化 → 自动持久化(防抖由 AppSettingsService 内部 Timer 提供)。
        /// ★ Fix P0-2: 删除高频率的 ConfigurationManager.SaveSetting 调用。
        /// 原来拖拽窗口 5 秒会触发 ~300 次 SizeChanged × 4 个 .exe.config 全量写,
        /// 每次 SaveSetting 都会 OpenExeConfiguration + XmlDocument.Save + RefreshSection,
        /// 产生大量临时对象,直接推高 Post-GC baseline。
        /// 现在只调一次 AppSettingsService.RequestSave(),由其 1 秒防抖合并所有写。
        /// </summary>
        private void MainWindow_SizeOrLocationChanged(object? sender, EventArgs e)
        {
            if (WindowState != System.Windows.WindowState.Normal) return;
            double w = this.Width;
            double h = this.Height;
            double l = this.Left;
            double t = this.Top;

            _normalWidth = w;
            _normalHeight = h;

            AppSettingsService.Instance.Update(s =>
            {
                s.WindowWidth = w;
                s.WindowHeight = h;
                s.WindowLeft = l;
                s.WindowTop = t;
            });
        }

        private static Mutex _mutex = null;

        private bool EnsureSingleInstance()
        {
            bool createdNew;
            string mutexName = "Global\\MyApplicationMutex";
            _mutex = new Mutex(true, mutexName, out createdNew);

            if (createdNew)
            {
                // 这是第一个实例
                return true;
            }
            else
            {
                // 已经有一个实例在运行
                return false;
            }
        }

        #region 快捷键相关

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl) && !isHotKeyPressed)
            {
                isHotKeyPressed = true;
                HotKeyPressedAction();
            }
        }

        private void MainWindow_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            {
                isHotKeyPressed = false;
                HotKeyReleasedAction();
            }
        }

        private void HotKeyPressedAction()
        {
            // 在这里执行按下快捷键时的操作
            Debug.WriteLine("热键被按下！");
            // 例如：启用鼠标穿透
            StartClickThrough();
        }

        private void HotKeyReleasedAction()
        {
            // 在这里执行释放快捷键时的操作
            Debug.WriteLine("热键被释放！");
            // 例如：禁用鼠标穿透
            StopClickThrough();
        }



        protected override void OnSourceInitialized(EventArgs e)
        {
            GlobalHotKey.Awake();
            //GlobalHotKey.Add(ModelKeys.CTRL, NormalKeys.F1, GlobalDo);
            
            _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (_hwndSource != null)
            {
                _hwndSource.AddHook(HwndHook);
            }
            
            base.OnSourceInitialized(e);
        }

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVNODES_CHANGED = 0x0007;

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                int eventType = wParam.ToInt32();
                if (eventType == DBT_DEVICEARRIVAL || eventType == DBT_DEVICEREMOVECOMPLETE || eventType == DBT_DEVNODES_CHANGED)
                {
                    var vm = this.DataContext as MainWindowViewModel;
                    if (vm != null)
                    {
                        vm.OnDeviceChangeNotification();
                    }
                }
            }
            return IntPtr.Zero;
        }


        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isRealExit || !this.IsLoaded)
            {
                _isRealExit = true;
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
                base.OnClosing(e);
                return;
            }

            // 读取保存的退出行为
            string exitBehavior = ConfigurationManager.GetSetting("ExitBehavior") ?? "Prompt";

            if (exitBehavior == "MinimizeToTray")
            {
                e.Cancel = true;
                this.Hide();
                return;
            }
            else if (exitBehavior == "Exit")
            {
                _isRealExit = true;
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
                // 让程序开始退出流程
                return;
            }

            // 如果是 Prompt，弹出确认对话框
            var dialog = new ExitConfirmWindow();
            dialog.Owner = this;
            if (dialog.ShowDialog() == true)
            {
                if (dialog.Choice == ExitConfirmWindow.ExitChoice.MinimizeToTray)
                {
                    if (dialog.RememberChoice)
                    {
                        ConfigurationManager.SaveSetting("ExitBehavior", "MinimizeToTray");
                    }
                    e.Cancel = true;
                    this.Hide();
                }
                else if (dialog.Choice == ExitConfirmWindow.ExitChoice.ExitCompletely)
                {
                    if (dialog.RememberChoice)
                    {
                        ConfigurationManager.SaveSetting("ExitBehavior", "Exit");
                    }
                    _isRealExit = true;
                    if (_notifyIcon != null)
                    {
                        _notifyIcon.Visible = false;
                        _notifyIcon.Dispose();
                    }
                }
            }
            else
            {
                e.Cancel = true; // 取消关闭
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_playbackTimelineTimer != null)
            {
                try { _playbackTimelineTimer.Stop(); } catch { }
                _playbackTimelineTimer.Tick -= PlaybackTimelineTimer_Tick;
                _playbackTimelineTimer = null;
            }

            if (_hwndSource != null)
            {
                try { _hwndSource.RemoveHook(HwndHook); } catch { }
                _hwndSource = null;
            }

            SaveSettings();
            base.OnClosed(e);
            GlobalHotKey.Destroy();

            // 1. 释放 DataContext ViewModel 资源（优雅关停所有 CameraService 采集与录像线程）
            try
            {
                (this.DataContext as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"释放 DataContext 异常: {ex.Message}");
            }

            // 2. 释放互斥体
            if (_mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                    _mutex.Close();
                }
                catch { }
            }

            // 3. 触发 WPF 应用程序正常关闭
            try
            {
                Application.Current?.Shutdown();
            }
            catch { }

            // 4. 启动 1 秒看门狗，避免底层 DirectShow/OpenCV/FFmpeg 原生 COM 锁死导致 VS 调试进程挂起
            System.Threading.Tasks.Task.Run(() =>
            {
                System.Threading.Thread.Sleep(1000);
                Environment.Exit(0);
            });
        }


        bool isAutoChangeOpacity = false;

        private void GlobalDo(object sender, HotKeyEventArgs e)
        {
            Debug.WriteLine("按下全局快捷键");
            string keyName = "";
            //解析快捷键文本 
            //if (KeyHelper.UintToKeys.TryGetValue(e.RegisterInfo.ModelKey, out Key foundKey1))
            //{
            //    keyName += foundKey1.ToString();
            //    keyName += " + ";
            //}
            //if (KeyHelper.UintToKey.TryGetValue(e.RegisterInfo.NormalKey, out Key foundKey2))
            //{
            //    keyName += foundKey2.ToString();
            //}

            //Debug.WriteLine(KeyHelper.ValueToUint(sender));


            List<ModelKeys> result = KeyHelper.UintParse(e.RegisterInfo.ModelKey);
            foreach (ModelKeys key in result)
            {
                keyName += key.ToString() + " + ";
            }
            keyName += e.RegisterInfo.NormalKey.ToString();

            Debug.WriteLine(keyName);

            // KeyHelper.UintSum(e.RegisterInfo.ModelKey);
            if (!CanClickThrough)
            {
                if (OpacitySlider.Value > 0.9)
                {
                    isAutoChangeOpacity = true;
                    OpacitySlider.Value = 0.5;
                }
                StartClickThrough();
                CanClickThrough = !CanClickThrough;
                Instructions.Text = "穿透模式(" + keyName + ")";
                Instructions.Visibility = Visibility.Visible;
            }
            else
            {
                if (isAutoChangeOpacity)
                {
                    isAutoChangeOpacity = false;
                    OpacitySlider.Value = 1.0;
                }
                StopClickThrough();
                CanClickThrough = !CanClickThrough;
                Instructions.Visibility = Visibility.Hidden;
            }
        }

        private void GlobalDo2(object sender, HotKeyEventArgs e)
        {
            this.Topmost = !this.Topmost;
        }

        #endregion

        #region 穿透相关
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 初始化系统托盘图标
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Text = "Camera Service";
            try
            {
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jh.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
                }
                else
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                }
            }
            catch
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += (s, args) =>
            {
                this.Show();
                if (this.WindowState == WindowState.Minimized)
                {
                    this.WindowState = WindowState.Normal;
                }
                this.Activate();
                this.Focus();
                var h = new WindowInteropHelper(this).Handle;
                if (h != IntPtr.Zero)
                {
                    SetForegroundWindow(h);
                    SwitchToThisWindow(h, true);
                }
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            var menuExit = new System.Windows.Forms.ToolStripMenuItem("完全退出");
            menuExit.Click += (s, args) =>
            {
                _isRealExit = true;
                this.Close(); // 将会触发真正的关闭
            };
            contextMenu.Items.Add(menuExit);
            _notifyIcon.ContextMenuStrip = contextMenu;

            // 确保窗口在前台可见并获得焦点，置顶显示
            this.WindowState = WindowState.Normal;
            this.Topmost = true;
            this.Show();
            this.Activate();
            this.Focus();

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetForegroundWindow(hwnd);
                SwitchToThisWindow(hwnd, true);
            }

            // 绑定 VM 事件
            var vm = this.DataContext as MainWindowViewModel;
            if (vm != null)
            {
                vm.PlaybackPositionRequest += Vm_PlaybackPositionRequest;
                vm.PropertyChanged += Vm_PropertyChanged;
            }

            _playbackTimelineTimer = new DispatcherTimer();
            _playbackTimelineTimer.Interval = TimeSpan.FromMilliseconds(500);
            _playbackTimelineTimer.Tick += PlaybackTimelineTimer_Tick;

            if (TimelineCanvas != null)
            {
                TimelineCanvas.SizeChanged += (s, ev) => RedrawTimeline();
            }
        }

        // 启用鼠标穿透效果
        private void EnableClickThrough(IntPtr hwnd)
        {
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT); // 启用鼠标穿透
        }

        // 禁用鼠标穿透效果
        private void DisableClickThrough(IntPtr hwnd)
        {
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_TRANSPARENT); // 取消鼠标穿透
        }

        // 在需要时启用穿透效果
        private void StartClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            EnableClickThrough(hwnd);
        }

        // 在需要时禁用穿透效果
        private void StopClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            DisableClickThrough(hwnd);
        }


        #endregion
        private void InitializeTitleBar()
        {
            // 设置标题栏初始状态为可见（使标题栏及窗口轮廓清晰可见）
            TitleBar.Opacity = 1.0;
        }

        private bool isMouseOverTitleBar = false;
        private bool IsMouseOverContentGrid = false;
        public bool isHotKeyPressed { get; private set; } = false;

        #region 拖拽修改窗口大小相关 (8向自由缩放/变形，杜绝锁定宽高比)

        private ResizeDirection _activeResizeDir = ResizeDirection.None;
        private Point _resizeStartScreenPoint;
        private double _resizeStartLeft;
        private double _resizeStartTop;
        private double _resizeStartWidth;
        private double _resizeStartHeight;

        private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            StartWindowResize(ResizeDirection.BottomRight, e);
        }

        private void ResizeBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            if (sender is FrameworkElement fe && fe.Tag is string tag && Enum.TryParse<ResizeDirection>(tag, out var dir))
            {
                StartWindowResize(dir, e);
            }
        }

        private void StartWindowResize(ResizeDirection direction, MouseButtonEventArgs e)
        {
            if (WindowState == WindowState.Maximized) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            _activeResizeDir = direction;
            _resizeStartScreenPoint = this.PointToScreen(e.GetPosition(this));
            _resizeStartLeft = this.Left;
            _resizeStartTop = this.Top;
            _resizeStartWidth = this.ActualWidth > 0 ? this.ActualWidth : this.Width;
            _resizeStartHeight = this.ActualHeight > 0 ? this.ActualHeight : this.Height;

            this.MouseMove += Window_Resize_MouseMove;
            this.MouseLeftButtonUp += Window_Resize_MouseLeftButtonUp;
            this.CaptureMouse();
            e.Handled = true;
        }

        private void Window_Resize_MouseMove(object sender, MouseEventArgs e)
        {
            if (_activeResizeDir == ResizeDirection.None || e.LeftButton != MouseButtonState.Pressed)
                return;

            Point currentScreenPoint = this.PointToScreen(e.GetPosition(this));

            var source = PresentationSource.FromVisual(this);
            double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            double dx = (currentScreenPoint.X - _resizeStartScreenPoint.X) / (dpiX > 0 ? dpiX : 1.0);
            double dy = (currentScreenPoint.Y - _resizeStartScreenPoint.Y) / (dpiY > 0 ? dpiY : 1.0);

            double newWidth = _resizeStartWidth;
            double newHeight = _resizeStartHeight;
            double newLeft = _resizeStartLeft;
            double newTop = _resizeStartTop;

            switch (_activeResizeDir)
            {
                case ResizeDirection.Right:
                    newWidth = Math.Max(MinWindowWidth, _resizeStartWidth + dx);
                    break;

                case ResizeDirection.Bottom:
                    newHeight = Math.Max(MinWindowHeight, _resizeStartHeight + dy);
                    break;

                case ResizeDirection.BottomRight:
                    newWidth = Math.Max(MinWindowWidth, _resizeStartWidth + dx);
                    newHeight = Math.Max(MinWindowHeight, _resizeStartHeight + dy);
                    break;

                case ResizeDirection.Left:
                {
                    double prospectiveWidth = _resizeStartWidth - dx;
                    if (prospectiveWidth >= MinWindowWidth)
                    {
                        newWidth = prospectiveWidth;
                        newLeft = _resizeStartLeft + dx;
                    }
                    else
                    {
                        newWidth = MinWindowWidth;
                        newLeft = _resizeStartLeft + (_resizeStartWidth - MinWindowWidth);
                    }
                    break;
                }

                case ResizeDirection.Top:
                {
                    double prospectiveHeight = _resizeStartHeight - dy;
                    if (prospectiveHeight >= MinWindowHeight)
                    {
                        newHeight = prospectiveHeight;
                        newTop = _resizeStartTop + dy;
                    }
                    else
                    {
                        newHeight = MinWindowHeight;
                        newTop = _resizeStartTop + (_resizeStartHeight - MinWindowHeight);
                    }
                    break;
                }

                case ResizeDirection.TopLeft:
                {
                    double prospectiveWidth = _resizeStartWidth - dx;
                    if (prospectiveWidth >= MinWindowWidth)
                    {
                        newWidth = prospectiveWidth;
                        newLeft = _resizeStartLeft + dx;
                    }
                    else
                    {
                        newWidth = MinWindowWidth;
                        newLeft = _resizeStartLeft + (_resizeStartWidth - MinWindowWidth);
                    }

                    double prospectiveHeight = _resizeStartHeight - dy;
                    if (prospectiveHeight >= MinWindowHeight)
                    {
                        newHeight = prospectiveHeight;
                        newTop = _resizeStartTop + dy;
                    }
                    else
                    {
                        newHeight = MinWindowHeight;
                        newTop = _resizeStartTop + (_resizeStartHeight - MinWindowHeight);
                    }
                    break;
                }

                case ResizeDirection.TopRight:
                {
                    newWidth = Math.Max(MinWindowWidth, _resizeStartWidth + dx);

                    double prospectiveHeight = _resizeStartHeight - dy;
                    if (prospectiveHeight >= MinWindowHeight)
                    {
                        newHeight = prospectiveHeight;
                        newTop = _resizeStartTop + dy;
                    }
                    else
                    {
                        newHeight = MinWindowHeight;
                        newTop = _resizeStartTop + (_resizeStartHeight - MinWindowHeight);
                    }
                    break;
                }

                case ResizeDirection.BottomLeft:
                {
                    double prospectiveWidth = _resizeStartWidth - dx;
                    if (prospectiveWidth >= MinWindowWidth)
                    {
                        newWidth = prospectiveWidth;
                        newLeft = _resizeStartLeft + dx;
                    }
                    else
                    {
                        newWidth = MinWindowWidth;
                        newLeft = _resizeStartLeft + (_resizeStartWidth - MinWindowWidth);
                    }

                    newHeight = Math.Max(MinWindowHeight, _resizeStartHeight + dy);
                    break;
                }
            }

            this.Left = newLeft;
            this.Top = newTop;
            this.Width = newWidth;
            this.Height = newHeight;
            _normalWidth = newWidth;
            _normalHeight = newHeight;
        }

        private void Window_Resize_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _activeResizeDir = ResizeDirection.None;
            this.MouseMove -= Window_Resize_MouseMove;
            this.MouseLeftButtonUp -= Window_Resize_MouseLeftButtonUp;
            this.ReleaseMouseCapture();
        }

        private double MinWindowWidth = 450; // 设置最小宽度
        private double MinWindowHeight = 240; // 设置最小高度

        private void ToggleMaximize()
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            else
                WindowState = WindowState.Maximized;
        }

        public enum ResizeDirection
        {
            None = 0,
            Left = 1,
            Right = 2,
            Top = 3,
            TopLeft = 4,
            TopRight = 5,
            Bottom = 6,
            BottomLeft = 7,
            BottomRight = 8,
        }

        #endregion

        private void MainContentGrid_MouseEnter(object sender, MouseEventArgs e)
        {
            IsMouseOverContentGrid = true;
            WindowResizeGrip.Opacity = 1.0;
            ConsoleToggleButton.Visibility = Visibility.Visible;
            ConsoleToggleButton.BeginAnimation(OpacityProperty, new DoubleAnimation(ConsoleToggleButton.Opacity, 1.0, TimeSpan.FromSeconds(0.2)));
        }

        private void MainContentGrid_MouseLeave(object sender, MouseEventArgs e)
        {
            IsMouseOverContentGrid = false;
            WindowResizeGrip.Opacity = 0.4;
            var vm = this.DataContext as MainWindowViewModel;
            double targetOpacity = (vm != null && vm.ConsoleStatus) ? 0.85 : 0.55;
            ConsoleToggleButton.BeginAnimation(OpacityProperty, new DoubleAnimation(ConsoleToggleButton.Opacity, targetOpacity, TimeSpan.FromSeconds(0.2)));
        }

        private void ShowTitleBar()
        {
            TitleBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3)));
            //Debug.WriteLine("标题栏显示");
        }

        private void HideTitleBar()
        {
            TitleBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.3)));
            //Debug.WriteLine("标题栏隐藏");
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 支持窗口任意自由变形，不再强制锁定宽高比
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (this.Content is Grid rootGrid)
            {
                if (this.WindowState == WindowState.Maximized)
                {
                    this.ClearValue(WidthProperty);
                    this.ClearValue(HeightProperty);

                    MainBorder.Margin = new Thickness(0);
                    MainBorder.BorderThickness = new Thickness(0);
                    MainBorder.Effect = null;

                    if (MainBorder.Child is Border innerBorder)
                    {
                        innerBorder.CornerRadius = new CornerRadius(0);
                    }

                    TitleBar.Margin = new Thickness(0);
                    WindowResizeGrip.Visibility = Visibility.Collapsed;
                    if (WindowResizeBorderGrid != null) WindowResizeBorderGrid.Visibility = Visibility.Collapsed;
                }
                else if (this.WindowState == WindowState.Normal)
                {
                    this.Width = _normalWidth;
                    this.Height = _normalHeight;

                    MainBorder.Margin = new Thickness(10, 2, 2, 10);
                    MainBorder.BorderThickness = new Thickness(1);
                    MainBorder.Effect = (System.Windows.Media.Effects.Effect)FindResource("EffectShadow3");

                    if (MainBorder.Child is Border innerBorder)
                    {
                        innerBorder.CornerRadius = new CornerRadius(5);
                    }

                    TitleBar.Margin = new Thickness(10, 0, 0, 10);
                    WindowResizeGrip.Visibility = Visibility.Visible;
                    if (WindowResizeBorderGrid != null) WindowResizeBorderGrid.Visibility = Visibility.Visible;
                }
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
                this.WindowState = WindowState.Normal;
            else
                this.WindowState = WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void TitleBar_MouseEnter(object sender, MouseEventArgs e)
        {
            isMouseOverTitleBar = true;
            //ShowTitleBar();
            //Debug.WriteLine("鼠标进入标题栏");
            ShowTitleBar();
        }

        private void TitleBar_MouseLeave(object sender, MouseEventArgs e)
        {
            isMouseOverTitleBar = false;
            //HideTitleBarIfNeeded();
            //Debug.WriteLine("鼠标离开标题栏");
            HideTitleBar();
        }

        /// <summary>
        /// 置顶按钮绑定命令
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            this.Topmost = topMostButton.IsChecked == true;
            AppSettingsService.Instance.Update(s => s.Topmost = this.Topmost);
            ConfigurationManager.SaveSetting("Topmost", this.Topmost.ToString());
        }

        private void LoadSettings()
        {
            var appSettings = AppSettingsService.Instance.Current;
            CanClickThrough = appSettings.CanClickThrough;
            this.Topmost = appSettings.Topmost;
            topMostButton.IsChecked = this.Topmost;

            this.MinWindowHeight = appSettings.MinWindowHeight > 0 ? appSettings.MinWindowHeight : 240;
            this.MinWindowWidth = appSettings.MinWindowWidth > 0 ? appSettings.MinWindowWidth : 450;

            double width = appSettings.WindowWidth > 0 ? appSettings.WindowWidth : 900;
            double height = appSettings.WindowHeight > 0 ? appSettings.WindowHeight : 480;
            double left = appSettings.WindowLeft;
            double top = appSettings.WindowTop;

            this.Width = width;
            this.Height = height;
            if (left >= 0) this.Left = left;
            if (top >= 0) this.Top = top;
            this._normalWidth = width;
            this._normalHeight = height;
        }

        private void SaveSettings()
        {
            double widthToSave = this.WindowState == WindowState.Maximized ? _normalWidth : this.Width;
            double heightToSave = this.WindowState == WindowState.Maximized ? _normalHeight : this.Height;

            AppSettingsService.Instance.Update(s =>
            {
                s.CanClickThrough = CanClickThrough;
                s.Topmost = this.Topmost;
                s.WindowWidth = widthToSave;
                s.WindowHeight = heightToSave;
                s.WindowLeft = this.Left;
                s.WindowTop = this.Top;
                s.MinWindowWidth = this.MinWindowWidth;
                s.MinWindowHeight = this.MinWindowHeight;
            });
            AppSettingsService.Instance.SaveSync();

            ConfigurationManager.SaveSetting("CanClickThrough", CanClickThrough.ToString());
            ConfigurationManager.SaveSetting("Topmost", this.Topmost.ToString());
            ConfigurationManager.SaveSetting("WindowWidth", widthToSave.ToString());
            ConfigurationManager.SaveSetting("WindowHeight", heightToSave.ToString());
            ConfigurationManager.SaveSetting("WindowLeft", this.Left.ToString());
            ConfigurationManager.SaveSetting("WindowTop", this.Top.ToString());
            ConfigurationManager.SaveSetting("MinWindowWidth", this.MinWindowWidth.ToString());
            ConfigurationManager.SaveSetting("MinWindowHeight", this.MinWindowHeight.ToString());
        }

        #region 内置回放与 24h 时间轴绘制逻辑

        private DispatcherTimer _playbackTimelineTimer;
        private System.Windows.Shapes.Line _timelineCursorLine;
        private TextBlock _timelineCursorTimeText;

        private void Vm_PlaybackPositionRequest(TimeSpan position)
        {
            if (PlaybackPlayer != null)
            {
                PlaybackPlayer.Position = position;
            }
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            var vm = this.DataContext as MainWindowViewModel;
            if (vm != null) vm.PlaybackState = "Playing";
            PlaybackPlayer?.Play();
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            var vm = this.DataContext as MainWindowViewModel;
            if (vm != null) vm.PlaybackState = "Paused";
            PlaybackPlayer?.Pause();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            var vm = this.DataContext as MainWindowViewModel;
            if (vm != null) vm.PlaybackState = "Stopped";
            PlaybackPlayer?.Stop();
        }

        private void Vm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var vm = sender as MainWindowViewModel;
            if (vm == null) return;

            if (e.PropertyName == nameof(MainWindowViewModel.PlaybackSpeed))
            {
                if (PlaybackPlayer != null)
                {
                    PlaybackPlayer.SpeedRatio = vm.PlaybackSpeed;
                }
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.PlaybackFiles))
            {
                RedrawTimeline();
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.IsPlaybackMode))
            {
                if (vm.IsPlaybackMode)
                {
                    _playbackTimelineTimer?.Start();
                    RedrawTimeline();
                }
                else
                {
                    _playbackTimelineTimer?.Stop();
                    if (PlaybackPlayer != null)
                    {
                        PlaybackPlayer.Stop();
                        PlaybackPlayer.Source = null;
                    }
                    vm.PlaybackState = "Stopped";
                }
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.PlaybackUrl))
            {
                if (PlaybackPlayer != null)
                {
                    if (string.IsNullOrEmpty(vm.PlaybackUrl))
                    {
                        PlaybackPlayer.Source = null;
                        HideOrResetCursor();
                        vm.PlaybackState = "Stopped";
                    }
                    else
                    {
                        PlaybackPlayer.Source = new Uri(vm.PlaybackUrl);
                        PlaybackPlayer.Play();
                        PlaybackPlayer.SpeedRatio = vm.PlaybackSpeed;
                        vm.PlaybackState = "Playing";
                    }
                }
            }
        }

        private void PlaybackPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            var vm = this.DataContext as MainWindowViewModel;
            if (vm != null && PlaybackPlayer != null)
            {
                PlaybackPlayer.SpeedRatio = vm.PlaybackSpeed;
            }
        }

        private void PlaybackPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            var vm = this.DataContext as MainWindowViewModel;
            if (vm != null)
            {
                vm.PlayNextFile();
                if (vm.PlaybackState == "Stopped")
                {
                    PlaybackPlayer?.Stop();
                }
            }
            else
            {
                PlaybackPlayer?.Stop();
            }
        }

        private void TimelineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TimelineCanvas == null) return;
            _isDraggingTimeline = true;
            _timelineDragStartPoint = e.GetPosition(TimelineCanvas);
            _timelineDragStartCenterPct = _zoomCenterPct;
            TimelineCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void TimelineCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingTimeline && TimelineCanvas != null)
            {
                Point currentPoint = e.GetPosition(TimelineCanvas);
                double deltaX = currentPoint.X - _timelineDragStartPoint.X;

                if (Math.Abs(deltaX) > 2)
                {
                    double canvasWidth = TimelineCanvas.ActualWidth;
                    if (canvasWidth > 0)
                    {
                        double deltaPct = (deltaX / canvasWidth) * (1.0 / _zoomLevel);
                        _zoomCenterPct = Math.Clamp(_timelineDragStartCenterPct - deltaPct, 0.0, 1.0);
                        RedrawTimeline();
                    }
                }
            }
        }

        private void TimelineCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingTimeline && TimelineCanvas != null)
            {
                TimelineCanvas.ReleaseMouseCapture();
                _isDraggingTimeline = false;

                Point endPoint = e.GetPosition(TimelineCanvas);
                double deltaX = endPoint.X - _timelineDragStartPoint.X;

                if (Math.Abs(deltaX) <= 3)
                {
                    double canvasWidth = TimelineCanvas.ActualWidth;
                    if (canvasWidth > 0)
                    {
                        double clickPct = endPoint.X / canvasWidth;
                        double viewportWidth = 1.0 / _zoomLevel;
                        double startPct = Math.Clamp(_zoomCenterPct - viewportWidth / 2.0, 0.0, Math.Max(0.0, 1.0 - viewportWidth));
                        double targetPct = startPct + clickPct * viewportWidth;

                        var vm = this.DataContext as MainWindowViewModel;
                        if (vm != null)
                        {
                            vm.TimelineClickCommand.Execute(targetPct);
                        }
                    }
                }
                e.Handled = true;
            }
        }

        private void TimelineCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (TimelineCanvas == null) return;

            Point mousePos = e.GetPosition(TimelineCanvas);
            double canvasWidth = TimelineCanvas.ActualWidth;
            if (canvasWidth <= 0) return;

            double viewportWidth = 1.0 / _zoomLevel;
            double startPct = Math.Clamp(_zoomCenterPct - viewportWidth / 2.0, 0.0, Math.Max(0.0, 1.0 - viewportWidth));
            double mousePctOnCanvas = mousePos.X / canvasWidth;
            double currentMouseTimePct = startPct + mousePctOnCanvas * viewportWidth;

            double oldZoom = _zoomLevel;
            if (e.Delta > 0)
            {
                _zoomLevel = Math.Min(48.0, _zoomLevel * 1.3); // 最大放大到 48 倍
            }
            else
            {
                _zoomLevel = Math.Max(1.0, _zoomLevel / 1.3); // 最小缩放到 1 倍
            }

            if (Math.Abs(_zoomLevel - oldZoom) > 0.001)
            {
                double newViewportWidth = 1.0 / _zoomLevel;
                _zoomCenterPct = currentMouseTimePct - (mousePctOnCanvas - 0.5) * newViewportWidth;
                _zoomCenterPct = Math.Clamp(_zoomCenterPct, 0.0, 1.0);

                RedrawTimeline();
                e.Handled = true;
            }
        }

        private void RedrawTimeline()
        {
            if (TimelineCanvas == null) return;
            TimelineCanvas.Children.Clear();
            _timelineCursorLine = null;
            _timelineCursorTimeText = null;

            var vm = this.DataContext as MainWindowViewModel;
            if (vm == null) return;

            double width = TimelineCanvas.ActualWidth;
            double height = TimelineCanvas.ActualHeight;
            if (width <= 0 || height <= 0) return;

            double viewportWidth = 1.0 / _zoomLevel;
            double startPct = Math.Clamp(_zoomCenterPct - viewportWidth / 2.0, 0.0, Math.Max(0.0, 1.0 - viewportWidth));
            double endPct = startPct + viewportWidth;

            // 1. 绘制背景网格：小时虚线，6小时粗实线，并标注时间文本
            for (int hour = 0; hour <= 24; hour++)
            {
                double hourPct = hour / 24.0;
                if (hourPct >= startPct - 0.01 && hourPct <= endPct + 0.01)
                {
                    double x = ((hourPct - startPct) / viewportWidth) * width;
                    
                    var line = new System.Windows.Shapes.Line
                    {
                        X1 = x,
                        Y1 = 0,
                        X2 = x,
                        Y2 = height - 15,
                        Stroke = new SolidColorBrush(hour % 6 == 0 ? Color.FromRgb(100, 100, 100) : Color.FromRgb(50, 50, 50)),
                        StrokeThickness = hour % 6 == 0 ? 1 : 0.5
                    };
                    if (hour % 6 != 0)
                    {
                        line.StrokeDashArray = new DoubleCollection(new double[] { 2, 2 });
                    }
                    TimelineCanvas.Children.Add(line);

                    if (hour % 3 == 0 || (_zoomLevel > 3.0 && hour % 1 == 0))
                    {
                        var text = new TextBlock
                        {
                            Text = $"{hour:D2}:00",
                            FontSize = 9,
                            Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                            HorizontalAlignment = HorizontalAlignment.Center
                        };
                        Canvas.SetLeft(text, x - 15);
                        Canvas.SetTop(text, height - 15);
                        TimelineCanvas.Children.Add(text);
                    }
                }
            }

            // 2. 绘制录像时间区间红条
            foreach (var file in vm.PlaybackFiles)
            {
                double fileStartPct = file.StartTime.TimeOfDay.TotalSeconds / (24.0 * 3600.0);
                double fileDurationPct = file.Duration.TotalSeconds / (24.0 * 3600.0);
                double fileEndPct = fileStartPct + fileDurationPct;

                if (fileEndPct >= startPct && fileStartPct <= endPct)
                {
                    double relStartPct = (Math.Max(startPct, fileStartPct) - startPct) / viewportWidth;
                    double relEndPct = (Math.Min(endPct, fileEndPct) - startPct) / viewportWidth;

                    double rectX = relStartPct * width;
                    double rectW = Math.Max(2, (relEndPct - relStartPct) * width);

                    var rect = new System.Windows.Shapes.Rectangle
                    {
                        Width = rectW,
                        Height = height - 15,
                        Fill = new SolidColorBrush(Color.FromArgb(120, 229, 57, 53)),
                        Stroke = new SolidColorBrush(Color.FromRgb(229, 57, 53)),
                        StrokeThickness = 0.5,
                        ToolTip = $"{file.StartTime:HH:mm:ss} - {file.EndTime:HH:mm:ss}"
                    };

                    Canvas.SetLeft(rect, rectX);
                    Canvas.SetTop(rect, 0);
                    TimelineCanvas.Children.Add(rect);
                }
            }

            // 3. 重新绘制游标
            UpdateTimelineCursor();
        }

        private void UpdateTimelineCursor()
        {
            if (TimelineCanvas == null) return;
            var vm = this.DataContext as MainWindowViewModel;
            if (vm == null || !vm.IsPlaybackMode)
            {
                if (_playbackTimelineTimer != null && _playbackTimelineTimer.IsEnabled)
                    _playbackTimelineTimer.Stop();
                return;
            }

            double width = TimelineCanvas.ActualWidth;
            double height = TimelineCanvas.ActualHeight;
            if (width <= 0 || height <= 0) return;

            if (string.IsNullOrEmpty(vm.PlaybackUrl) || PlaybackPlayer == null || !PlaybackPlayer.NaturalDuration.HasTimeSpan)
            {
                HideOrResetCursor();
                return;
            }

            PlaybackFileModel currentPlayingFile = null;
            foreach (var file in vm.PlaybackFiles)
            {
                if (file.FilePath == vm.PlaybackUrl)
                {
                    currentPlayingFile = file;
                    break;
                }
            }

            if (currentPlayingFile == null)
            {
                HideOrResetCursor();
                return;
            }

            TimeSpan currentTimeOfDay = currentPlayingFile.StartTime.TimeOfDay + PlaybackPlayer.Position;
            double cursorPct = currentTimeOfDay.TotalSeconds / (24.0 * 3600.0);
            
            double viewportWidth = 1.0 / _zoomLevel;
            double startPct = Math.Clamp(_zoomCenterPct - viewportWidth / 2.0, 0.0, Math.Max(0.0, 1.0 - viewportWidth));
            double endPct = startPct + viewportWidth;

            if (cursorPct >= startPct && cursorPct <= endPct)
            {
                double cursorX = ((cursorPct - startPct) / viewportWidth) * width;

                if (_timelineCursorLine == null)
                {
                    _timelineCursorLine = new System.Windows.Shapes.Line
                    {
                        Y1 = 0,
                        Y2 = height - 15,
                        Stroke = Brushes.Gold,
                        StrokeThickness = 2.0
                    };
                    TimelineCanvas.Children.Add(_timelineCursorLine);
                }
                else
                {
                    _timelineCursorLine.Visibility = Visibility.Visible;
                }
                _timelineCursorLine.X1 = cursorX;
                _timelineCursorLine.X2 = cursorX;

                if (_timelineCursorTimeText == null)
                {
                    _timelineCursorTimeText = new TextBlock
                    {
                        FontSize = 9.5,
                        Foreground = Brushes.Gold,
                        FontWeight = FontWeights.Bold,
                        Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                        Padding = new Thickness(2)
                    };
                    TimelineCanvas.Children.Add(_timelineCursorTimeText);
                }
                else
                {
                    _timelineCursorTimeText.Visibility = Visibility.Visible;
                }
                _timelineCursorTimeText.Text = currentTimeOfDay.ToString(@"hh\:mm\:ss");
                Canvas.SetLeft(_timelineCursorTimeText, Math.Clamp(cursorX - 25, 0, width - 55));
                Canvas.SetTop(_timelineCursorTimeText, 2);
            }
            else
            {
                HideOrResetCursor();
            }
        }

        private void HideOrResetCursor()
        {
            if (_timelineCursorLine != null) _timelineCursorLine.Visibility = Visibility.Collapsed;
            if (_timelineCursorTimeText != null) _timelineCursorTimeText.Visibility = Visibility.Collapsed;
        }

        private void PlaybackTimelineTimer_Tick(object sender, EventArgs e)
        {
            UpdateTimelineCursor();
        }

        #endregion

        #region 拖拽网格画面调换位置

        private Point _dragStartPoint;

        private void ImageBlock_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void ImageBlock_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point mousePos = e.GetPosition(null);
                Vector diff = _dragStartPoint - mousePos;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var imageBlock = sender as FrameworkElement;
                    var cam = imageBlock?.DataContext as CameraService;
                    if (cam != null)
                    {
                        DragDrop.DoDragDrop(imageBlock, cam, DragDropEffects.Move);
                    }
                }
            }
        }

        private void ImageBlock_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            if (e.Data.GetDataPresent(typeof(CameraService)))
            {
                e.Effects = DragDropEffects.Move;
            }
            e.Handled = true;
        }

        private void ImageBlock_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(CameraService)))
            {
                var sourceCam = e.Data.GetData(typeof(CameraService)) as CameraService;
                var targetBlock = sender as FrameworkElement;
                var targetCam = targetBlock?.DataContext as CameraService;

                if (sourceCam != null && targetCam != null && sourceCam != targetCam)
                {
                    var vm = this.DataContext as MainWindowViewModel;
                    if (vm != null)
                    {
                        int sourceIndex = vm.Cameras.IndexOf(sourceCam);
                        int targetIndex = vm.Cameras.IndexOf(targetCam);
                        if (sourceIndex >= 0 && targetIndex >= 0)
                        {
                            vm.Cameras.Move(sourceIndex, targetIndex);
                            vm.SaveCameraOrder();
                        }
                    }
                }
            }
            e.Handled = true;
        }

        #endregion
    }
}