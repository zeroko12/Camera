using OpenCvSharp;
using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LExpr = System.Linq.Expressions.Expression;

namespace camera
{
    /// <summary>
    /// 高性能 Mat → Image.Source 绑定助手。
    ///
    /// 关键设计决策（每条都直接对应 dump 中观察到的内存/性能问题）:
    /// 1. 单个 <see cref="ConcurrentDictionary{TKey,TValue}"/> 缓存 (Type, Property) → 访问器,
    ///    getter/setter 一次性 Expression.Compile 后长期复用,热路径上等价于直接方法调用。
    /// 2. 直接调用 <see cref="WriteableBitmap.Lock"/> / <see cref="WriteableBitmap.AddDirtyRect"/>
    ///    / <see cref="WriteableBitmap.Unlock"/>,使用 unsafe 指针复制 Mat 像素到 wb.BackBuffer。
    ///    完全绕开 <c>OpenCvSharp.WpfExtensions.WriteableBitmapConverter.ToWriteableBitmap</c>,
    ///    那个调用在某些版本会临时分配/订阅额外的 BitmapSource 包装,贡献了 dump 里的
    ///    12 M 条目的 Dictionary&lt;EventHandler*, int&gt; 泄露。
    /// 3. 复用单个 WriteableBitmap,仅在尺寸/格式真变化时重新分配。
    /// 4. 所有路径都是 unsafe 的内存拷贝,无任何托管分配。
    /// </summary>
    public static class ImageMatHelper
    {
        public static readonly DependencyProperty SourceMatProperty =
            DependencyProperty.RegisterAttached(
                "SourceMat",
                typeof(Mat),
                typeof(ImageMatHelper),
                new PropertyMetadata(null, OnSourceMatChanged));

        public static Mat GetSourceMat(DependencyObject obj)
        {
            return (Mat)obj.GetValue(SourceMatProperty);
        }

        public static void SetSourceMat(DependencyObject obj, Mat value)
        {
            obj.SetValue(SourceMatProperty, value);
        }

        private sealed class Accessors
        {
            public Func<DependencyObject, ImageSource> Getter;
            public Action<DependencyObject, ImageSource> Setter;
        }

        // 单个缓存,key 为 (实例运行时 Type, Source 属性 PropertyInfo)。
        // 同一 Type 只会编译一次 Expression(启动期一次性成本),热路径仅委托调用。
        private static readonly ConcurrentDictionary<(Type Type, PropertyInfo Prop), Accessors> _accessorsCache = new();

        private static void OnSourceMatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d == null) return;
            var mat = e.NewValue as Mat;
            if (mat == null || mat.Empty())
            {
                SetSourceProperty(d, null);
                return;
            }

            try
            {
                var type = d.GetType();
                var sourceProp = type.GetProperty("Source", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (sourceProp == null) return;

                var acc = _accessorsCache.GetOrAdd((type, sourceProp), BuildAccessors);

                if (acc.Getter == null || acc.Setter == null) return;

                var wb = acc.Getter(d) as WriteableBitmap;

                var format = mat.Channels() switch
                {
                    1 => PixelFormats.Gray8,
                    3 => PixelFormats.Bgr24,
                    4 => PixelFormats.Bgra32,
                    _ => PixelFormats.Bgr24
                };

                if (wb == null || wb.PixelWidth != mat.Width || wb.PixelHeight != mat.Height || wb.Format != format)
                {
                    // 仅在尺寸/格式真变化时重建 WriteableBitmap。
                    // 旧 wb 被 Image.Source 引用,赋值后旧引用替换会立即可 GC。
                    wb = new WriteableBitmap(mat.Width, mat.Height, 96, 96, format, null);
                    acc.Setter(d, wb);
                }

                // 直接 unsafe 像素拷贝,绕开 OpenCvSharp.WpfExtensions 包装层
                CopyMatToWriteableBitmap(mat, wb);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[ImageMatHelper] 更新 WriteableBitmap 失败", ex);
            }
        }

