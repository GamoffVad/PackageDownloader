using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace PackageDownloader;

/// <summary>
/// Схлопывает любые пробелы и переносы строк в один пробел — чтобы описания
/// пакетов отображались одной строкой и высота строк таблицы была одинаковой.
/// </summary>
public class SingleLineConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? Regex.Replace(s, @"\s+", " ").Trim() : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
