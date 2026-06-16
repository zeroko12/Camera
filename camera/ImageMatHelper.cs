using OpenCvSharp;
using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace camera
{
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

        private static readonly ConcurrentDictionary<Type, Func<DependencyObject, ImageSource>> _getters = new();
        private static readonly ConcurrentDictionary<Type, Action<DependencyObject, ImageSource>> _setters = new();

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
                
                if (!_getters.TryGetValue(type, out var getter))
                {
                    var prop = type.GetProperty("Source");
                    if (prop != null && prop.CanRead)
                    {
                        var targetExp = System.Linq.Expressions.Expression.Parameter(typeof(DependencyObject), "target");
                        var castTarget = System.Linq.Expressions.Expression.Convert(targetExp, type);
                        var getProp = System.Linq.Expressions.Expression.Property(castTarget, prop);
                        var castResult = System.Linq.Expressions.Expression.Convert(getProp, typeof(ImageSource));
                        getter = System.Linq.Expressions.Expression.Lambda<Func<DependencyObject, ImageSource>>(
                            castResult, targetExp
                        ).Compile();
                        _getters[type] = getter;
                    }
                }

                if (!_setters.TryGetValue(type, out var setter))
                {
                    var prop = type.GetProperty("Source");
                    if (prop != null && prop.CanWrite)
                    {
                        var targetExp = System.Linq.Expressions.Expression.Parameter(typeof(DependencyObject), "target");
                        var valueExp = System.Linq.Expressions.Expression.Parameter(typeof(ImageSource), "value");
                        var castTarget = System.Linq.Expressions.Expression.Convert(targetExp, type);
                        var castValue = System.Linq.Expressions.Expression.Convert(valueExp, prop.PropertyType);
                        var assign = System.Linq.Expressions.Expression.Assign(
                            System.Linq.Expressions.Expression.Property(castTarget, prop),
                            castValue
                        );
                        setter = System.Linq.Expressions.Expression.Lambda<Action<DependencyObject, ImageSource>>(
                            assign, targetExp, valueExp
                        ).Compile();
                        _setters[type] = setter;
                    }
                }

                if (getter == null || setter == null) return;

                var wb = getter(d) as WriteableBitmap;
                
                var format = mat.Channels() switch
                {
                    1 => PixelFormats.Gray8,
                    3 => PixelFormats.Bgr24,
                    4 => PixelFormats.Bgra32,
                    _ => PixelFormats.Bgr24
                };

                if (wb == null || wb.PixelWidth != mat.Width || wb.PixelHeight != mat.Height || wb.Format != format)
                {
                    wb = new WriteableBitmap(
                        mat.Width,
                        mat.Height,
                        96,
                        96,
                        format,
                        null);
                    setter(d, wb);
                }

                OpenCvSharp.WpfExtensions.WriteableBitmapConverter.ToWriteableBitmap(mat, wb);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[ImageMatHelper] 更新 WriteableBitmap 失败", ex);
            }
        }

        private static void SetSourceProperty(DependencyObject d, ImageSource value)
        {
            var type = d.GetType();
            if (!_setters.TryGetValue(type, out var setter))
            {
                var prop = type.GetProperty("Source");
                if (prop != null && prop.CanWrite)
                {
                    var targetExp = System.Linq.Expressions.Expression.Parameter(typeof(DependencyObject), "target");
                    var valueExp = System.Linq.Expressions.Expression.Parameter(typeof(ImageSource), "value");
                    var castTarget = System.Linq.Expressions.Expression.Convert(targetExp, type);
                    var castValue = System.Linq.Expressions.Expression.Convert(valueExp, prop.PropertyType);
                    var assign = System.Linq.Expressions.Expression.Assign(
                        System.Linq.Expressions.Expression.Property(castTarget, prop),
                        castValue
                    );
                    setter = System.Linq.Expressions.Expression.Lambda<Action<DependencyObject, ImageSource>>(
                        assign, targetExp, valueExp
                    ).Compile();
                    _setters[type] = setter;
                }
            }

            setter?.Invoke(d, value);
        }
    }
}
