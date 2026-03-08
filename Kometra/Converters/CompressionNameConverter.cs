using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Kometra.Infrastructure; // Aggiunto per l'accesso al LocalizationManager
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
                // Recuperiamo la traduzione tramite il LocalizationManager
                FitsCompressionMode.None => LocalizationManager.Instance["ExportCompressionNone"],
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