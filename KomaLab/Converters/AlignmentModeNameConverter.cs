using Avalonia; // <--- Aggiungi questo per AvaloniaProperty
using Avalonia.Data.Converters;
using System;
using System.Globalization;
using KomaLab.Models; 

namespace KomaLab.Converters;

public class AlignmentModeNameConverter : IValueConverter
{
    // Da Enum a Stringa (per la UI)
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AlignmentMode mode)
        {
            return mode switch
            {
                AlignmentMode.Automatic => "Automatica",
                AlignmentMode.Guided    => "Guidata",
                AlignmentMode.Manual    => "Manuale",
                AlignmentMode.Stars     => "Stelle",
                _ => mode.ToString()
            };
        }
        return value;
    }

    // Da Stringa a Enum (dalla UI al ViewModel)
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            return str switch
            {
                "Automatica" => AlignmentMode.Automatic,
                "Guidata"    => AlignmentMode.Guided,
                "Manuale"    => AlignmentMode.Manual,
                "Stelle"     => AlignmentMode.Stars,
                _ => AvaloniaProperty.UnsetValue 
            };
        }
        return AvaloniaProperty.UnsetValue;
    }
}