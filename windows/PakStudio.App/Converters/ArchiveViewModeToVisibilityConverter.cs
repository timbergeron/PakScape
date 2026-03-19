using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PakStudio.Core.Documents;

namespace PakStudio.App.Converters;

public sealed class ArchiveViewModeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ArchiveViewMode mode || parameter is not string rawMode)
        {
            return Visibility.Collapsed;
        }

        if (!Enum.TryParse<ArchiveViewMode>(rawMode, ignoreCase: true, out var expected))
        {
            return Visibility.Collapsed;
        }

        return mode == expected ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
