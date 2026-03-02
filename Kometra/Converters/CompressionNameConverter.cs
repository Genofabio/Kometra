using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Kometra.Models.Export;

namespace Kometra.Converters;

public class CompressionNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is FitsCompressionMode mode)
        {
            return mode switch
            {
                FitsCompressionMode.None => "Nessuna",
                _ => mode.ToString()
            };
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}