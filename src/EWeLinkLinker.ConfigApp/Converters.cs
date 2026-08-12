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

