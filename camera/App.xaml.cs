using camera.Views;
using Prism.Ioc;
using System.Diagnostics;
using System.Windows;

namespace camera
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        protected override Window CreateShell()
        {
            return Container.Resolve<MainWindow>();
        }

        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {

        }

        protected override void OnExit(ExitEventArgs e)
        {
            Debug.WriteLine("程序退出");
            base.OnExit(e);
        }
    }
}
