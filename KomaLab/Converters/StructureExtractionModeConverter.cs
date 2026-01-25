using System;
using System.Globalization;
using Avalonia.Data.Converters;
using KomaLab.Models.Processing.Enhancement;

namespace KomaLab.Converters;

public class StructureExtractionModeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is StructureExtractionMode mode)
        {
            return mode switch
            {
                StructureExtractionMode.LarsonSekaninaStandard => "Larson-Sekanina (Standard)",
                StructureExtractionMode.LarsonSekaninaSymmetric => "Larson-Sekanina (Simmetrico)",
                
                StructureExtractionMode.UnsharpMaskingMedian => "Unsharp Masking (Mediana)",
                
                StructureExtractionMode.AdaptiveLaplacianRVSF => "RVSF Adattivo (Singolo)",
                StructureExtractionMode.AdaptiveLaplacianMosaic => "RVSF Adattivo (Mosaico)",
                
                _ => value.ToString()
            };
        }
        return "Sconosciuto";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}