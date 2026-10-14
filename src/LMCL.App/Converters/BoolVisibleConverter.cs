using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LMCL.App.Converters;

/// <summary>
/// bool → Visibility。false 时给 <see cref="Visibility.Collapsed"/>（不占位）。
/// 结果行上的「已安装」小标、详情里的各种条件块都靠它。
/// </summary>
public sealed class BoolVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
