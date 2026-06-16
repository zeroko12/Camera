using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Threading;

namespace camera.Pipeline.Processors
{
    /// <summary>
    /// 时间戳与自定义文字水印叠加处理器。
    ///
    /// ══════════════════ 性能优化策略 ══════════════════
    ///
    /// 【优化 1】ROI 小区域 Mat（最大收益）
    ///   原方案每次叠加都对整帧（3840×2160 ≈ 24MB）做 Cv2.Add，
    ///   99% 都是无意义的「0+原图」运算。
    ///   新方案：只为文字包围盒（通常 ~400×50px，< 60KB）创建小 Mat，
    ///   Cv2.Add 只作用于 frame 的 ROI 子矩阵，数据量减少 >99%。
    ///
    /// 【优化 2】Lock-free 时间戳更新
    ///   原方案每帧都要获取 _timeLock。
    ///   新方案：用 volatile string（CLR 引用赋值原子性）+ Interlocked 比较秒边界 Ticks，
    ///   完全无锁，竞争重复格式化的结果相同，安全无害。
    ///
    /// 【优化 3】静态字体名称缓存（进程级，只解析一次）
    ///   原方案每次 RegenerateOverlay 都遍历字体列表。
    ///   新方案：s_resolvedFontName 静态字段，双检锁单次初始化，
    ///   后续只用字体名直接 new Font(...)。
    ///
    /// 【优化 4】BitmapToMat 单块复制快速路径
    ///   当 Bitmap stride == width×3 且 Mat step == width×3（无填充，小 Mat 常见）时，
    ///   一次 MemoryCopy 替换逐行循环。
    ///
    /// 【中文支持】
    ///   OpenCV Hershey 字体 ASCII-only，改用 GDI+（System.Drawing）渲染，
    ///   支持完整 Unicode / 中文字符。
    /// </summary>
    public class TimestampOverlayProcessor : IFrameProcessor, IDisposable
    {
        public string Name => "TimestampOverlay";
        public bool Enabled { get; set; } = true;
        public int Order { get; set; } = 100;

        // ═══ Lock-free 时间戳缓存 ═══
        private volatile string _cachedTimeString = "";
        private long _lastTimeStringTicks;          // 上次格式化时的「秒边界」Ticks

        // ═══ 字体参数 ═══
        public double FontScale { get; set; } = 0.3;

        private string _customText = "";
        public string CustomText
        {
            get => _customText;
            set
            {
                var val = value ?? "";
                if (val.Length > 30) // 限制最大 30 个字符
                {
                    val = val.Substring(0, 30);
                }
                if (_customText != val)
                {
                    _customText = val;
                    lock (_overlayLock) { _lastRenderedCustomText = null; }
                }
            }
        }

        // ═══ ROI 小区域缓存 ═══
        private struct OverlayRegion
        {
            public Mat SmallMat;    // 仅包含文字包围盒的小 Mat（黑底白字）
            public Rect FrameRect;  // 在完整帧中的位置
        }
        private OverlayRegion[] _regions = Array.Empty<OverlayRegion>();
        private string _lastRenderedTimeStr;
        private string _lastRenderedCustomText;
        private double _lastRenderedFontScale = -1;
        private int _lastRenderedWidth = -1;
        private int _lastRenderedHeight = -1;
        private readonly object _overlayLock = new();

        // ═══ 静态字体名称缓存（进程内只解析一次，双检锁）═══
        private static volatile string s_resolvedFontName;
        private static readonly object s_fontNameLock = new();

        // ────────────────────────────────────────────────
        //  热路径：每帧调用
        // ────────────────────────────────────────────────
        public void Process(Mat frame, DateTime timestamp)
        {
            if (!Enabled || frame == null || frame.Empty()) return;
            if (frame.Type() != MatType.CV_8UC3) return;

            // ─── 第一步: Lock-free 时间戳更新（无 lock，无字符串分配，>99% 情况）───
            string timeStr = GetOrUpdateTimeString(timestamp);

            // ─── 第二步: 检查缓存，必要时重新生成 ROI 小 Mat ───
            lock (_overlayLock)
            {
                bool needRegen =
                    _regions.Length == 0 ||
                    frame.Width  != _lastRenderedWidth  ||
                    frame.Height != _lastRenderedHeight  ||
                    timeStr      != _lastRenderedTimeStr  ||
                    CustomText   != _lastRenderedCustomText ||
                    Math.Abs(FontScale - _lastRenderedFontScale) > 0.001;

                if (needRegen)
                {
                    RegenerateRegions(frame.Width, frame.Height, timeStr);
                    _lastRenderedTimeStr    = timeStr;
                    _lastRenderedCustomText = CustomText;
                    _lastRenderedFontScale  = FontScale;
                    _lastRenderedWidth      = frame.Width;
                    _lastRenderedHeight     = frame.Height;
                }

                // ─── 第三步: 仅对文字包围盒 ROI 做 SIMD Add（数据量 < 1/100 帧）───
                // frame[rect] 返回共享底层像素的子矩阵，写入直接作用于 frame 原始数据
                foreach (ref var region in _regions.AsSpan())
                {
                    using var roi = frame[region.FrameRect];
                    Cv2.Add(roi, region.SmallMat, roi);
                }
            }
        }

