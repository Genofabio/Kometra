using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Kometra.Infrastructure; // Aggiunto per l'accesso al LocalizationManager
using Kometra.Models.Processing.Enhancement;

namespace Kometra.Converters;

public class ImageEnhancementModeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ImageEnhancementMode mode)
        {
            return mode switch
            {
                // --- Radial & Rotational ---
                ImageEnhancementMode.LarsonSekaninaStandard => LocalizationManager.Instance["EnhanceModeLarsonStd"],
                ImageEnhancementMode.LarsonSekaninaSymmetric => LocalizationManager.Instance["EnhanceModeLarsonSym"],
                ImageEnhancementMode.AdaptiveLaplacianRVSF => LocalizationManager.Instance["EnhanceModeRvsf"],
                ImageEnhancementMode.AdaptiveLaplacianMosaic => LocalizationManager.Instance["EnhanceModeMosaic"],
                
                // --- Geometric Models (Filtri Fisici) ---
                ImageEnhancementMode.InverseRho => LocalizationManager.Instance["EnhanceModeInvRho"],
                ImageEnhancementMode.RadialWeightedModel => LocalizationManager.Instance["EnhanceModeRwm"],
                ImageEnhancementMode.MedianComaModel => LocalizationManager.Instance["EnhanceModeMcm"],

                // --- Azimuthal ---
                ImageEnhancementMode.AzimuthalAverage => LocalizationManager.Instance["EnhanceModeAziAvg"],
                ImageEnhancementMode.AzimuthalMedian => LocalizationManager.Instance["EnhanceModeAziMed"],
                ImageEnhancementMode.AzimuthalRenormalization => LocalizationManager.Instance["EnhanceModeAziRenorm"],

                // --- Feature Extraction ---
                ImageEnhancementMode.FrangiVesselnessFilter => LocalizationManager.Instance["EnhanceModeFrangi"],
                ImageEnhancementMode.StructureTensorCoherence => LocalizationManager.Instance["EnhanceModeCoherence"],
                ImageEnhancementMode.WhiteTopHatExtraction => LocalizationManager.Instance["EnhanceModeWhiteTopHat"],
                
                // --- NUOVO: Adaptive Laplace ---
                ImageEnhancementMode.AdaptiveLaplaceFilter => LocalizationManager.Instance["EnhanceModeAdaptiveLaplace"],

                // --- Local Contrast ---
                ImageEnhancementMode.UnsharpMaskingMedian => LocalizationManager.Instance["EnhanceModeUnsharp"],
                ImageEnhancementMode.ClaheLocalContrast => LocalizationManager.Instance["EnhanceModeClahe"],
                ImageEnhancementMode.AdaptiveLocalNormalization => LocalizationManager.Instance["EnhanceModeZScore"],

                _ => value.ToString() ?? LocalizationManager.Instance["CommonUnknown"]
            };
        }
        return LocalizationManager.Instance["CommonUnknown"];
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}