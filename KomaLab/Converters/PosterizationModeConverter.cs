using System;
using System.Globalization;
using Avalonia.Data.Converters;
using KomaLab.Models; // Assicurati che il namespace del tuo Enum sia corretto

namespace KomaLab.Converters
{
    public class PosterizationModeConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is VisualizationMode mode)
            {
                return mode switch
                {
                    VisualizationMode.Linear => "Lineare",
                    VisualizationMode.Logarithmic => "Logaritmico",
                    VisualizationMode.SquareRoot => "Radice Quadrata",
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