using System.Globalization;
using System.Windows.Data;
using LinuxOps.Services;

namespace LinuxOps.Converters;

public sealed class DistroIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => DistroIconSource.ForHint(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
