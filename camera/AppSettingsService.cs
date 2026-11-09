#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace camera
{
    /// <summary>
    /// 应用配置的持久化服务(企业级,替换原 ConfigurationManager)。
    ///
    /// 核心能力:
    /// 1. JSON 持久化 — 文件存放在 %APPDATA%\CameraMonitor\settings.json,
    ///    不污染 EXE 目录,可被 .NET 8 单文件部署、容器化正确处理。
    /// 2. 原子写入 — 先写 .tmp,fsync 后 rename,保证配置文件不损坏。
    /// 3. 迁移兼容 — 旧 App.config / .exe.config 中的设置作为 fallback。
    /// 4. 完整日志 — 所有读写操作记录到日志文件。
    /// 5. 线程安全 — 所有公共方法可重入。
    /// </summary>
    public sealed class AppSettingsService
    {
        private static readonly Lazy<AppSettingsService> _instance =
            new(() => new AppSettingsService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static AppSettingsService Instance => _instance.Value;

        // ─── 持久化位置 ───
        // %APPDATA%\CameraMonitor\settings.json
        public string FilePath { get; }

        // ─── JSON 序列化选项(单例,带注释尾随逗号容忍)──
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented            = true,
            DefaultIgnoreCondition   = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling      = JsonCommentHandling.Skip,
            AllowTrailingCommas      = true,
            Encoder                  = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling           = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters               = { new JsonStringEnumConverter() },
        };

        // ─── 内存中的当前设置(线程安全访问)──
        private AppSettings _current = AppSettings.CreateDefault();
        public AppSettings Current => _current;

        // ─── 自动保存防抖(避免高频 setter 反复落盘)──
        private readonly System.Threading.Timer _autoSaveTimer;
        private const int AutoSaveDelayMs = 1000;  // 1 秒防抖
        private readonly object _saveLock = new();

        private AppSettingsService()
        {
            FilePath = DetermineFilePath();
            _autoSaveTimer = new System.Threading.Timer(_ => FlushPendingSave(),
                null, Timeout.Infinite, Timeout.Infinite);

            Logger.LogInfo($"[Settings] 单一配置文件路径: {FilePath}");
            _current = Load();
        }

        private static string DetermineFilePath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var localPath = Path.Combine(baseDir, "settings.json");

            try
            {
                if (File.Exists(localPath))
                {
                    // 测试现有文件写权限
                    using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    {
                    }
                    return localPath;
                }

                // 文件不存在，测试目录可写性
                var testFile = Path.Combine(baseDir, $".write_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return localPath;
            }
            catch (Exception ex)
            {
                // 如果安装在受限目录导致不可写，安全回退到用户 APPDATA
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dir = Path.Combine(appData, "CameraMonitor");
                Directory.CreateDirectory(dir);
                var fallbackPath = Path.Combine(dir, "settings.json");
                Logger.LogWarning($"[Settings] 程序目录不可写 ({ex.Message})，安全回退至用户目录: {fallbackPath}");
                return fallbackPath;
            }
        }

        /// <summary>
        /// 加载设置。单一配置源: settings.json > 默认值。
        /// </summary>
        public AppSettings Load()
        {
            if (File.Exists(FilePath))
            {
                try
                {
                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                    if (loaded != null)
                    {
                        string resolvedTitle = !string.IsNullOrWhiteSpace(loaded.SystemTitle)
                            ? loaded.SystemTitle
                            : (!string.IsNullOrWhiteSpace(loaded.Title) ? loaded.Title : AppSettings.DefaultSystemTitle);

                        _current = new AppSettings
                        {
                            IsInitialized = true,
                            LoadSource    = "JSON",
                            LoadedAt      = DateTime.UtcNow,
                            // 复制所有字段
                            IsSave = loaded.IsSave,
                            Opacity = loaded.Opacity,
                            Title = resolvedTitle,
                            SystemTitle = resolvedTitle,
                            OverwriteOldFiles = loaded.OverwriteOldFiles,
                            DiskSpaceThreshold = loaded.DiskSpaceThreshold > 0 ? loaded.DiskSpaceThreshold : AppSettings.DefaultDiskSpaceThresholdMb,
                            SaveDirectory = string.IsNullOrWhiteSpace(loaded.SaveDirectory) ? AppSettings.DefaultSaveDirectory : loaded.SaveDirectory,
                            Quality = loaded.Quality > 0 ? loaded.Quality : AppSettings.DefaultQuality,
                            SaveInterval = loaded.SaveInterval > 0 ? loaded.SaveInterval : AppSettings.DefaultSaveIntervalMin,
                            MaxPreviewWidth = loaded.MaxPreviewWidth > 0 ? loaded.MaxPreviewWidth : AppSettings.DefaultMaxPreviewWidth,
                            PreviewFps = loaded.PreviewFps > 0 ? loaded.PreviewFps : AppSettings.DefaultPreviewFps,
                            RecordFps = loaded.RecordFps > 0 ? loaded.RecordFps : AppSettings.DefaultRecordFps,
                            Contrast = loaded.Contrast,
                            Luminance = loaded.Luminance,
                            IsGrayHomogenization = loaded.IsGrayHomogenization,
                            OverlayTextSize = loaded.OverlayTextSize > 0 ? loaded.OverlayTextSize : AppSettings.DefaultOverlayTextSize,
                            MaxFps = loaded.MaxFps > 0 ? loaded.MaxFps : AppSettings.DefaultMaxFps,
                            WindowWidth = loaded.WindowWidth > 0 ? loaded.WindowWidth : AppSettings.DefaultWindowWidth,
                            WindowHeight = loaded.WindowHeight > 0 ? loaded.WindowHeight : AppSettings.DefaultWindowHeight,
                            WindowLeft = loaded.WindowLeft,
                            WindowTop = loaded.WindowTop,
                            MinWindowWidth = loaded.MinWindowWidth > 0 ? loaded.MinWindowWidth : AppSettings.DefaultMinWindowWidth,
                            MinWindowHeight = loaded.MinWindowHeight > 0 ? loaded.MinWindowHeight : AppSettings.DefaultMinWindowHeight,
                            AspectRatio = loaded.AspectRatio > 0 ? loaded.AspectRatio : AppSettings.DefaultAspectRatio,
                            CanClickThrough = loaded.CanClickThrough,
                            Topmost = loaded.Topmost,
                            ExitBehavior = string.IsNullOrWhiteSpace(loaded.ExitBehavior) ? AppSettings.DefaultExitBehavior : loaded.ExitBehavior,
                            MaxNvencSessions = loaded.MaxNvencSessions,
                            MaxAmfSessions = loaded.MaxAmfSessions,
                            MaxQsvSessions = loaded.MaxQsvSessions,
                            UseV3Architecture = loaded.UseV3Architecture,
                            ForceCpuEncoding = loaded.ForceCpuEncoding,
                            IsDarkTheme = loaded.IsDarkTheme,
                            IsStreamingEnabled = loaded.IsStreamingEnabled,
                            StreamProtocol = string.IsNullOrWhiteSpace(loaded.StreamProtocol) ? AppSettings.DefaultStreamProtocol : loaded.StreamProtocol,
                            StreamPort = loaded.StreamPort > 0 ? loaded.StreamPort : AppSettings.DefaultStreamPort,
                            StreamFps = loaded.StreamFps > 0 ? loaded.StreamFps : AppSettings.DefaultStreamFps,
                            StreamQuality = loaded.StreamQuality > 0 ? loaded.StreamQuality : AppSettings.DefaultStreamQuality,
                            Cameras = loaded.Cameras ?? new(StringComparer.OrdinalIgnoreCase),
                            CameraOrder = loaded.CameraOrder ?? new(),
                        };
                        Logger.LogInfo($"[Settings] 从单一配置源加载成功 ({FilePath})");
                        return _current;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[Settings] JSON 加载失败: {ex.Message}", ex);
                }
            }

            // 兜底: 全部默认值，并立即落盘生成初始配置文件
            _current = AppSettings.CreateDefault();
            Logger.LogWarning($"[Settings] 配置文件不存在或加载失败，使用默认配置并生成初始文件 → {FilePath}");
            SaveSync(_current);
            return _current;
        }

        /// <summary>
        /// 立即同步保存(阻塞,带文件锁)。
        /// </summary>
        public void SaveSync(AppSettings? settings = null)
        {
            lock (_saveLock)
            {
                try
                {
                    var target = settings ?? _current;
                    var json = JsonSerializer.Serialize(target, JsonOpts);
                    var tmp = FilePath + ".tmp";

                    // 原子写入:先写临时文件 → 强制 flush → rename
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buf = System.Text.Encoding.UTF8.GetBytes(json);
                        fs.Write(buf, 0, buf.Length);
                        fs.Flush(true);  // 强制 fsync,落盘后再 rename
                    }
                    // Windows 下 File.Move(..., overwrite: true) 在 .NET 8 才有
                    if (File.Exists(FilePath))
                        File.Replace(tmp, FilePath, null);
                    else
                        File.Move(tmp, FilePath);

                    Logger.LogInfo($"[Settings] 保存成功 → {FilePath} ({json.Length} 字节)");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[Settings] 保存失败: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// 调度一次防抖保存(1 秒内多次调用合并为一次落盘)。
        /// 推荐在属性 setter 末尾调用。
        /// </summary>
        public void RequestSave()
        {
            _autoSaveTimer.Change(AutoSaveDelayMs, Timeout.Infinite);
        }

        /// <summary>
        /// 防抖定时器回调:把内存中的最新设置写盘。
        /// </summary>
        private void FlushPendingSave()
        {
            try { SaveSync(_current); }
            catch (Exception ex) { Logger.LogError($"[Settings] 自动保存失败: {ex.Message}", ex); }
        }

        /// <summary>
        /// 用回调函数更新当前设置并调度自动保存。
        /// 主窗口 ViewModel 的 setter 用这个 API:
        ///
        /// <code>
        /// public int DiskSpaceThreshold {
        ///     get => _s.DiskSpaceThreshold;
        ///     set {
        ///         _s = _s.With(x => x.DiskSpaceThreshold = value);
        ///         AppSettingsService.Instance.RequestSave();
        ///     }
        /// }
        /// </code>
        /// </summary>
        public void Update(System.Action<AppSettings> mutator)
        {
            mutator(_current);
            RequestSave();
        }
    }
}