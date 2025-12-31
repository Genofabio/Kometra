using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using KomaLab.Models;

namespace KomaLab.Converters;

public class AlignmentTargetNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AlignmentTarget target)
        {
            return target switch
            {
                AlignmentTarget.Comet => "Cometa",
                AlignmentTarget.Stars => "Campo Stellare",
                _ => target.ToString()
            };
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Non serve il convert back per una ComboBox in sola lettura
        return AvaloniaProperty.UnsetValue;
    }
}