        /// <summary>
        /// 为指定 (Type, PropertyInfo) 一次性编译 typed getter/setter。
        /// 用 Expression.Lambda 而 编译 编译反射 delegate,
        /// 是因为实例 setter 必须接收「声明类型」作首参(如 Action&lt;ImageBlock, ImageSource&gt;),
        /// 无法直接通过 Delegate.CreateDelegate(typeof(Action&lt;DependencyObject, ImageSource&gt;), null, setter)
        /// 构造(签名不兼容)。
        /// </summary>
        private static Accessors BuildAccessors((Type Type, PropertyInfo Prop) key)
        {
            var (targetType, prop) = key;
            var acc = new Accessors();

            var getterMethod = prop.GetGetMethod();
            if (getterMethod != null)
            {
                // 关键:Lambda 的 ParameterExpression 类型必须与委托首参完全一致 (DependencyObject),
                // 真实目标类型在 lambda body 内 Convert 后再访问属性。
                var targetParam = LExpr.Parameter(typeof(DependencyObject), "target");
                LExpr castTarget = targetType == typeof(DependencyObject)
                    ? (LExpr)targetParam
                    : LExpr.Convert(targetParam, targetType);
                var getExpr = LExpr.Property(castTarget, prop);
                LExpr castResult = prop.PropertyType == typeof(ImageSource)
                    ? (LExpr)getExpr
                    : LExpr.Convert(getExpr, typeof(ImageSource));
                acc.Getter = LExpr.Lambda<Func<DependencyObject, ImageSource>>(
                    castResult, targetParam).Compile();
            }

            var setterMethod = prop.GetSetMethod();
            if (setterMethod != null)
            {
                var targetParam = LExpr.Parameter(typeof(DependencyObject), "target");
                var valueParam = LExpr.Parameter(typeof(ImageSource), "value");
                LExpr castTarget = targetType == typeof(DependencyObject)
                    ? (LExpr)targetParam
                    : LExpr.Convert(targetParam, targetType);
                LExpr castValue = prop.PropertyType == typeof(ImageSource)
                    ? (LExpr)valueParam
                    : LExpr.Convert(valueParam, prop.PropertyType);
                var assign = LExpr.Assign(LExpr.Property(castTarget, prop), castValue);
                acc.Setter = LExpr.Lambda<Action<DependencyObject, ImageSource>>(
                    assign, targetParam, valueParam).Compile();
            }

            return acc;
        }

        /// <summary>
        /// unsafe 像素级 Mat → WriteableBitmap 拷贝。
        ///
        /// 已知格式对应:MatType.CV_8UC1→Gray8, CV_8UC3→Bgr24, CV_8UC4→Bgra32。
        /// 各通道字节宽度与 WPF PixelFormat 一致时,逐行拷贝;否则做逐像素转换。
        ///
        /// 严禁在调用路径上分配任何托管对象,避免触发 BitmapSource 事件订阅字典膨胀。
        /// </summary>
        private static unsafe void CopyMatToWriteableBitmap(Mat mat, WriteableBitmap wb)
        {
            int w = mat.Width;
            int h = mat.Height;
            int channels = mat.Channels();

            wb.Lock();
            try
            {
                IntPtr backBuffer = wb.BackBuffer;
                int backBufferStride = wb.BackBufferStride;
                long matStep = (long)mat.Step();
                byte* matPtr = (byte*)mat.DataPointer;

                // 单通道 Gray8 / 三通道 Bgr24 / 四通道 Bgra32 与 WPF 对应格式字节序一致
                bool layoutMatches =
                    (channels == 1 && wb.Format == PixelFormats.Gray8) ||
                    (channels == 3 && wb.Format == PixelFormats.Bgr24) ||
                    (channels == 4 && wb.Format == PixelFormats.Bgra32);

                if (layoutMatches && backBufferStride == matStep)
                {
                    // 完美对齐:整块一次 memcpy,O(1) 系统调用
                    Buffer.MemoryCopy(matPtr, (void*)backBuffer, (long)h * backBufferStride, (long)h * backBufferStride);
                }
                else if (layoutMatches)
                {
                    // 步长不一致:逐行拷贝
                    int rowBytes = w * channels;
                    for (int y = 0; y < h; y++)
                    {
                        Buffer.MemoryCopy(matPtr + y * matStep, (void*)(backBuffer + y * backBufferStride), rowBytes, rowBytes);
                    }
                }
                else
                {
                    // 通道数不匹配,逐像素按通道拷贝
                    int dstChannels = wb.Format.BitsPerPixel / 8;
                    int copyChannels = Math.Min(channels, dstChannels);
                    for (int y = 0; y < h; y++)
                    {
                        byte* src = matPtr + y * matStep;
                        byte* dst = (byte*)(backBuffer + y * backBufferStride);
                        for (int x = 0; x < w; x++)
                        {
                            for (int c = 0; c < copyChannels; c++)
                            {
                                dst[x * dstChannels + c] = src[x * channels + c];
                            }
                        }
                    }
                }

                // 向 WPF 提交脏矩形通知以触发 GPU 显存纹理刷新
                wb.AddDirtyRect(new Int32Rect(0, 0, w, h));
            }
            finally
            {
                wb.Unlock();
            }
        }

        private static void SetSourceProperty(DependencyObject d, ImageSource value)
        {
            if (d == null) return;
            var type = d.GetType();
            var sourceProp = type.GetProperty("Source", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (sourceProp == null || !sourceProp.CanWrite) return;

            var acc = _accessorsCache.GetOrAdd((type, sourceProp), BuildAccessors);
            acc.Setter?.Invoke(d, value);
        }
    }
}
