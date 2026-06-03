using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace camera
{
    /// <summary>
    /// 简单的文件日志记录器
    /// </summary>
    public static class Logger
    {
        private static readonly string LogDirectory;
        private static string _currentDateStr;
        private static string _logFilePath;
        private static readonly object _lockObj = new object();

        static Logger()
        {
            try
            {
                // 日志目录：应用程序目录下的 Logs 文件夹
                LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }

                UpdateLogFilePath();

                // 写入启动标记
                LogInfo("=".PadRight(80, '='));
                LogInfo($"应用程序启动 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                LogInfo($"日志存储目录: {LogDirectory}");
                LogInfo("=".PadRight(80, '='));

                // 启动时清理一次旧日志
                CleanOldLogs();
            }
            catch (Exception ex)
            {
                // 如果日志初始化失败，至少输出到调试窗口
                Debug.WriteLine($"日志初始化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新日志文件路径（支持每日分文件）
        /// </summary>
        private static void UpdateLogFilePath()
        {
            _currentDateStr = DateTime.Now.ToString("yyyyMMdd");
            string logFileName = $"camera_{_currentDateStr}.log";
            _logFilePath = Path.Combine(LogDirectory, logFileName);
        }

        /// <summary>
        /// 记录信息日志
        /// </summary>
        public static void LogInfo(string message)
        {
            WriteLog("INFO", message);
        }

        /// <summary>
        /// 记录警告日志
        /// </summary>
        public static void LogWarning(string message)
        {
            WriteLog("WARN", message);
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        public static void LogError(string message, Exception ex = null)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(message);

            if (ex != null)
            {
                sb.AppendLine($"异常类型: {ex.GetType().FullName}");
                sb.AppendLine($"异常消息: {ex.Message}");
                sb.AppendLine($"堆栈跟踪: {ex.StackTrace}");

                // 记录内部异常
                if (ex.InnerException != null)
                {
                    sb.AppendLine("--- 内部异常 ---");
                    sb.AppendLine($"类型: {ex.InnerException.GetType().FullName}");
                    sb.AppendLine($"消息: {ex.InnerException.Message}");
                    sb.AppendLine($"堆栈: {ex.InnerException.StackTrace}");
                }
            }

            WriteLog("ERROR", sb.ToString());
        }

        /// <summary>
        /// 记录致命错误
        /// </summary>
        public static void LogFatal(string message, Exception ex = null)
        {
            WriteLog("FATAL", message);
            if (ex != null)
            {
                LogError("致命错误详情", ex);
            }
        }

        /// <summary>
        /// 写入日志文件
        /// </summary>
        private static void WriteLog(string level, string message)
        {
            try
            {
                lock (_lockObj)
                {
                    // 检查日期是否变更，实现每日分文件
                    if (DateTime.Now.ToString("yyyyMMdd") != _currentDateStr)
                    {
                        UpdateLogFilePath();
                        CleanOldLogs(); // 换天时顺便清理
                    }

                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    string logEntry = $"[{timestamp}] [{level}] {message}";

                    // 写入文件
                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine, System.Text.Encoding.UTF8);

                    // 同时输出到调试窗口
                    Debug.WriteLine(logEntry);
                }
            }
            catch (Exception ex)
            {
                // 如果写入失败，至少输出到调试窗口
                Debug.WriteLine($"写入日志失败: {ex.Message}");
                Debug.WriteLine($"原始消息: [{level}] {message}");
            }
        }

        /// <summary>
        /// 清理30天前的日志文件
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

        /// <summary>
        /// 获取日志文件路径（用于调试）
        /// </summary>
        public static string GetLogFilePath()
        {
            return _logFilePath;
        }

        /// <summary>
        /// 打开日志文件夹
        /// </summary>
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
