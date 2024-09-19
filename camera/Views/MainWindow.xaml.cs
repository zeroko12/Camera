using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace camera.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const double AspectRatio = 12.0 / 9.0;
        private const double TitleBarHeight = 30; // 设置一个合适的标题栏高度

        public MainWindow()
        {
            InitializeComponent();
            InitializeTitleBar();
        }

        private void InitializeTitleBar()
        {
            // 设置标题栏初始状态为透明
            TitleBar.Opacity = 0;
        }

        private bool isMouseOverTitleBar = false;
        private bool IsMouseOverContentGrid = false;

        private void MainContentGrid_MouseEnter(object sender, MouseEventArgs e)
        {
            Debug.WriteLine("鼠标进入摄像头显示");
            ConsoleToggleButton.Visibility = Visibility.Visible;
            ConsoleToggleButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3)));
            IsMouseOverContentGrid = true;
        }

        private void MainContentGrid_MouseLeave(object sender, MouseEventArgs e)
        {
            Debug.WriteLine("鼠标离开摄像头显示");
            var animation = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.3));
            animation.Completed += (s, _) => ConsoleToggleButton.Visibility = Visibility.Collapsed;
            ConsoleToggleButton.BeginAnimation(OpacityProperty, animation);
            IsMouseOverContentGrid = false;
        }

        private void ShowTitleBar()
        {
            TitleBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3)));
            Debug.WriteLine("标题栏显示");
        }

        private void HideTitleBar()
        {
            TitleBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.3)));
            Debug.WriteLine("标题栏隐藏");
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
            Debug.WriteLine("鼠标进入标题栏");
            ShowTitleBar();
        }

        private void TitleBar_MouseLeave(object sender, MouseEventArgs e)
        {
            isMouseOverTitleBar = false;
            //HideTitleBarIfNeeded();
            Debug.WriteLine("鼠标离开标题栏");
            HideTitleBar();
        }
    }
}
