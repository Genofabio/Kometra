namespace KomaLab.Models.Processing.Enhancement;

public enum StructureExtractionMode
{
    LarsonSekaninaStandard,
    LarsonSekaninaSymmetric,
    UnsharpMaskingMedian,
    AdaptiveLaplacianRVSF,
    AdaptiveLaplacianMosaic,
    FrangiVesselnessFilter,      // Coerenza basata su Hessiana (Curve)
    StructureTensorCoherence,    // Coerenza basata su Gradiente (Flusso/Rette)
    WhiteTopHatExtraction,
    ClaheLocalContrast,
    AdaptiveLocalNormalization
}