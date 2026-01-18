using System;
using KomaLab.Models.Primitives;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

public class GeometricEngine : IGeometricEngine
{
    public GeometricEngine() { }

    // =======================================================================
    // 1. TRASFORMAZIONI SPAZIALI (WARPING)
    // =======================================================================

    public Mat WarpTranslation(Mat source, Point2D sourcePoint, Point2D targetPoint, Size2D outputSize)
    {
        if (source == null || source.Empty()) return new Mat();

        // 1. Lavoriamo sempre in Double per preservare la fotometria durante il ricampionamento
        bool needsConversion = source.Type() != MatType.CV_64FC1;
        using Mat workingSrc = needsConversion ? new Mat() : source;
        if (needsConversion) source.ConvertTo(workingSrc, MatType.CV_64FC1);

        // 2. Calcolo del vettore di spostamento (Shift Vector)
        // La matematica è: Punto_Destinazione - Punto_Sorgente
        double tx = targetPoint.X - sourcePoint.X;
        double ty = targetPoint.Y - sourcePoint.Y;

        // 3. Matrice di Trasformazione Affine (Traslazione pura)
        // $$ M = \begin{bmatrix} 1 & 0 & tx \\ 0 & 1 & ty \end{bmatrix} $$
        using Mat m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, 1.0); m.Set(0, 1, 0.0); m.Set(0, 2, tx);
        m.Set(1, 0, 0.0); m.Set(1, 1, 1.0); m.Set(1, 2, ty);

        var cvSize = new Size((int)outputSize.Width, (int)outputSize.Height);
        
        // 4. Inizializziamo il risultato con NaN (Background astronomico standard)
        Mat result = new Mat(cvSize, MatType.CV_64FC1, new Scalar(double.NaN));
        
        // 5. Warp con Lanczos4: il "Gold Standard" per preservare la PSF delle stelle
        // Evita l'effetto "morbido" della bilineare e il ringing della bicubica semplice.
        Cv2.WarpAffine(
            workingSrc, 
            result, 
            m, 
            cvSize, 
            InterpolationFlags.Lanczos4, 
            BorderTypes.Constant, 
            new Scalar(double.NaN)
        );

        return result;
    }

    // =======================================================================
    // 2. ESTRAZIONE REGIONI (ANALISI)
    // =======================================================================

    public Mat ExtractRegion(Mat source, Point2D center, int radius)
    {
        if (source == null || source.Empty()) return new Mat();

        // Calcolo ROI con protezione bordi
        int size = radius * 2;
        int sx = (int)Math.Max(0, center.X - radius);
        int sy = (int)Math.Max(0, center.Y - radius);
        int sw = (int)Math.Min(size, source.Width - sx);
        int sh = (int)Math.Min(size, source.Height - sy);

        if (sw <= 0 || sh <= 0) return new Mat();

        using Mat crop = new Mat(source, new Rect(sx, sy, sw, sh));
        
        // Convertiamo a Float32 per l'analisi (più performante di Double e sufficiente)
        Mat result = new Mat();
        crop.ConvertTo(result, MatType.CV_32FC1); 
        
        // Pulizia per motori di analisi (es. Star Finder o Comet Tracker)
        Cv2.PatchNaNs(result, 0.0);
        
        return result;
    }
}