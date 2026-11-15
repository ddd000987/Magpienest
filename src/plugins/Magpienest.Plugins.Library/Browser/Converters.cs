using System.Globalization;
using System.Windows.Data;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>Rating → star glyph for the star at position ConverterParameter (1..5): filled or outline.</summary>
internal sealed class StarGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var rating = value is int r ? r : 0;
        var position = int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
        return rating >= position ? "" : "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Negates a bool.</summary>
internal sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
