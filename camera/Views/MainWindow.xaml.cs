using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace camera.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const double AspectRatio = 12.0 / 9.0;
        public MainWindow()
        {
            InitializeComponent();
        }

        private void MainContentGrid_MouseEnter(object sender, MouseEventArgs e)
        {
            ConsoleToggleButton.Visibility = Visibility.Visible;
            ConsoleToggleButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3)));
        }

        private void MainContentGrid_MouseLeave(object sender, MouseEventArgs e)
        {
            var animation = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.3));
            animation.Completed += (s, _) => ConsoleToggleButton.Visibility = Visibility.Collapsed;
            ConsoleToggleButton.BeginAnimation(OpacityProperty, animation);
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
    }
}
