using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.ConfigApp;

public class StateToIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string state)
        {
            // Treat null, empty, or "on" as "on" (index 0); everything else as "off" (index 1)
            return string.IsNullOrEmpty(state) || state.Equals("on", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }
        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int index)
        {
            return index == 0 ? "on" : "off";
        }
        return "on";
    }
}

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

public class ChannelToIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int channelCount && channelCount >= 1 && channelCount <= 5)
            return channelCount - 1;
        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int index)
            return index + 1;
        return 1;
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
/// Convert power-state string to Chinese display: on=开, off=关
/// </summary>
public class StateToOnOffConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && s.Equals("on", StringComparison.OrdinalIgnoreCase))
            return "开";
        return "关";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && s == "开")
            return "on";
        return "off";
    }
}

/// <summary>
/// Convert service status text to a color brush: RUNNING=green, STOPPED=red, else gray
/// </summary>
public class ServiceStatusToBrushConverter : IValueConverter
{
    private static readonly Brush RunningBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush StoppedBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
    private static readonly Brush UnknownBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value?.ToString()?.ToUpperInvariant() ?? "";
        if (s.Contains("RUNNING")) return RunningBrush;
        if (s.Contains("STOPPED")) return StoppedBrush;
        return UnknownBrush;
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

