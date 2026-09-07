using System;
using System.Collections.Generic;

namespace camera
{
    /// <summary>
    /// 强类型应用配置。
    ///
    /// 所有用户可配置项集中在此类,避免散落在多处定义默认值。
    /// 通过 <see cref="AppSettingsService"/> 持久化到 %APPDATA%\CameraMonitor\settings.json,
    /// 老版本 App.config 作为迁移 fallback。
    ///
    /// 设计原则:
    /// 1. <c>init</c> setter 表达不可变性,只有 <see cref="AppSettingsService"/> 能构造新实例。
    /// 2. 默认值集中在此类的静态字段,新增配置只需改一处。
    /// 3. 摄像头特定配置通过嵌套类型 <see cref="CameraSettings"/> 表达。
    /// </summary>
    public sealed class AppSettings
    {
        // ────────────────── 全局默认值(单一来源)──────────────────
        public const bool    DefaultIsSave                = true;
        public const double  DefaultOpacity               = 1.0;
        public const string  DefaultTitle                 = "监控";
        public const string  DefaultSystemTitle           = "季华恒一离子注入机监控";
        public const bool    DefaultOverwriteOldFiles     = true;
        public const int     DefaultDiskSpaceThresholdMb  = 10240;  // 与 App.config 一致
        public const int     DefaultQuality               = 20;
        public const int     DefaultSaveIntervalMin       = 1;
        public const int     DefaultMaxPreviewWidth       = 640;
        public const int     DefaultPreviewFps            = 15;
        public const int     DefaultRecordFps             = 15;
        public const double  DefaultContrast               = 1.0;
        public const double  DefaultLuminance              = 0.0;
        public const bool    DefaultIsGrayHomogenization  = false;
        public const double  DefaultOverlayTextSize        = 0.3;
        public const int     DefaultMaxFps                 = 30;
        public const double  DefaultMinWindowWidth         = 450;
        public const double  DefaultMinWindowHeight        = 240;
        public const double  DefaultWindowWidth            = 900;
        public const double  DefaultWindowHeight           = 480;
        public const double  DefaultWindowLeft             = 100;
        public const double  DefaultWindowTop              = 100;
        public const bool    DefaultCanClickThrough       = false;
        public const bool    DefaultTopmost                = true;
        public const string  DefaultExitBehavior           = "Prompt";
        public const double  DefaultAspectRatio           = 4.0 / 3.0;

        // ────────────────── 摄像头特定默认值 ──────────────────
        public const int     DefaultCameraRecordFps        = 10;
        public const string  DefaultCameraRecordResolution = "与采集分辨率一致";

        // ────────────────── 运行时状态(不持久化)──────────────────
        public bool         IsInitialized   { get; init; }
        public string?      LoadSource      { get; init; }   // "JSON" / "App.config" / "Defaults"
        public DateTime?    LoadedAt        { get; init; }

        // ────────────────── 全局 UI 设置 ──────────────────
        public bool         IsSave              { get; init; }
        public double       Opacity             { get; init; }
        public string       Title               { get; init; } = DefaultTitle;
        public string       SystemTitle         { get; init; } = DefaultSystemTitle;
        public bool         OverwriteOldFiles   { get; init; } = DefaultOverwriteOldFiles;
        public int          DiskSpaceThreshold  { get; init; } = DefaultDiskSpaceThresholdMb;
        public int          Quality             { get; init; } = DefaultQuality;
        public int          SaveInterval        { get; init; } = DefaultSaveIntervalMin;
        public int          MaxPreviewWidth     { get; init; } = DefaultMaxPreviewWidth;
        public int          PreviewFps          { get; init; } = DefaultPreviewFps;
        public int          RecordFps           { get; init; } = DefaultRecordFps;
        public double       Contrast            { get; init; } = DefaultContrast;
        public double       Luminance           { get; init; } = DefaultLuminance;
        public bool         IsGrayHomogenization { get; init; }
        public double       OverlayTextSize     { get; init; } = DefaultOverlayTextSize;
        public int          MaxFps              { get; init; } = DefaultMaxFps;

        // ────────────────── 主窗口几何 ──────────────────
        public double       WindowWidth         { get; init; } = DefaultWindowWidth;
        public double       WindowHeight        { get; init; } = DefaultWindowHeight;
        public double       WindowLeft          { get; init; } = DefaultWindowLeft;
        public double       WindowTop           { get; init; } = DefaultWindowTop;
        public double       MinWindowWidth      { get; init; } = DefaultMinWindowWidth;
        public double       MinWindowHeight     { get; init; } = DefaultMinWindowHeight;
        public double       AspectRatio         { get; init; } = DefaultAspectRatio;
        public bool         CanClickThrough     { get; init; }
        public bool         Topmost             { get; init; } = DefaultTopmost;
        public string       ExitBehavior        { get; init; } = DefaultExitBehavior;

        // ────────────────── 摄像头特定覆盖 ──────────────────
        // key = camera 的稳定配置 key(由 CameraService.GetConfigKey 生成)
        public CameraSettingsCollection Cameras { get; init; } = new();

        /// <summary>
        /// 单个摄像头的覆盖配置(只保存用户改过的项)。
        /// </summary>
        public sealed class CameraSettings
        {
            public string? CustomText        { get; init; }
            public string? Resolution        { get; init; }
            public string? RecordResolution  { get; init; }
            public int?    RecordFps         { get; init; }
        }

        /// <summary>
        /// 按 configKey 索引的摄像头覆盖集合。
        /// 用 sealed 字典(只读)防止运行时被恶意修改。
        /// </summary>
        public sealed class CameraSettingsCollection
        {
            private readonly System.Collections.Generic.Dictionary<string, CameraSettings> _inner
                = new(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyDictionary<string, CameraSettings> Items => _inner;

            public CameraSettings? GetOrDefault(string key)
                => _inner.TryGetValue(key, out var v) ? v : null;

            public void Set(string key, CameraSettings settings)
            {
                if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));
                _inner[key] = settings;
            }
        }

        /// <summary>
        /// 返回当前实例的"等价默认" — 用于 reset。
        /// </summary>
        public static AppSettings CreateDefault() => new()
        {
            IsInitialized = true,
            LoadSource    = "Defaults",
            LoadedAt      = DateTime.UtcNow,
        };
    }
}