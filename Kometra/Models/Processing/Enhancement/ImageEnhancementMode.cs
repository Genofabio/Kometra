namespace Kometra.Models.Processing.Enhancement;

public enum ImageEnhancementMode
{
    // --- 1. RADIAL & ROTATIONAL ENHANCEMENT (Morfologia Cometa) ---
    LarsonSekaninaSymmetric,
    LarsonSekaninaStandard,
    AdaptiveLaplacianRVSF,
    AdaptiveLaplacianMosaic,
    MedianComaModel,
    RadialWeightedModel,
    InverseRho,
    AzimuthalAverage,
    AzimuthalMedian,
    AzimuthalRenormalization,

    // --- 2. SHAPE & FEATURE EXTRACTION (Estrazione Forme) ---
    FrangiVesselnessFilter,
    StructureTensorCoherence,
    WhiteTopHatExtraction,
    AdaptiveLaplaceFilter,

    // --- 3. LOCAL CONTRAST ENHANCEMENT (Contrasto Locale) ---
    UnsharpMaskingMedian,
    ClaheLocalContrast,
    AdaptiveLocalNormalization
}