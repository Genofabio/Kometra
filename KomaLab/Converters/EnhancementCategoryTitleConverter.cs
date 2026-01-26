using System;
using System.Globalization;
using Avalonia.Data.Converters;
using KomaLab.Models.Processing.Enhancement;

namespace KomaLab.Converters;

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
        return "Strumento KomaLab";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}