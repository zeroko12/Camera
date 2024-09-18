using System.Windows;

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
