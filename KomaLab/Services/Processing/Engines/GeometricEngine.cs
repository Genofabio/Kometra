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

    /// <summary>
    /// Esegue una traslazione sub-pixel di alta precisione.
    /// Utilizza un sistema di Alpha Mask per prevenire l'erosione dei bordi causata dai NaN 
    /// durante l'interpolazione Lanczos4.
    /// </summary>
    public Mat WarpTranslation(Mat source, Point2D sourcePoint, Point2D targetPoint, Size2D outputSize)
    {
        if (source == null || source.Empty()) return new Mat();

        // 1. Identificazione dell'area dati validi per l'isolamento fisico
        using Mat mask = new Mat();
        Cv2.Compare(source, source, mask, CmpType.EQ); // Identifica i pixel non-NaN
        Rect validRect = Cv2.BoundingRect(mask);

        if (validRect.Width <= 0 || validRect.Height <= 0) return source.Clone();

        // 2. Isolamento e preparazione della sorgente
        // Il Clone() è fondamentale: impedisce a Lanczos4 di campionare NaN esterni nella memoria fisica
        using Mat workingSrc = source.SubMat(validRect).Clone();
        if (workingSrc.Type() != MatType.CV_64FC1) workingSrc.ConvertTo(workingSrc, MatType.CV_64FC1);
        
        // Pulizia NaN interni per non corrompere l'interpolazione
        using (Mat nanMask = new Mat())
        {
            Cv2.Compare(workingSrc, workingSrc, nanMask, CmpType.NE);
            workingSrc.SetTo(new Scalar(0), nanMask);
        }

        // 3. Generazione Maschera Alpha per il ripristino selettivo dei NaN
        using Mat alphaMask = new Mat(workingSrc.Size(), MatType.CV_8UC1, new Scalar(255));

        // 4. Calcolo delle coordinate locali e del vettore di spostamento
        Point2D adjustedSourcePoint = new Point2D(sourcePoint.X - validRect.X, sourcePoint.Y - validRect.Y);
        double tx = targetPoint.X - adjustedSourcePoint.X;
        double ty = targetPoint.Y - adjustedSourcePoint.Y;

        using Mat m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, 1.0); m.Set(0, 1, 0.0); m.Set(0, 2, tx);
        m.Set(1, 0, 0.0); m.Set(1, 1, 1.0); m.Set(1, 2, ty);

        var cvSize = new Size((int)outputSize.Width, (int)outputSize.Height);
        
        // 5. Warp dell'immagine (Interpolazione Lanczos4 su sfondo nero)
        Mat result = new Mat(cvSize, MatType.CV_64FC1, new Scalar(0));
        Cv2.WarpAffine(workingSrc, result, m, cvSize, InterpolationFlags.Lanczos4, BorderTypes.Constant, new Scalar(0));

        // 6. Warp della maschera (Nearest Neighbor per mantenere i bordi netti)
        using Mat warpedMask = new Mat(cvSize, MatType.CV_8UC1, new Scalar(0));
        Cv2.WarpAffine(alphaMask, warpedMask, m, cvSize, InterpolationFlags.Nearest, BorderTypes.Constant, new Scalar(0));

        // 7. Ripristino dei NaN nelle aree prive di segnale originale
        using Mat finalMask = new Mat();
        Cv2.Compare(warpedMask, new Scalar(0), finalMask, CmpType.EQ);
        result.SetTo(new Scalar(double.NaN), finalMask);

        return result;
    }

    // =======================================================================
    // 2. ESTRAZIONE REGIONI (ANALISI)
    // =======================================================================

    public Mat ExtractRegion(Mat source, Point2D center, int radius)
    {
        if (source == null || source.Empty()) return new Mat();

        // Calcolo ROI con protezione dei confini della matrice
        int size = radius * 2;
        int sx = (int)Math.Max(0, center.X - radius);
        int sy = (int)Math.Max(0, center.Y - radius);
        int sw = (int)Math.Min(size, source.Width - sx);
        int sh = (int)Math.Min(size, source.Height - sy);

        if (sw <= 0 || sh <= 0) return new Mat();

        using Mat crop = new Mat(source, new Rect(sx, sy, sw, sh));
        
        // Conversione a Float32 per ottimizzare le prestazioni dei motori di analisi
        Mat result = new Mat();
        crop.ConvertTo(result, MatType.CV_32FC1); 
        
        // Sanitizzazione NaN per calcoli statistici
        Cv2.PatchNaNs(result, 0.0);
        
        return result;
    }
    
    // =======================================================================
    // 3. RITAGLIO E RIDIMENSIONAMENTO (CROP)
    // =======================================================================

    /// <summary>
    /// Ritaglia una porzione dell'immagine centrata su un punto specifico.
    /// Se l'area di ritaglio esce dai bordi dell'immagine originale, 
    /// lo spazio vuoto viene riempito con il valore di default (0 o NaN).
    /// </summary>
    public Mat CropCentered(Mat source, Point2D center, Size2D targetSize)
    {
        if (source == null || source.Empty()) return new Mat();

        int cw = (int)targetSize.Width;
        int ch = (int)targetSize.Height;
        
        // Calcolo dell'angolo in alto a sinistra del ritaglio (coordinate immagine sorgente)
        // Usiamo Math.Floor per gestire correttamente i centri sub-pixel se necessario
        int x = (int)Math.Floor(center.X - (cw / 2.0));
        int y = (int)Math.Floor(center.Y - (ch / 2.0));

        // 1. Creiamo il canvas di destinazione vuoto (nero/NaN)
        // Mantiene lo stesso tipo (es. CV_64FC1) della sorgente
        Mat result = new Mat(new Size(cw, ch), source.Type(), new Scalar(0));
        
        // Se l'immagine è float/double, inizializziamo a NaN per correttezza scientifica
        if (source.Depth() == MatType.CV_32F || source.Depth() == MatType.CV_64F)
        {
            result.SetTo(new Scalar(double.NaN));
        }

        // 2. Calcolo dell'intersezione (Safe Region of Interest)
        // Dobbiamo capire quale parte rettangolare della sorgente cade dentro il nostro crop
        int srcX = Math.Max(0, x);
        int srcY = Math.Max(0, y);
        int srcW = Math.Min(source.Width, x + cw) - srcX;
        int srcH = Math.Min(source.Height, y + ch) - srcY;

        // Coordinate relative nel canvas di destinazione
        int dstX = srcX - x;
        int dstY = srcY - y;

        // 3. Copia dei dati se c'è intersezione
        if (srcW > 0 && srcH > 0)
        {
            Rect srcRect = new Rect(srcX, srcY, srcW, srcH);
            Rect dstRect = new Rect(dstX, dstY, srcW, srcH);

            using var sourceRoi = new Mat(source, srcRect);
            using var destRoi = new Mat(result, dstRect);
            
            sourceRoi.CopyTo(destRoi);
        }

        return result;
    }
}