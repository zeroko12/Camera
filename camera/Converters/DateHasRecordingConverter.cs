using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace camera.Converters
{
    /// <summary>
    /// 日历日期录像状态转换器:
    /// 用于在回放日历中根据当前日期是否存在录像文件，动态设置圆点标记可见性、文字加粗与 ToolTip 提示。
    /// </summary>
    public class DateHasRecordingConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values != null && values.Length >= 2)
            {
                DateTime date;
                if (values[0] is DateTime dt)
                {
                    date = dt;
                }
                else if (values[0] is string str && DateTime.TryParse(str, out var parsed))
                {
                    date = parsed;
                }
                else
                {
                    if (parameter as string == "Visibility") return Visibility.Hidden;
                    if (parameter as string == "FontWeight") return FontWeights.Normal;
                    return DependencyProperty.UnsetValue;
                }

                bool hasRecording = false;
                if (values[1] is HashSet<DateTime> dateSet)
                {
                    hasRecording = dateSet.Contains(date.Date);
                }
                else if (values[1] is IEnumerable<DateTime> dateList)
                {
                    foreach (var d in dateList)
                    {
                        if (d.Date == date.Date)
                        {
                            hasRecording = true;
                            break;
                        }
                    }
                }

                string param = parameter as string;
                if (param == "Visibility")
                {
                    // 录像指示圆点可见性: 有录像显示，无录像隐藏（保留占位防止高度抖动）
                    return hasRecording ? Visibility.Visible : Visibility.Hidden;
                }
                if (param == "FontWeight")
                {
                    // 有录像日期文字加粗
                    return hasRecording ? FontWeights.Bold : FontWeights.Normal;
                }
                if (param == "ToolTip")
                {
                    return hasRecording
                        ? $"{date:yyyy-MM-dd} (有录像)"
                        : $"{date:yyyy-MM-dd} (无录像)";
                }

                return hasRecording;
            }

            if (parameter as string == "Visibility") return Visibility.Hidden;
            if (parameter as string == "FontWeight") return FontWeights.Normal;
            return DependencyProperty.UnsetValue;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
