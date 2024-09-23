using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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

        public MainWindow()
        {
            InitializeComponent();
            AspectRatio = this.Width / this.Height;
            InitializeTitleBar();
        }

        private void InitializeTitleBar()
        {
            // 设置标题栏初始状态为透明
            TitleBar.Opacity = 0;
        }

        private bool isMouseOverTitleBar = false;
        private bool IsMouseOverContentGrid = false;

        private Point lastMousePosition;

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
    }
}
