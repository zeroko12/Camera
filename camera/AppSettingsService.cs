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
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "CameraMonitor");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "settings.json");

            _autoSaveTimer = new System.Threading.Timer(_ => FlushPendingSave(),
                null, Timeout.Infinite, Timeout.Infinite);

            Logger.LogInfo($"[Settings] 配置文件路径: {FilePath}");
            _current = Load();
        }

        /// <summary>
        /// 加载设置。优先级:JSON 文件 > App.config 迁移 > 默认值。
        /// </summary>
        public AppSettings Load()
        {
            // 1. 优先尝试 JSON 文件
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
                            DiskSpaceThreshold = loaded.DiskSpaceThreshold,
                            SaveDirectory = string.IsNullOrWhiteSpace(loaded.SaveDirectory) ? AppSettings.DefaultSaveDirectory : loaded.SaveDirectory,
                            Quality = loaded.Quality,
                            SaveInterval = loaded.SaveInterval,
                            MaxPreviewWidth = loaded.MaxPreviewWidth,
                            PreviewFps = loaded.PreviewFps,
                            RecordFps = loaded.RecordFps,
                            Contrast = loaded.Contrast,
                            Luminance = loaded.Luminance,
                            IsGrayHomogenization = loaded.IsGrayHomogenization,
                            OverlayTextSize = loaded.OverlayTextSize,
                            MaxFps = loaded.MaxFps,
                            WindowWidth = loaded.WindowWidth,
                            WindowHeight = loaded.WindowHeight,
                            WindowLeft = loaded.WindowLeft,
                            WindowTop = loaded.WindowTop,
                            MinWindowWidth = loaded.MinWindowWidth,
                            MinWindowHeight = loaded.MinWindowHeight,
                            AspectRatio = loaded.AspectRatio,
                            CanClickThrough = loaded.CanClickThrough,
                            Topmost = loaded.Topmost,
                            ExitBehavior = loaded.ExitBehavior,
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
                        Logger.LogInfo($"[Settings] 从 JSON 加载成功 ({FilePath})");
                        return _current;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[Settings] JSON 加载失败,回退到默认: {ex.Message}", ex);
                }
            }

            // 2. fallback: 从旧 App.config 迁移
            try
            {
                var migrated = MigrateFromLegacyConfig();
                _current = new AppSettings
                {
                    IsInitialized = true,
                    LoadSource    = "App.config",
                    LoadedAt      = DateTime.UtcNow,
                    IsSave = migrated.IsSave,
                    Opacity = migrated.Opacity,
                    Title = migrated.Title,
                    SystemTitle = migrated.SystemTitle,
                    OverwriteOldFiles = migrated.OverwriteOldFiles,
                    DiskSpaceThreshold = migrated.DiskSpaceThreshold,
                    SaveDirectory = migrated.SaveDirectory,
                    Quality = migrated.Quality,
                    SaveInterval = migrated.SaveInterval,
                    MaxPreviewWidth = migrated.MaxPreviewWidth,
                    PreviewFps = migrated.PreviewFps,
                    RecordFps = migrated.RecordFps,
                    Contrast = migrated.Contrast,
                    Luminance = migrated.Luminance,
                    IsGrayHomogenization = migrated.IsGrayHomogenization,
                    OverlayTextSize = migrated.OverlayTextSize,
                    MaxFps = migrated.MaxFps,
                    WindowWidth = migrated.WindowWidth,
                    WindowHeight = migrated.WindowHeight,
                    WindowLeft = migrated.WindowLeft,
                    WindowTop = migrated.WindowTop,
                    MinWindowWidth = migrated.MinWindowWidth,
                    MinWindowHeight = migrated.MinWindowHeight,
                    AspectRatio = migrated.AspectRatio,
                    CanClickThrough = migrated.CanClickThrough,
                    Topmost = migrated.Topmost,
                    ExitBehavior = migrated.ExitBehavior,
                    MaxNvencSessions = migrated.MaxNvencSessions,
                    MaxAmfSessions = migrated.MaxAmfSessions,
                    MaxQsvSessions = migrated.MaxQsvSessions,
                    UseV3Architecture = migrated.UseV3Architecture,
                    ForceCpuEncoding = migrated.ForceCpuEncoding,
                    IsDarkTheme = migrated.IsDarkTheme,
                    Cameras = migrated.Cameras ?? new(StringComparer.OrdinalIgnoreCase),
                    CameraOrder = migrated.CameraOrder ?? new(),
                };
                Logger.LogInfo("[Settings] 从 App.config 迁移配置");
                // 立即落盘,迁移完成
                SaveSync(_current);
                return _current;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Settings] App.config 迁移失败,使用默认: {ex.Message}", ex);
            }

            // 3. 兜底:全部默认
            _current = AppSettings.CreateDefault();
            Logger.LogWarning("[Settings] 使用默认配置(JSON 与 App.config 都不存在)");
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

        /// <summary>
        /// 从 System.Configuration.ConfigurationManager 迁移所有已知 key。
        /// </summary>
        private AppSettings MigrateFromLegacyConfig()
        {
            var legacy = System.Configuration.ConfigurationManager.AppSettings;
            string Get(string k) => legacy[k];

            int Ip(string k, int d) => int.TryParse(Get(k), out var v) ? v : d;
            bool Bp(string k, bool d) => bool.TryParse(Get(k), out var v) ? v : d;
            double Dp(string k, double d) => double.TryParse(Get(k), out var v) ? v : d;
            string Sp(string k, string d) => string.IsNullOrEmpty(Get(k)) ? d : Get(k);

            var cameras = new Dictionary<string, AppSettings.CameraSettings>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in legacy.AllKeys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (key.StartsWith("Camera_", StringComparison.OrdinalIgnoreCase))
                {
                    int lastUnder = key.LastIndexOf('_');
                    if (lastUnder > 7)
                    {
                        string configKey = key.Substring(7, lastUnder - 7);
                        string prop = key.Substring(lastUnder + 1);

                        if (!cameras.TryGetValue(configKey, out var camSetting))
                        {
                            camSetting = new AppSettings.CameraSettings();
                            cameras[configKey] = camSetting;
                        }

                        string val = legacy[key];
                        switch (prop.ToLowerInvariant())
                        {
                            case "customtext": camSetting.CustomText = val; break;
                            case "resolution": camSetting.Resolution = val; break;
                            case "recordresolution": camSetting.RecordResolution = val; break;
                            case "recordfps": if (int.TryParse(val, out int rfps)) camSetting.RecordFps = rfps; break;
                            case "ismanualdisabled": if (bool.TryParse(val, out bool dis)) camSetting.IsManualDisabled = dis; break;
                            case "isfliphorizontal": if (bool.TryParse(val, out bool fh)) camSetting.IsFlipHorizontal = fh; break;
                            case "isflipvertical": if (bool.TryParse(val, out bool fv)) camSetting.IsFlipVertical = fv; break;
                            case "stretchmode": camSetting.StretchMode = val; break;
                        }
                    }
                }
            }

            string legacyTitle = Sp("SystemTitle", Sp("Title", AppSettings.DefaultSystemTitle));

            return new AppSettings
            {
                IsSave               = Bp("isSave", AppSettings.DefaultIsSave),
                Opacity              = Dp("Opacity", AppSettings.DefaultOpacity),
                Title                = legacyTitle,
                SystemTitle          = legacyTitle,
                OverwriteOldFiles    = Bp("OverwriteOldFiles", AppSettings.DefaultOverwriteOldFiles),
                DiskSpaceThreshold   = Ip("DiskSpaceThreshold", AppSettings.DefaultDiskSpaceThresholdMb),
                SaveDirectory        = Sp("SaveDirectory", AppSettings.DefaultSaveDirectory),
                Quality              = Ip("Quality", AppSettings.DefaultQuality),
                SaveInterval         = Ip("SaveInterval", AppSettings.DefaultSaveIntervalMin),
                MaxPreviewWidth      = Ip("MaxPreviewWidth", AppSettings.DefaultMaxPreviewWidth),
                PreviewFps           = Ip("PreviewFps", AppSettings.DefaultPreviewFps),
                RecordFps            = Ip("RecordFps", AppSettings.DefaultRecordFps),
                Contrast             = Dp("Contrast", AppSettings.DefaultContrast),
                Luminance            = Dp("Luminance", AppSettings.DefaultLuminance),
                IsGrayHomogenization = Bp("IsGrayHomogenization", AppSettings.DefaultIsGrayHomogenization),
                OverlayTextSize      = Dp("OverlayTextSize", AppSettings.DefaultOverlayTextSize),
                MaxFps               = Ip("MaxFps", AppSettings.DefaultMaxFps),
                WindowWidth          = Dp("WindowWidth", AppSettings.DefaultWindowWidth),
                WindowHeight         = Dp("WindowHeight", AppSettings.DefaultWindowHeight),
                WindowLeft           = Dp("WindowLeft", AppSettings.DefaultWindowLeft),
                WindowTop            = Dp("WindowTop", AppSettings.DefaultWindowTop),
                MinWindowWidth       = Dp("MinWindowWidth", AppSettings.DefaultMinWindowWidth),
                MinWindowHeight      = Dp("MinWindowHeight", AppSettings.DefaultMinWindowHeight),
                AspectRatio          = Dp("AspectRatio", AppSettings.DefaultAspectRatio),
                CanClickThrough      = Bp("CanClickThrough", AppSettings.DefaultCanClickThrough),
                Topmost              = Bp("Topmost", AppSettings.DefaultTopmost),
                ExitBehavior         = Sp("ExitBehavior", AppSettings.DefaultExitBehavior),
                MaxNvencSessions     = Ip("MaxNvencSessions", AppSettings.DefaultMaxNvencSessions),
                MaxAmfSessions       = Ip("MaxAmfSessions", AppSettings.DefaultMaxAmfSessions),
                MaxQsvSessions       = Ip("MaxQsvSessions", AppSettings.DefaultMaxQsvSessions),
                UseV3Architecture    = Bp("UseV3Architecture", AppSettings.DefaultUseV3Architecture),
                ForceCpuEncoding     = Bp("ForceCpuEncoding", AppSettings.DefaultForceCpuEncoding),
                IsDarkTheme          = Bp("IsDarkTheme", AppSettings.DefaultIsDarkTheme),
                Cameras              = cameras,
            };
        }
    }
}