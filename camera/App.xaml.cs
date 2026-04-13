using camera.Views;
using Prism.Ioc;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace camera
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                Logger.LogInfo("应用程序启动中...");

                // 注册全局异常处理
                RegisterGlobalExceptionHandling();

                Logger.LogInfo("全局异常处理已注册");

                base.OnStartup(e);

                Logger.LogInfo("应用程序启动完成");
            }
            catch (Exception ex)
            {
                Logger.LogFatal("应用程序启动失败", ex);
                MessageBox.Show(
                    $"应用程序启动失败！\n\n错误: {ex.Message}\n\n日志位置: {Logger.GetLogFilePath()}",
                    "启动错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Environment.Exit(1);
            }
        }

        protected override Window CreateShell()
        {
            try
            {
                Logger.LogInfo("创建主窗口...");
                var window = Container.Resolve<MainWindow>();
                Logger.LogInfo("主窗口创建成功");
                return window;
            }
            catch (Exception ex)
            {
                Logger.LogFatal("创建主窗口失败", ex);
                MessageBox.Show(
                    $"创建主窗口失败！\n\n错误: {ex.Message}\n\n日志位置: {Logger.GetLogFilePath()}",
                    "初始化错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                throw;
            }
        }

        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {
            Logger.LogInfo("注册依赖注入类型...");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Logger.LogInfo("应用程序正在退出...");
            Debug.WriteLine("程序退出");
            base.OnExit(e);
            Logger.LogInfo("应用程序已退出");
        }

        /// <summary>
        /// 注册全局异常处理
        /// </summary>
        private void RegisterGlobalExceptionHandling()
        {
            // UI 线程未捕获异常
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;

            // 非 UI 线程未捕获异常
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // Task 未捕获异常
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            Logger.LogInfo("已注册 3 个全局异常处理器:");
            Logger.LogInfo("  1. DispatcherUnhandledException (UI 线程)");
            Logger.LogInfo("  2. UnhandledException (非 UI 线程)");
            Logger.LogInfo("  3. UnobservedTaskException (Task 异常)");
        }

        private bool _isHandlingException = false;
        /// <summary>
        /// UI 线程未捕获异常处理
        /// </summary>
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // 防重入：如果在处理异常的过程中再次发生异常，直接记录并退出
            if (_isHandlingException)
            {
                Debug.WriteLine("检测到递归异常处理，正在强制终止程序...");
                return;
            }

            _isHandlingException = true;
            try
            {
                // [1] 首先确保日志被写入（最重要）
                try
                {
                    Logger.LogError("UI 线程未捕获异常", e.Exception);
                }
                catch (Exception logEx)
                {
                    Debug.WriteLine("记录 UI 异常失败: " + logEx.Message);
                }

                // [2] 尝试正常弹窗询问用户是否继续
                MessageBoxResult result = MessageBoxResult.No;
                try
                {
                    result = MessageBox.Show(
                        $"程序遇到错误！\n\n错误: {e.Exception.Message}\n\n是否继续运行？\n\n点击「是」尝试继续，点击「否」退出并查看日志。\n\n日志位置: {Logger.GetLogFilePath()}",
                        "系统异常",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Error);
                }
                catch
                {
                    // 如果弹窗失败（例如内存溢出或窗口句柄耗尽），默认退出
                    result = MessageBoxResult.No;
                }

                if (result == MessageBoxResult.Yes)
                {
                    e.Handled = true;
                    Logger.LogWarning("用户选择忽略异常继续运行");
                }
                else
                {
                    Logger.LogWarning("用户选择退出程序");
                    e.Handled = false;
                    // 如果选择退出，确保所有 FFmpeg 等资源被清理（理想情况下通过 OnExit，但此处直接关掉进程更安全）
                    Environment.Exit(1);
                }
            }
            catch (Exception fatalEx)
            {
                // 最后的防线：绝对不能再抛出任何异常
                try { Logger.LogFatal("全局异常处理器内部崩溃", fatalEx); } catch { }
                Debug.WriteLine("FATAL EXCEPTION IN HANDLER: " + fatalEx.Message);
            }
            finally
            {
                _isHandlingException = false;
            }
        }

        /// <summary>
        /// 非 UI 线程未捕获异常处理
        /// </summary>
        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                Exception ex = e.ExceptionObject as Exception;
                Logger.LogFatal($"非 UI 线程未捕获异常 (IsTerminating: {e.IsTerminating})", ex);

                if (e.IsTerminating)
                {
                    // 程序即将终止，显示错误信息
                    MessageBox.Show(
                        $"程序遇到严重错误，即将退出！\n\n错误: {ex?.Message}\n\n日志位置: {Logger.GetLogFilePath()}",
                        "严重错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Stop);
                }
            }
            catch (Exception logEx)
            {
                Debug.WriteLine($"记录非 UI 线程异常失败: {logEx.Message}");
            }
        }

        /// <summary>
        /// Task 未观察到的异常处理
        /// </summary>
        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                Logger.LogError("Task 未观察到的异常", e.Exception);

                // 标记为已观察，防止程序崩溃
                e.SetObserved();

                Logger.LogWarning("Task 异常已被观察并处理");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理 Task 异常失败: {ex.Message}");
            }
        }
    }
}