        // ────────────────────────────────────────────────
        //  Lock-free 时间字符串更新
        // ────────────────────────────────────────────────

        /// <summary>
        /// 利用 volatile string 引用赋值的 CLR 原子性消除锁。
        /// 竞争写入（极偶发）的结果相同，无害。
        /// </summary>
        private string GetOrUpdateTimeString(DateTime timestamp)
        {
            // 截断到秒边界，方便与上次 Ticks 比较
            long secondBoundary = (timestamp.Ticks / TimeSpan.TicksPerSecond) * TimeSpan.TicksPerSecond;
            if (secondBoundary != Interlocked.Read(ref _lastTimeStringTicks))
            {
                _cachedTimeString = timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                Interlocked.Exchange(ref _lastTimeStringTicks, secondBoundary);
            }
            return _cachedTimeString;
        }

        // ────────────────────────────────────────────────
        //  冷路径：仅在文字/字号/分辨率变化时调用（每秒≤1次）
        // ────────────────────────────────────────────────

        /// <summary>
        /// 重新生成 ROI 小区域 Mat 列表。
        /// 总内存占用 ≈ 文字包围盒面积 × 3 字节，通常远小于 1% 帧大小。
        /// </summary>
        private void RegenerateRegions(int frameW, int frameH, string timeStr)
        {
            // 释放旧区域
            foreach (var r in _regions)
                r.SmallMat?.Dispose();

            float fontSize = (float)Math.Max(10, FontScale * 60.0 * (frameW / 1920.0));
            int   margin   = Math.Max(8, (int)(15 * (frameW / 1920.0)));

            // 字体名称只解析一次（静态缓存），后续直接 new Font
            string fontName = ResolveChineseFontName();
            using var font = new System.Drawing.Font(
                fontName, fontSize,
                System.Drawing.FontStyle.Regular,
                System.Drawing.GraphicsUnit.Pixel);

            // 用 1×1 临时 Graphics 测量文字尺寸（无需真实画布）
            System.Drawing.SizeF timeSize   = default;
            System.Drawing.SizeF customSize = default;
            using (var dummy = new System.Drawing.Bitmap(1, 1))
            using (var gm   = System.Drawing.Graphics.FromImage(dummy))
            {
                if (!string.IsNullOrEmpty(timeStr))
                    timeSize = gm.MeasureString(timeStr, font);
                if (!string.IsNullOrEmpty(CustomText))
                    customSize = gm.MeasureString(CustomText, font);
            }

            var list = new List<OverlayRegion>(2);

            // ─── 时间戳（右上角）───
            if (timeSize.Width > 0 && timeSize.Height > 0)
            {
                // +2 为阴影留 1px 偏移量
                int tw = Math.Min((int)Math.Ceiling(timeSize.Width)  + 2, frameW);
                int th = Math.Min((int)Math.Ceiling(timeSize.Height) + 2, frameH);
                int rx = Math.Max(0, frameW - tw - margin);
                int ry = Math.Max(0, margin);

                list.Add(new OverlayRegion
                {
                    SmallMat  = RenderTextToSmallMat(timeStr, font, tw, th),
                    FrameRect = new Rect(rx, ry, tw, th)
                });
            }

            // ─── 自定义文字（左上角）───
            if (customSize.Width > 0 && customSize.Height > 0)
            {
                // 限制最大渲染宽度为画幅宽度的 40%
                int maxCw = (int)(frameW * 0.40f);
                int cw = Math.Min((int)Math.Ceiling(customSize.Width)  + 2, maxCw);
                int ch = Math.Min((int)Math.Ceiling(customSize.Height) + 2, frameH);
                int rx = Math.Max(0, margin);
                int ry = Math.Max(0, margin);

                list.Add(new OverlayRegion
                {
                    SmallMat  = RenderTextToSmallMat(CustomText, font, cw, ch, cw < customSize.Width),
                    FrameRect = new Rect(rx, ry, cw, ch)
                });
            }

            _regions = list.ToArray();
        }

