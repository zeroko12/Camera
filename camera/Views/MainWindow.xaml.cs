using System.Windows;
using System.Windows.Input;

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
            //MainBorder.MouseEnter += MainBorder_MouseEnter;
            //MainBorder.MouseLeave += MainBorder_MouseLeave;
        }

        private void MainBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            VisualStateManager.GoToState(this, "MouseOver", true);
        }

        private void MainBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            VisualStateManager.GoToState(this, "Normal", true);
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
    }
}
