using System;
using System.Globalization;
using Avalonia.Data.Converters;
using KomaLab.Models.Processing.Enhancement;

namespace KomaLab.Converters;

public class ImageEnhancementModeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ImageEnhancementMode mode)
        {
            return mode switch
            {
                // --- Radial & Rotational ---
                ImageEnhancementMode.LarsonSekaninaStandard => "Larson-Sekanina (Standard)",
                ImageEnhancementMode.LarsonSekaninaSymmetric => "Larson-Sekanina (Simmetrico)",
                ImageEnhancementMode.AdaptiveLaplacianRVSF => "RVSF Adattivo (Singolo)",
                ImageEnhancementMode.AdaptiveLaplacianMosaic => "RVSF Adattivo (Mosaico)",
                
                // --- Geometric Models (Filtri Fisici) ---
                ImageEnhancementMode.InverseRho => "Correzione Fisica (1/Rho)",
                ImageEnhancementMode.RadialWeightedModel => "Radial Weighted Model (R.W.M.)", // AGGIUNTO
                ImageEnhancementMode.MedianComaModel => "Median Coma Model (M.C.M.)",         // AGGIUNTO

                // --- Azimuthal ---
                ImageEnhancementMode.AzimuthalAverage => "Media Azimutale",
                ImageEnhancementMode.AzimuthalMedian => "Mediana Azimutale",
                ImageEnhancementMode.AzimuthalRenormalization => "Rinormalizzazione Azimutale",

                // --- Feature Extraction ---
                ImageEnhancementMode.FrangiVesselnessFilter => "Filtro di Frangi (Getti Curvi)",
                ImageEnhancementMode.StructureTensorCoherence => "Esaltazione Coerenza (Tensore)",
                ImageEnhancementMode.WhiteTopHatExtraction => "White Top-Hat (Morfologico)",

                // --- Local Contrast ---
                ImageEnhancementMode.UnsharpMaskingMedian => "Unsharp Masking (Mediana)",
                ImageEnhancementMode.ClaheLocalContrast => "CLAHE (Contrasto Adattivo)",
                ImageEnhancementMode.AdaptiveLocalNormalization => "Normalizzazione Locale (LSN)",

                _ => value.ToString() ?? "Sconosciuto"
            };
        }
        return "Sconosciuto";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}