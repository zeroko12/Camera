using OpenCvSharp;
using System;
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

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo> _propertyCache = new();

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
                if (!_propertyCache.TryGetValue(type, out var sourceProp))
                {
                    sourceProp = type.GetProperty("Source");
                    if (sourceProp != null && sourceProp.CanWrite)
                    {
                        _propertyCache[type] = sourceProp;
                    }
                }

                if (sourceProp == null) return;

                var wb = sourceProp.GetValue(d) as WriteableBitmap;
                
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
                    sourceProp.SetValue(d, wb);
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
            if (!_propertyCache.TryGetValue(type, out var sourceProp))
            {
                sourceProp = type.GetProperty("Source");
                if (sourceProp != null && sourceProp.CanWrite)
                {
                    _propertyCache[type] = sourceProp;
                }
            }

            if (sourceProp != null)
            {
                sourceProp.SetValue(d, value);
            }
        }
    }
}
