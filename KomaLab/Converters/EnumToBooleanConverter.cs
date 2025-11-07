using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace KomaLab.Converters;

/// <summary>
/// Converte un valore Enum in un bool (true) se corrisponde al parametro.
/// Usato per collegare i RadioButton a una singola proprietà Enum nel ViewModel.
/// </summary>
public class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;

        string enumValue = value.ToString() ?? "";
        string targetValue = parameter.ToString() ?? "";
        
        return enumValue.Equals(targetValue, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is false)
            return Avalonia.Data.BindingOperations.DoNothing; // Non tornare indietro se deselezionato

        if (parameter == null)
            return Avalonia.Data.BindingOperations.DoNothing;

        // Converte la stringa del parametro nell'enum
        return Enum.Parse(targetType, parameter.ToString() ?? "", true);
    }
}