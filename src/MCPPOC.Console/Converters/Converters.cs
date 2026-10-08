using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MCPPOC.Console.Models;
using MCPPOC.Console.Services;

namespace MCPPOC.Console.Converters;

public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ServerState.Running => new SolidColorBrush(Color.FromRgb(0x2e, 0xa0, 0x6a)),
        ServerState.Starting or ServerState.Stopping => new SolidColorBrush(Color.FromRgb(0xd9, 0xa2, 0x2e)),
        ServerState.Error => new SolidColorBrush(Color.FromRgb(0xba, 0x26, 0x23)),
        _ => new SolidColorBrush(Color.FromRgb(0x55, 0x58, 0x5e)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xfa, 0x42, 0x3e)),
        LogLevel.Warning => new SolidColorBrush(Color.FromRgb(0xd9, 0xa2, 0x2e)),
        LogLevel.Request => new SolidColorBrush(Color.FromRgb(0x6a, 0xb0, 0xf5)),
        LogLevel.Status => new SolidColorBrush(Color.FromRgb(0x9a, 0x7b, 0xf9)),
        LogLevel.Debug => new SolidColorBrush(Color.FromRgb(0x7a, 0x7d, 0x85)),
        _ => new SolidColorBrush(Colors.Gainsboro),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>True -> Wrap, False -> NoWrap. Used to bind a checkbox directly to TextWrapping.</summary>
public sealed class BoolToWrapConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is bool b && b) ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TextWrapping.Wrap;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;
}

public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value != null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
