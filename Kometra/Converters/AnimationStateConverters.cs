using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;
using Kometra.Infrastructure; // Aggiunto per l'accesso al LocalizationManager

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
            // Recupera la traduzione per "Ferma Animazione"
            return LocalizationManager.Instance["MenuStopAnimation"];
        }
        
        // Recupera la traduzione per "Avvia Animazione"
        return LocalizationManager.Instance["MenuStartAnimation"];
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) 
        => throw new NotImplementedException();
}