using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Kometra.Infrastructure; // Aggiunto per l'accesso al LocalizationManager
using Kometra.Models.Visualization;

namespace Kometra.Converters
{
    public class PosterizationModeConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is VisualizationMode mode)
            {
                return mode switch
                {
                    // Recuperiamo le traduzioni dal LocalizationManager
                    VisualizationMode.Linear => LocalizationManager.Instance["ViewModeLinear"],
                    VisualizationMode.Logarithmic => LocalizationManager.Instance["ViewModeLogarithmic"],
                    VisualizationMode.SquareRoot => LocalizationManager.Instance["ViewModeSquareRoot"],
                    _ => value.ToString()
                };
            }
            return value;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}