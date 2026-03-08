using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Kometra.Infrastructure; // Aggiunto per l'accesso al LocalizationManager
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
                EnhancementCategory.RadialRotational => LocalizationManager.Instance["EnhancementCategoryRadial"],
                EnhancementCategory.FeatureExtraction => LocalizationManager.Instance["EnhancementCategoryFeatures"],
                EnhancementCategory.LocalContrast => LocalizationManager.Instance["EnhancementCategoryContrast"],
                _ => LocalizationManager.Instance["EnhancementCategoryDefault"]
            };
        }
        return LocalizationManager.Instance["EnhancementToolDefault"];
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}