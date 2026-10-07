using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.ConfigApp;

public class BoolToStringConverter : System.Windows.Data.IValueConverter
{
    public string TrueValue { get; set; } = "是";
    public string FalseValue { get; set; } = "否";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue ? TrueValue : FalseValue;
        if (value is bool?)
            return FalseValue;
        return FalseValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 将在线状态转换为颜色画笔：true=绿色, false=灰色
/// </summary>
public class StatusToBrushConverter : IValueConverter
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // #4CAF50
    private static readonly Brush OfflineBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)); // #9E9E9E

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isOnline && isOnline)
            return SuccessBrush;
        return OfflineBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 布尔值反转转 Visibility（true=Collapsed, false=Visible）
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is not Visibility.Visible;
    }
}

/// <summary>
/// ComparisonOperator 转显示文本
/// </summary>
public class ComparisonToDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ComparisonOperator comparison)
        {
            return comparison switch
            {
                ComparisonOperator.Gte => "≥",
                ComparisonOperator.Gt => ">",
                ComparisonOperator.Lte => "≤",
                ComparisonOperator.Lt => "<",
                ComparisonOperator.Eq => "=",
                ComparisonOperator.Neq => "≠",
                ComparisonOperator.Range => "范围",
                ComparisonOperator.OutsideRange => "范围外",
                _ => comparison.ToString()
            };
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}


/// <summary>
/// 通道下拉的候选，按所选设备的通道数生成（老界面写死 CH0–CH3，而模型允许 1-8 通道，
/// 4 路以上的插排根本选不到 4-7）。
/// 已存的通道号比这台设备的通道数大时，也照样把它列进候选：否则 SelectedIndex 越界会被 WPF
/// 悄悄改成 -1 再回写进配置，用户看到的是一条莫名其妙变成"未选"的动作。越界本身由
/// MainWindow.SaveConfig 点名拦下。
/// </summary>
public class ChannelListConverter : IMultiValueConverter
{
    // 同一长度必须复用同一个 List 实例：ItemsSource 换了引用，WPF 会把 SelectedIndex 清成 -1
    // 并回写进模型，用户一点通道号就被"清空"。
    private static readonly Dictionary<int, List<string>> _cache = new();
    private static readonly object _cacheLock = new();

    private static List<string> ForCount(int count)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(count, out var list))
            {
                list = new List<string>(count);
                for (int i = 0; i < count; i++) list.Add($"CH{i}");
                _cache[count] = list;
            }
            return list;
        }
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var deviceId = values.Length > 0 ? values[0] as string : null;
        var outlet = values.Length > 1 && values[1] is int o ? o : 0;

        var count = 1;
        if (deviceId != null && values.Length > 2 && values[2] is System.Collections.IEnumerable devices)
        {
            foreach (var d in devices)
            {
                if (d is DeviceInfo di && di.DeviceId == deviceId)
                {
                    count = Math.Max(1, di.ChannelCount);
                    break;
                }
            }
        }

        return ForCount(Math.Max(count, outlet + 1));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
