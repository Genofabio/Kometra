using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Kometra.Models.Processing.Enhancement;

namespace Kometra.Converters;

public class EnhancementCategoryTitleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is EnhancementCategory category)
        {
            return category switch
            {
                EnhancementCategory.RadialRotational => "Analisi Morfologica (Radiale & Rotazionale)",
                EnhancementCategory.FeatureExtraction => "Estrazione Strutture e Forme",
                EnhancementCategory.LocalContrast => "Miglioramento Contrasto Locale",
                _ => "Strumenti di Elaborazione"
            };
        }
        return "Strumento Kometra";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}