using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;
using Kometra.Infrastructure; // Aggiunto per accedere al LocalizationManager
using Kometra.Models.Processing.Alignment;

namespace Kometra.Converters;

public class AlignmentModeNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AlignmentMode mode)
        {
            // Usiamo l'indexer del LocalizationManager per recuperare la traduzione corrente
            return mode switch
            {
                AlignmentMode.Automatic => LocalizationManager.Instance["AlignModeAuto"],
                AlignmentMode.Guided    => LocalizationManager.Instance["AlignModeGuided"],
                AlignmentMode.Manual    => LocalizationManager.Instance["AlignModeManual"],
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