using System;
using Kometra.Models.Primitives;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public class GeometricEngine : IGeometricEngine
{
    public GeometricEngine() { }

    // =======================================================================
    // 1. TRASFORMAZIONI SPAZIALI (WARPING)
    // =======================================================================

    /// <summary>
    /// Esegue una traslazione sub-pixel di alta precisione.
    /// Utilizza un sistema di Alpha Mask per prevenire l'erosione dei bordi causata dai NaN 
    /// durante l'interpolazione Lanczos4 e Border Replicate per evitare ringing.
    /// </summary>
    public Mat WarpTranslation(Mat source, Point2D sourcePoint, Point2D targetPoint, Size2D outputSize)
    {
        if (source == null || source.Empty()) return new Mat();

        // 1. Identificazione dell'area dati validi per l'isolamento fisico
        using Mat mask = new Mat();
        // Check per float/double per gestire NaN
        if (source.Depth() == MatType.CV_32F || source.Depth() == MatType.CV_64F)
        {
            // Nota: Compare con se stesso per NaN fallisce in alcune versioni, meglio check != 0 se nan-patched
            // Qui assumiamo che i NaN siano gestiti o che usiamo una maschera binaria sicura.
            Cv2.Compare(source, source, mask, CmpTypes.EQ); 
        }
        else
        {
            Cv2.Compare(source, new Scalar(0), mask, CmpTypes.NE);
        }
        
        Rect validRect = Cv2.BoundingRect(mask);

        if (validRect.Width <= 0 || validRect.Height <= 0) 
            return new Mat(new Size((int)outputSize.Width, (int)outputSize.Height), source.Type(), new Scalar(0));

        // 2. Isolamento e preparazione della sorgente
        using Mat workingSrc = source.SubMat(validRect).Clone();
        if (workingSrc.Type() != MatType.CV_64FC1) workingSrc.ConvertTo(workingSrc, MatType.CV_64FC1);
        
        // Pulizia NaN interni: sostituiamo con 0 per non corrompere i calcoli di Lanczos
        using (Mat nanMask = new Mat())
        {
            Cv2.Compare(workingSrc, workingSrc, nanMask, CmpTypes.NE); // Trova i NaN (dove a != a)
            workingSrc.SetTo(new Scalar(0), nanMask);
        }

        // 3. Generazione Maschera Alpha (255 dove c'è immagine, 0 fuori)
        using Mat alphaMask = new Mat(workingSrc.Size(), MatType.CV_8UC1, new Scalar(255));

        // 4. Calcolo coordinate e matrice di trasformazione
        Point2D adjustedSourcePoint = new Point2D(sourcePoint.X - validRect.X, sourcePoint.Y - validRect.Y);
        double tx = targetPoint.X - adjustedSourcePoint.X;
        double ty = targetPoint.Y - adjustedSourcePoint.Y;

        using Mat m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, 1.0); m.Set(0, 1, 0.0); m.Set(0, 2, tx);
        m.Set(1, 0, 0.0); m.Set(1, 1, 1.0); m.Set(1, 2, ty);

        var cvSize = new Size((int)outputSize.Width, (int)outputSize.Height);
        
        // 5. Warp dell'immagine 
        // CORREZIONE QUI: BorderTypes.Replicate invece di Constant.
        // Questo estende l'ultimo pixel valido verso l'infinito, fornendo a Lanczos
        // dati coerenti ai bordi ed eliminando il "ringing" (riga bianca/nera) causato dal salto a zero.
        Mat result = new Mat(cvSize, MatType.CV_64FC1, new Scalar(0));
        Cv2.WarpAffine(workingSrc, result, m, cvSize, InterpolationFlags.Lanczos4, BorderTypes.Replicate);

        // 6. Warp della maschera 
        // Qui usiamo Constant(0) perché la maschera DEVE dire dove finisce l'immagine reale.
        // Nearest neighbor mantiene i bordi della maschera netti (senza sfumature).
        using Mat warpedMask = new Mat(cvSize, MatType.CV_8UC1, new Scalar(0));
        Cv2.WarpAffine(alphaMask, warpedMask, m, cvSize, InterpolationFlags.Nearest, BorderTypes.Constant, new Scalar(0));

        // 7. Ripristino dei NaN (Clipping finale)
        // Usiamo la maschera warpata per tagliare via tutto ciò che è stato "replicato" o "inventato" fuori dai bordi.
        using Mat finalMask = new Mat();
        Cv2.Compare(warpedMask, new Scalar(0), finalMask, CmpTypes.EQ);
        result.SetTo(new Scalar(double.NaN), finalMask);

        return result;
    }

    // =======================================================================
    // 2. ESTRAZIONE REGIONI (ANALISI) - Invariato
    // =======================================================================

    public Mat ExtractRegion(Mat source, Point2D center, int radius)
    {
        if (source == null || source.Empty()) return new Mat();

        int size = radius * 2;
        int sx = (int)Math.Max(0, center.X - radius);
        int sy = (int)Math.Max(0, center.Y - radius);
        int sw = (int)Math.Min(size, source.Width - sx);
        int sh = (int)Math.Min(size, source.Height - sy);

        if (sw <= 0 || sh <= 0) return new Mat();

        using Mat crop = new Mat(source, new Rect(sx, sy, sw, sh));
        Mat result = new Mat();
        crop.ConvertTo(result, MatType.CV_32FC1); 
        Cv2.PatchNaNs(result, 0.0);
        return result;
    }
    
    // =======================================================================
    // 3. RITAGLIO E RIDIMENSIONAMENTO (CROP) - Invariato
    // =======================================================================

    public Mat CropCentered(Mat source, Point2D center, Size2D targetSize)
    {
        if (source == null || source.Empty()) return new Mat();

        int cw = (int)targetSize.Width;
        int ch = (int)targetSize.Height;
        
        int x = (int)Math.Floor(center.X - (cw / 2.0));
        int y = (int)Math.Floor(center.Y - (ch / 2.0));

        Scalar fillValue = new Scalar(0);
        bool isFloat = source.Depth() == MatType.CV_32F || source.Depth() == MatType.CV_64F;
        if (isFloat) fillValue = new Scalar(double.NaN);

        Mat result = new Mat(new Size(cw, ch), source.Type(), fillValue);

        int srcX = Math.Max(0, x);
        int srcY = Math.Max(0, y);
        int srcW = Math.Min(source.Width, x + cw) - srcX;
        int srcH = Math.Min(source.Height, y + ch) - srcY;

        if (srcW > 0 && srcH > 0)
        {
            int dstX = srcX - x;
            int dstY = srcY - y;

            Rect srcRect = new Rect(srcX, srcY, srcW, srcH);
            Rect dstRect = new Rect(dstX, dstY, srcW, srcH);

            using var sourceRoi = new Mat(source, srcRect);
            using var destRoi = new Mat(result, dstRect);
            sourceRoi.CopyTo(destRoi);
        }

        return result;
    }
}