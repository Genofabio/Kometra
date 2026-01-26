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
                // --- Algoritmi Rotazionali e High-Pass ---
                StructureExtractionMode.LarsonSekaninaStandard => "Larson-Sekanina (Standard)",
                StructureExtractionMode.LarsonSekaninaSymmetric => "Larson-Sekanina (Simmetrico)",
                StructureExtractionMode.UnsharpMaskingMedian => "Unsharp Masking (Mediana)",
                
                // --- Algoritmi Radiali (RVSF) ---
                StructureExtractionMode.AdaptiveLaplacianRVSF => "RVSF Adattivo (Singolo)",
                StructureExtractionMode.AdaptiveLaplacianMosaic => "RVSF Adattivo (Mosaico)",

                // --- Analisi della Coerenza e Getti ---
                StructureExtractionMode.FrangiVesselnessFilter => "Filtro di Frangi (Getti Curvi)",
                StructureExtractionMode.StructureTensorCoherence => "Esaltazione Coerenza (Tensore)",
                
                // --- Morfologia e Contrast Enhancement ---
                StructureExtractionMode.WhiteTopHatExtraction => "White Top-Hat (Morfologico)",
                StructureExtractionMode.ClaheLocalContrast => "CLAHE (Contrasto Adattivo)",
                StructureExtractionMode.AdaptiveLocalNormalization => "Normalizzazione Locale (LSN)",
                
                _ => value.ToString() ?? "Sconosciuto"
            };
        }
        return "Sconosciuto";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // La conversione inversa solitamente non è necessaria per le ComboBox sola lettura
        throw new NotSupportedException();
    }
}