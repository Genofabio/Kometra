using OpenCvSharp;
using System;
using System.Threading.Tasks;

namespace KomaLab.Services.Processing.Engines;

public interface IStructureExtractionEngine
{
    /// <summary> Applica il filtro Larson-Sekanina per evidenziare morfologie rotazionali. </summary>
    Task ApplyLarsonSekaninaAsync(Mat src, Mat dst, double angleDeg, double radialShiftX, double radialShiftY, bool isSymmetric);

    /// <summary> Applica un filtro High-Pass tramite sottrazione della mediana locale. </summary>
    Task ApplyUnsharpMaskingMedianAsync(Mat src, Mat dst, int kernelSize, IProgress<double> progress = null);

    /// <summary> Applica il filtro RVSF (Radial Variable Slope Filter) adattivo. </summary>
    Task ApplyAdaptiveRVSFAsync(Mat src, Mat dst, double paramA, double paramB, double paramN, bool useLog, IProgress<double> progress = null);

    /// <summary> Genera un Mosaico (4x2) applicando l'RVSF con 8 combinazioni di parametri. </summary>
    Task ApplyRVSFMosaicAsync(Mat src, Mat dst, (double v1, double v2) paramA, (double v1, double v2) paramB, (double v1, double v2) paramN, bool useLog);

    /// <summary>
    /// Applica il Filtro di Frangi (Vesselness).
    /// Basato sull'analisi dell'Hessiana, è eccellente per isolare getti filamentosi anche se curvi.
    /// </summary>
    Task ApplyFrangiVesselnessAsync(Mat src, Mat dst, double sigma, double beta, double c, IProgress<double> progress = null);

    /// <summary>
    /// Applica CLAHE (Contrast Limited Adaptive Histogram Equalization).
    /// Normalizzazione dinamica del contrasto tramite istogrammi locali (workflow a 16-bit).
    /// </summary>
    Task ApplyClaheAsync(Mat src, Mat dst, double clipLimit, int tileGridSize);

    /// <summary>
    /// Applica la Normalizzazione Statistica Locale (LSN).
    /// Workflow 100% Floating Point. Normalizza ogni pixel basandosi sulla media e deviazione standard locale.
    /// </summary>
    Task ApplyLocalNormalizationAsync(Mat src, Mat dst, int windowSize, double intensity, IProgress<double> progress = null);

    /// <summary>
    /// Applica la Trasformata White Top-Hat.
    /// Operazione morfologica che "spiana" il bagliore della chioma per lasciare solo le strutture strette (getti).
    /// </summary>
    Task ApplyWhiteTopHatAsync(Mat src, Mat dst, int kernelSize);
    
    // <summary>
    /// Esalta le strutture basandosi sulla coerenza del Tensore di Struttura.
    /// Isola i flussi laminari e i getti lineari attenuando il rumore isotropico.
    /// </summary>
    Task ApplyStructureTensorEnhancementAsync(Mat src, Mat dst, int sigma, int rho, IProgress<double> progress = null);
}