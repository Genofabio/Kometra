using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Kometra.Infrastructure; // Aggiunto per l'accesso al LocalizationManager
using Kometra.Models.Processing.Enhancement;

namespace Kometra.Converters;

public class ImageEnhancementDescriptionConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ImageEnhancementMode mode)
        {
            return mode switch
            {
                // --- Radial & Rotational (Gradienti) ---
                ImageEnhancementMode.LarsonSekaninaStandard => 
                    LocalizationManager.Instance["EnhanceDescLarsonStd"],
                
                ImageEnhancementMode.LarsonSekaninaSymmetric => 
                    LocalizationManager.Instance["EnhanceDescLarsonSym"],

                ImageEnhancementMode.AdaptiveLaplacianRVSF => 
                    LocalizationManager.Instance["EnhanceDescRvsf"],

                ImageEnhancementMode.AdaptiveLaplacianMosaic => 
                    LocalizationManager.Instance["EnhanceDescMosaic"],

                // --- Polar & Digital Models (Geometria) ---
                ImageEnhancementMode.InverseRho => 
                    LocalizationManager.Instance["EnhanceDescInvRho"],

                ImageEnhancementMode.RadialWeightedModel =>
                    LocalizationManager.Instance["EnhanceDescRwm"],

                ImageEnhancementMode.MedianComaModel =>
                    LocalizationManager.Instance["EnhanceDescMcm"],

                // --- Azimuthal Filters (Polari) ---
                ImageEnhancementMode.AzimuthalAverage => 
                    LocalizationManager.Instance["EnhanceDescAziAvg"],

                ImageEnhancementMode.AzimuthalMedian => 
                    LocalizationManager.Instance["EnhanceDescAziMed"],

                ImageEnhancementMode.AzimuthalRenormalization => 
                    LocalizationManager.Instance["EnhanceDescAziRenorm"],

                // --- Feature Extraction (Morfologia) ---
                ImageEnhancementMode.FrangiVesselnessFilter =>
                    LocalizationManager.Instance["EnhanceDescFrangi"],

                ImageEnhancementMode.StructureTensorCoherence =>
                    LocalizationManager.Instance["EnhanceDescCoherence"],

                ImageEnhancementMode.WhiteTopHatExtraction =>
                    LocalizationManager.Instance["EnhanceDescWhiteTopHat"],

                // --- Adaptive Laplace ---
                ImageEnhancementMode.AdaptiveLaplaceFilter =>
                    LocalizationManager.Instance["EnhanceDescAdaptiveLaplace"],

                // --- Local Contrast ---
                ImageEnhancementMode.UnsharpMaskingMedian => 
                    LocalizationManager.Instance["EnhanceDescUnsharp"],

                ImageEnhancementMode.ClaheLocalContrast =>
                    LocalizationManager.Instance["EnhanceDescClahe"],

                ImageEnhancementMode.AdaptiveLocalNormalization =>
                    LocalizationManager.Instance["EnhanceDescZScore"],
                
                _ => LocalizationManager.Instance["EnhanceDescDefault"]
            };
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}