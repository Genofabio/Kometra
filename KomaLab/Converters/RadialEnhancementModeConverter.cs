using System;
using System.Globalization;
using Avalonia.Data.Converters;
using KomaLab.Models.Processing;
using KomaLab.Models.Processing.Enhancement;

namespace KomaLab.Converters;

public class RadialEnhancementModeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is RadialEnhancementMode mode)
        {
            return mode switch
            {
                RadialEnhancementMode.InverseRho => "Correzione Fisica (1/Rho)",
                RadialEnhancementMode.AzimuthalAverage => "Media Azimutale",
                RadialEnhancementMode.AzimuthalMedian => "Mediana Azimutale",
                RadialEnhancementMode.AzimuthalRenormalization => "Rinormalizzazione",
                _ => mode.ToString()
            };
        }
        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}