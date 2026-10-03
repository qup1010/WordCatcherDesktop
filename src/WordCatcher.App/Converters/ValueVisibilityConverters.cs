using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using WordCatcher.Core.Enums;

namespace WordCatcher.App.Converters;

public sealed class SyncStatusLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SyncStatus.Pending => "等待同步", SyncStatus.Syncing => "同步中", SyncStatus.Synced => "已同步",
        SyncStatus.Retryable => "等待重试", SyncStatus.Failed => "同步失败", _ => "未知状态"
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

public sealed class LocalDateTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTimeOffset date ? date.ToLocalTime().ToString(parameter as string ?? "MM-dd HH:mm", culture) : "—";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(value as string)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool boolean && !boolean;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
