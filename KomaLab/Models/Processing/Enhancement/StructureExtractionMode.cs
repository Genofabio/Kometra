namespace KomaLab.Models.Processing.Enhancement;

public enum StructureExtractionMode
{
    LarsonSekaninaStandard,    // I - Rot
    LarsonSekaninaSymmetric,   // 2I - Rot(+) - Rot(-)
    UnsharpMaskingMedian,      // Ex "RVSF" semplice (sottrazione mediana)
    AdaptiveLaplacianRVSF,     // RVSF Singolo (A, B, N)
    AdaptiveLaplacianMosaic    // RVSF Mosaico (8 combinazioni)
}