        /// <summary>
        /// 将单行文字渲染到仅包含文字包围盒大小的新 Mat（黑底白字 + 1px 黑色阴影）。
        /// </summary>
        private static Mat RenderTextToSmallMat(string text, System.Drawing.Font font, int w, int h, bool forceEllipsis = false)
        {
            using var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var g      = System.Drawing.Graphics.FromImage(bitmap);

            g.Clear(System.Drawing.Color.Black);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var shadowBrush = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
            using var textBrush   = new System.Drawing.SolidBrush(System.Drawing.Color.White);

            if (forceEllipsis)
            {
                using var format = new System.Drawing.StringFormat
                {
                    Trimming = System.Drawing.StringTrimming.EllipsisCharacter,
                    FormatFlags = System.Drawing.StringFormatFlags.NoWrap
                };
                var rect = new System.Drawing.RectangleF(0, 0, w, h);
                var shadowRect = new System.Drawing.RectangleF(1f, 1f, w, h);
                g.DrawString(text, font, shadowBrush, shadowRect, format);
                g.DrawString(text, font, textBrush, rect, format);
            }
            else
            {
                // 先画阴影（偏移 1px，增强可读性）
                g.DrawString(text, font, shadowBrush, 1f, 1f);
                // 再画主文字
                g.DrawString(text, font, textBrush,   0f, 0f);
            }

            return BitmapToNewMat(bitmap, w, h);
        }

        /// <summary>
        /// 解析首个可用中文字体名（静态双检锁，进程内只执行一次）。
        /// </summary>
        private static string ResolveChineseFontName()
        {
            if (s_resolvedFontName != null) return s_resolvedFontName;
            lock (s_fontNameLock)
            {
                if (s_resolvedFontName != null) return s_resolvedFontName;

                string[] candidates =
                [
                    "Microsoft YaHei", "SimHei", "SimSun",
                    "NSimSun", "Arial Unicode MS", "Arial"
                ];
                foreach (var name in candidates)
                {
                    try
                    {
                        using var f = new System.Drawing.Font(
                            name, 12f,
                            System.Drawing.FontStyle.Regular,
                            System.Drawing.GraphicsUnit.Pixel);
                        if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            s_resolvedFontName = name;
                            return s_resolvedFontName;
                        }
                    }
                    catch { /* 继续下一个候选 */ }
                }
                s_resolvedFontName = System.Drawing.FontFamily.GenericSansSerif.Name;
                return s_resolvedFontName;
            }
        }

        /// <summary>
        /// 将 Format24bppRgb Bitmap 转换为新的 CV_8UC3 Mat（unsafe 指针直接复制）。
        /// GDI+ 存储顺序为 BGR，与 OpenCV 一致，无需通道重排。
        /// 当 stride 与 step 均等于 width×3（小 Mat 常见，无行填充）时，
        /// 退化为单次整块 MemoryCopy，避免循环开销。
        /// </summary>
        private static unsafe Mat BitmapToNewMat(System.Drawing.Bitmap bitmap, int w, int h)
        {
            var mat  = new Mat(h, w, MatType.CV_8UC3, Scalar.All(0));
            var rect = new System.Drawing.Rectangle(0, 0, w, h);
            var bmpData = bitmap.LockBits(
                rect,
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                int  bmpStride = bmpData.Stride;
                int  matStep   = (int)mat.Step();
                int  rowBytes  = w * 3;
                byte* src      = (byte*)bmpData.Scan0.ToPointer();
                byte* dst      = (byte*)mat.DataPointer;

                if (bmpStride == rowBytes && matStep == rowBytes)
                {
                    // 快速路径：无填充字节，整块一次复制
                    Buffer.MemoryCopy(src, dst, (long)h * rowBytes, (long)h * rowBytes);
                }
                else
                {
                    // 通用路径：逐行复制（处理 stride/step 不对齐的情况）
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy(src + y * bmpStride, dst + y * matStep, matStep, rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
            return mat;
        }

        public void Dispose()
        {
            lock (_overlayLock)
            {
                foreach (var r in _regions)
                    r.SmallMat?.Dispose();
                _regions = Array.Empty<OverlayRegion>();
            }
        }
    }
}
