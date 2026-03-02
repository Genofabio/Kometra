using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Kometra.Models.Fits.Health; // Assicurati che questo namespace sia corretto per il tuo Enum

namespace Kometra.Converters;

public class StatusToBrushConverter : IValueConverter
{
    // PROPRIETÀ PUBBLICHE: Saranno riempite dallo XAML con le tue risorse!
    public IBrush? SuccessBrush { get; set; }
    public IBrush? ErrorBrush { get; set; }
    public IBrush? PendingBrush { get; set; }
    public IBrush? WarningBrush { get; set; } // <-- Aggiungi questa proprietà

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is HeaderHealthStatus status)
        {
            return status switch
            {
                HeaderHealthStatus.Valid => SuccessBrush,
                HeaderHealthStatus.Invalid => ErrorBrush,
                HeaderHealthStatus.Warning => WarningBrush, // <-- Ora restituisce il Giallo
                _ => PendingBrush
            };
        }
        return PendingBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}