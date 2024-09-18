using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Diagnostics;

namespace camera
{
    public class MatToImageSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is Mat mat)
            {
                try
                {
                    if (mat.Empty())
                    {
                        Debug.WriteLine("Mat is empty");
                        return null;
                    }

                    //Debug.WriteLine($"Mat info: Type={mat.Type()}, Channels={mat.Channels()}, Size={mat.Size()}");

                    return BitmapSourceConverter.ToBitmapSource(mat);
                    // return mat.ToBitmapSource();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error converting Mat to BitmapSource: {ex.Message}");
                    return null;
                }
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}