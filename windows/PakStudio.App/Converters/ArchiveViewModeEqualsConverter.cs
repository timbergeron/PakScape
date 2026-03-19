using System.Globalization;
using System.Windows.Data;
using PakStudio.Core.Documents;

namespace PakStudio.App.Converters;

public sealed class ArchiveViewModeEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ArchiveViewMode mode || parameter is not string rawMode)
        {
            return false;
        }

        return Enum.TryParse<ArchiveViewMode>(rawMode, ignoreCase: true, out var expected) && mode == expected;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
