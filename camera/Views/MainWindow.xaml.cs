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

namespace camera.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private double AspectRatio = 12.0 / 9.0;
        private const double TitleBarHeight = 30; // 设置一个合适的标题栏高度

        public bool CanClickThrough { get; set; } = false;
        public MainWindow()
        {
            InitializeComponent();
            AspectRatio = this.Width / this.Height;
            InitializeTitleBar();

            Loaded += MainWindow_Loaded;

            // 移除以下行
            //this.KeyDown += MainWindow_KeyDown;
            //this.KeyUp += MainWindow_KeyUp;

            //默认置顶
            this.Topmost = true;
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
            base.OnSourceInitialized(e);
        }


        protected override void OnClosed(EventArgs e)
        {
            GlobalHotKey.Destroy();
            base.OnClosed(e);
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
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 设置窗口样式为透明
            int exStyle = GetWindowLong(new WindowInteropHelper(this).Handle, GWL_EXSTYLE);
            SetWindowLong(new WindowInteropHelper(this).Handle, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            //StartClickThrough();
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
            // 设置标题栏初始状态为透明
            TitleBar.Opacity = 0;
        }

        private bool isMouseOverTitleBar = false;
        private bool IsMouseOverContentGrid = false;

        private Point lastMousePosition;

        public bool isHotKeyPressed { get; private set; } = false;

        #region 拖拽修改窗口大小相关
        private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
            }
            else
            {
                lastMousePosition = e.GetPosition(this);
                this.MouseMove += ResizeWindow_MouseMove;
                this.MouseLeftButtonUp += ResizeWindow_MouseLeftButtonUp;
                this.CaptureMouse();
            }
        }

        private void ResizeWindow_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPosition = e.GetPosition(this);
                double deltaX = currentPosition.X - lastMousePosition.X;
                double deltaY = currentPosition.Y - lastMousePosition.Y;

                ResizeWindow(deltaX, deltaY);

                lastMousePosition = currentPosition;
            }
        }

        private void ResizeWindow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            this.MouseMove -= ResizeWindow_MouseMove;
            this.MouseLeftButtonUp -= ResizeWindow_MouseLeftButtonUp;
            this.ReleaseMouseCapture();
        }

        private void ResizeWindow(double deltaX, double deltaY)
        {
            double newWidth = Math.Max(MinWidth, this.Width + deltaX);
            double newHeight = Math.Max(MinHeight, this.Height + deltaY);

            // 保持宽高比
            if (newWidth / newHeight > AspectRatio)
            {
                newWidth = newHeight * AspectRatio;
            }
            else
            {
                newHeight = newWidth / AspectRatio;
            }

            this.Width = newWidth;
            this.Height = newHeight;
        }

        private void ToggleMaximize()
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            else
                WindowState = WindowState.Maximized;
        }

        private void ResizeWindow(ResizeDirection direction)
        {
            WindowInteropHelper helper = new WindowInteropHelper(this);
            var hwnd = helper.Handle;

            // 获取当前窗口的大小
            var currentWidth = this.Width;
            var currentHeight = this.Height;

            // 设置比例因子（例如，1.1 表示增加 10%）
            double scaleFactor = 64 / 48;

            // 根据方向计算新的宽度和高度
            double newWidth = currentWidth;
            double newHeight = currentHeight;

            switch (direction)
            {
                case ResizeDirection.Left:
                case ResizeDirection.Right:
                    newWidth *= scaleFactor; // 只调整宽度
                    break;
                case ResizeDirection.Top:
                case ResizeDirection.Bottom:
                    newHeight *= scaleFactor; // 只调整高度
                    break;
                case ResizeDirection.TopLeft:
                case ResizeDirection.BottomRight:
                    newWidth *= scaleFactor;
                    newHeight *= scaleFactor; // 同时调整宽度和高度
                    break;
                case ResizeDirection.TopRight:
                case ResizeDirection.BottomLeft:
                    newWidth *= scaleFactor;
                    newHeight *= scaleFactor; // 同时调整宽度和高度
                    break;
            }
            SendMessage(helper.Handle, 0x112, (IntPtr)(61440 + direction), IntPtr.Zero);
            // 设置新的宽度和高度
            this.Width = newWidth;
            this.Height = newHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        enum ResizeDirection
        {
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
            //Debug.WriteLine("鼠标进入摄像头显示");
            ConsoleToggleButton.Visibility = Visibility.Visible;
            ConsoleToggleButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3)));
            IsMouseOverContentGrid = true;
            WindowResizeGrip.Opacity = 50;
        }

        private void MainContentGrid_MouseLeave(object sender, MouseEventArgs e)
        {
            //Debug.WriteLine("鼠标离开摄像头显示");
            var animation = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.3));
            animation.Completed += (s, _) => ConsoleToggleButton.Visibility = Visibility.Collapsed;
            ConsoleToggleButton.BeginAnimation(OpacityProperty, animation);
            IsMouseOverContentGrid = false;
            WindowResizeGrip.Opacity = 0;
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
            if (e.NewSize.Width / e.NewSize.Height > AspectRatio)
            {
                this.Width = this.Height * AspectRatio;
            }
            else
            {
                this.Height = this.Width / AspectRatio;
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

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            this.Topmost = !this.Topmost;
        }

        //private void MainBorder_MouseEnter(object sender, MouseEventArgs e)
        //{
        //    StartClickThrough();
        //    Debug.WriteLine("启用穿透");
        //}

        //private void MainBorder_MouseLeave(object sender, MouseEventArgs e)
        //{
        //    StopClickThrough();
        //    Debug.WriteLine("禁用穿透");
        //}
    }
}
