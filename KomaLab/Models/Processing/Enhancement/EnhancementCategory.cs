namespace KomaLab.Models.Processing.Enhancement;

public enum EnhancementCategory
{
    RadialRotational,
    FeatureExtraction, 
    LocalContrast
}

public static class ImageEnhancementModeExtensions
{
    // Questo è OK nel modello perché è logica di business (categorizzazione)
    public static EnhancementCategory GetCategory(this ImageEnhancementMode mode)
    {
        return mode switch
        {
            ImageEnhancementMode.LarsonSekaninaStandard => EnhancementCategory.RadialRotational,
            ImageEnhancementMode.LarsonSekaninaSymmetric => EnhancementCategory.RadialRotational,
            ImageEnhancementMode.AdaptiveLaplacianRVSF => EnhancementCategory.RadialRotational,
            ImageEnhancementMode.AdaptiveLaplacianMosaic => EnhancementCategory.RadialRotational,
            ImageEnhancementMode.InverseRho => EnhancementCategory.RadialRotational,
            ImageEnhancementMode.MedianComaModel => EnhancementCategory.RadialRotational,
            ImageEnhancementMode.RadialWeightedModel => EnhancementCategory.RadialRotational,
            ImageEnhancementMode.AzimuthalAverage => EnhancementCategory.RadialRotational,
            ImageEnhancementMode.AzimuthalMedian => EnhancementCategory.RadialRotational,
            ImageEnhancementMode.AzimuthalRenormalization => EnhancementCategory.RadialRotational,

            ImageEnhancementMode.FrangiVesselnessFilter => EnhancementCategory.FeatureExtraction,
            ImageEnhancementMode.StructureTensorCoherence => EnhancementCategory.FeatureExtraction,
            ImageEnhancementMode.WhiteTopHatExtraction => EnhancementCategory.FeatureExtraction,

            ImageEnhancementMode.UnsharpMaskingMedian => EnhancementCategory.LocalContrast,
            ImageEnhancementMode.ClaheLocalContrast => EnhancementCategory.LocalContrast,
            ImageEnhancementMode.AdaptiveLocalNormalization => EnhancementCategory.LocalContrast,

            _ => EnhancementCategory.LocalContrast
        };
    }
}