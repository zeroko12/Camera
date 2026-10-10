#nullable enable
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
        public const string  DefaultSaveDirectory         = "SavedVideos";
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
        public const bool    DefaultIsDarkTheme           = false;

        // ────────────────── 摄像头特定默认值 ──────────────────
        public const int     DefaultCameraRecordFps        = 10;
        public const string  DefaultCameraRecordResolution = "与采集分辨率一致";

        // ────────────────── 硬件与编码默认值 ──────────────────
        public const int     DefaultMaxNvencSessions       = 2;
        public const int     DefaultMaxAmfSessions         = 3;
        public const int     DefaultMaxQsvSessions         = 0;
        public const bool    DefaultUseV3Architecture      = true;
        public const bool    DefaultForceCpuEncoding       = false;

        // ────────────────── 运行时状态(不持久化)──────────────────
        public bool         IsInitialized   { get; set; }
        public string?      LoadSource      { get; set; }   // "JSON" / "App.config" / "Defaults"
        public DateTime?    LoadedAt        { get; set; }

        // ────────────────── 全局 UI 设置 ──────────────────
        public bool         IsSave              { get; set; } = DefaultIsSave;
        public double       Opacity             { get; set; } = DefaultOpacity;
        public string       Title               { get; set; } = DefaultTitle;
        public string       SystemTitle         { get; set; } = DefaultSystemTitle;
        public bool         OverwriteOldFiles   { get; set; } = DefaultOverwriteOldFiles;
        public int          DiskSpaceThreshold  { get; set; } = DefaultDiskSpaceThresholdMb;
        public string       SaveDirectory       { get; set; } = DefaultSaveDirectory;
        public int          Quality             { get; set; } = DefaultQuality;
        public int          SaveInterval        { get; set; } = DefaultSaveIntervalMin;
        public int          MaxPreviewWidth     { get; set; } = DefaultMaxPreviewWidth;
        public int          PreviewFps          { get; set; } = DefaultPreviewFps;
        public int          RecordFps           { get; set; } = DefaultRecordFps;
        public double       Contrast            { get; set; } = DefaultContrast;
        public double       Luminance           { get; set; } = DefaultLuminance;
        public bool         IsGrayHomogenization { get; set; }
        public double       OverlayTextSize     { get; set; } = DefaultOverlayTextSize;
        public int          MaxFps              { get; set; } = DefaultMaxFps;
        public bool         IsDarkTheme         { get; set; } = DefaultIsDarkTheme;

        // ────────────────── 主窗口几何 ──────────────────
        public double       WindowWidth         { get; set; } = DefaultWindowWidth;
        public double       WindowHeight        { get; set; } = DefaultWindowHeight;
        public double       WindowLeft          { get; set; } = DefaultWindowLeft;
        public double       WindowTop           { get; set; } = DefaultWindowTop;
        public double       MinWindowWidth      { get; set; } = DefaultMinWindowWidth;
        public double       MinWindowHeight     { get; set; } = DefaultMinWindowHeight;
        public double       AspectRatio         { get; set; } = DefaultAspectRatio;
        public bool         CanClickThrough     { get; set; }
        public bool         Topmost             { get; set; } = DefaultTopmost;
        public string       ExitBehavior        { get; set; } = DefaultExitBehavior;

        // ────────────────── 硬件与编码设置 ──────────────────
        public int          MaxNvencSessions    { get; set; } = DefaultMaxNvencSessions;
        public int          MaxAmfSessions      { get; set; } = DefaultMaxAmfSessions;
        public int          MaxQsvSessions      { get; set; } = DefaultMaxQsvSessions;
        public bool         UseV3Architecture   { get; set; } = DefaultUseV3Architecture;
        public bool         ForceCpuEncoding    { get; set; } = DefaultForceCpuEncoding;

        // ────────────────── 摄像头特定覆盖 ──────────────────
        // key = camera 的稳定配置 key(由 CameraService.GetConfigKey 生成)
        public Dictionary<string, CameraSettings> Cameras { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // 摄像头排列顺序(按 configKey 存储)
        public List<string> CameraOrder { get; set; } = new();

        /// <summary>
        /// 单个摄像头的覆盖配置。
        /// </summary>
        public sealed class CameraSettings
        {
            public string? CustomText        { get; set; }
            public string? Resolution        { get; set; }
            public string? RecordResolution  { get; set; }
            public int?    RecordFps         { get; set; }
            public bool?   IsManualDisabled  { get; set; }
            public bool?   IsFlipHorizontal  { get; set; }
            public bool?   IsFlipVertical    { get; set; }
            public string? StretchMode       { get; set; }
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