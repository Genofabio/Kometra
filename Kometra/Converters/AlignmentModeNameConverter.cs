using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;
using Kometra.Models;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Alignment;

namespace Kometra.Converters;

public class AlignmentModeNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AlignmentMode mode)
        {
            return mode switch
            {
                AlignmentMode.Automatic => "Automatica",
                AlignmentMode.Guided    => "Guidata",
                AlignmentMode.Manual    => "Manuale",
                _ => mode.ToString()
            };
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return AvaloniaProperty.UnsetValue;
    }
}