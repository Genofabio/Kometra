using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons; // Assicurati di avere questo using

namespace Kometra.Converters;

// Converte: bool (IsRunning) -> MaterialIconKind (Icona)
public class AnimationIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isRunning && isRunning)
        {
            return MaterialIconKind.Pause; // Se gira, mostra Pausa
        }
        return MaterialIconKind.Play;      // Se fermo, mostra Play
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) 
        => throw new NotImplementedException();
}

// Converte: bool (IsRunning) -> String (Testo)
public class AnimationTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isRunning && isRunning)
        {
            return "Ferma Animazione";
        }
        return "Avvia Animazione";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) 
        => throw new NotImplementedException();
}