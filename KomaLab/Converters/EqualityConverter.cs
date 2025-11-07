using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace KomaLab.Converters;

/// <summary>
/// Converte un oggetto in 'true' se è uguale al parametro (ConverterParameter).
/// Usato per bindare la visibilità in base a un valore Enum.
/// </summary>
public class EqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Converte sia il valore che il parametro in stringa per un confronto sicuro
        string? currentValue = value?.ToString();
        string? parameterValue = parameter?.ToString();
        
        return string.Equals(currentValue, parameterValue, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Non necessario per questo scenario (binding OneWay)
        throw new NotImplementedException();
    }
}