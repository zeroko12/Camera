using System.Windows;
using camera.ViewModels;

namespace camera.Views
{
    public partial class TestWindowV3 : Window
    {
        public TestWindowV3()
        {
            InitializeComponent();
            DataContext = new TestWindowV3ViewModel(() => this.Close());
        }
    }
}
