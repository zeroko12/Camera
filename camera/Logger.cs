using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace camera
{
    /// <summary>
    /// 异步落盘的日志记录器。
    ///
    /// 设计目标:把 LogInfo/LogWarning/LogError 的开销从「同步 I/O + 锁」
    /// 降到「入队 + 返回」(< 1 微秒)。热路径上(如每帧更新、采集回调)可放心调用。
    ///
    /// 实现:
    /// 1. 调用线程只把日志条目塞进 <see cref="BlockingCollection{T}"/>。
    /// 2. 后台 <see cref="_writerThread"/> 单线程消费,负责日期切换、文件旋转、I/O。
    /// 3. 关闭时优雅排空队列。
    /// </summary>
    public static class Logger
    {
        private static readonly string LogDirectory;
        private static string _currentDateStr;
        private static string _logFilePath;
        private static readonly object _lockObj = new object();

        // 有界队列,防止日志风暴把内存吃光。256K 条目按平均 100 字节/条 = 25 MB 上限。
        private static readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 262144);
        private static readonly Thread _writerThread;

        static Logger()
        {
            try
            {
                LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }

                UpdateLogFilePath();

                // 启动后台写线程
                _writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "camera-logger-writer",
                    Priority = ThreadPriority.BelowNormal  // 不抢业务线程
                };
                _writerThread.Start();

                // 写入启动标记(直接同步写,启动期必须可见)
                LogInfo("=".PadRight(80, '='));
                LogInfo($"应用程序启动 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                LogInfo($"日志存储目录: {LogDirectory}");
                LogInfo("=".PadRight(80, '='));

                // 启动时清理一次旧日志
                CleanOldLogs();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"日志初始化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新日志文件路径(支持每日分文件)。
        /// </summary>
        private static void UpdateLogFilePath()
        {
            _currentDateStr = DateTime.Now.ToString("yyyyMMdd");
            string logFileName = $"camera_{_currentDateStr}.log";
            _logFilePath = Path.Combine(LogDirectory, logFileName);
        }

        public static void LogInfo(string message)
        {
            Enqueue("INFO", message);
        }

        public static void LogWarning(string message)
        {
            Enqueue("WARN", message);
        }

        public static void LogError(string message, Exception ex = null)
        {
            if (ex != null)
            {
                var sb = new StringBuilder();
                sb.AppendLine(message);
                sb.AppendLine($"异常类型: {ex.GetType().FullName}");
                sb.AppendLine($"异常消息: {ex.Message}");
                sb.AppendLine($"堆栈跟踪: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    sb.AppendLine("--- 内部异常 ---");
                    sb.AppendLine($"类型: {ex.InnerException.GetType().FullName}");
                    sb.AppendLine($"消息: {ex.InnerException.Message}");
                    sb.AppendLine($"堆栈: {ex.InnerException.StackTrace}");
                }
                Enqueue("ERROR", sb.ToString());
            }
            else
            {
                Enqueue("ERROR", message);
            }
        }

        public static void LogFatal(string message, Exception ex = null)
        {
            Enqueue("FATAL", message);
            if (ex != null)
            {
                LogError("致命错误详情", ex);
            }
        }

        /// <summary>
        /// 入队。失败时回退到 Debug.WriteLine,绝不让日志记录抛出异常打断业务。
        /// </summary>
        private static void Enqueue(string level, string message)
        {
            try
            {
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
                if (!_queue.TryAdd(entry))
                {
                    // 队列满 → 降级到 Debug(避免丢消息但不阻塞调用方)
                    Debug.WriteLine($"[LOG-DROPPED] {entry}");
                }
            }
            catch
            {
                // 不抛异常
            }
        }

        /// <summary>
        /// 后台写线程循环。负责日期切换、文件旋转、磁盘 I/O。
        /// 完全运行在独立线程,与业务线程解耦。
        /// </summary>
        private static void WriterLoop()
        {
            try
            {
                foreach (var entry in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        lock (_lockObj)
                        {
                            // 检查日期变更,实现每日分文件
                            if (DateTime.Now.ToString("yyyyMMdd") != _currentDateStr)
                            {
                                UpdateLogFilePath();
                                CleanOldLogs();
                            }

                            File.AppendAllText(_logFilePath, entry + Environment.NewLine, System.Text.Encoding.UTF8);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"写入日志失败: {ex.Message}");
                        Debug.WriteLine($"原始消息: {entry}");
                    }
                }
            }
            catch (ThreadAbortException) { /* 进程退出 */ }
            catch (Exception ex)
            {
                Debug.WriteLine($"日志写线程崩溃: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理30天前的日志文件。
        /// </summary>
        private static void CleanOldLogs()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                    return;

                var files = Directory.GetFiles(LogDirectory, "camera_*.log");
                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    if ((DateTime.Now - fileInfo.CreationTime).TotalDays > 30)
                    {
                        fileInfo.Delete();
                        Debug.WriteLine($"已删除旧日志文件: {file}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清理旧日志失败: {ex.Message}");
            }
        }

        public static string GetLogFilePath()
        {
            return _logFilePath;
        }

        public static void OpenLogFolder()
        {
            try
            {
                if (Directory.Exists(LogDirectory))
                {
                    Process.Start("explorer.exe", LogDirectory);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"打开日志文件夹失败: {ex.Message}");
            }
        }
    }
}
