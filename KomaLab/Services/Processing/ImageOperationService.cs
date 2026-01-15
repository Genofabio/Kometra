using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using OpenCvSharp;

namespace KomaLab.Services.Processing;

// ---------------------------------------------------------------------------
// FILE: ImageOperationService.cs
// RUOLO: Operatore Pixel-Level (Pure Math Engine)
// VERSIONE: Completamente disaccoppiata da FITS. Lavora solo su Mat.
// ---------------------------------------------------------------------------

public class ImageOperationService : IImageOperationService
{
    private readonly IImageAnalysisService _analysis;

    // Rimosse dipendenze IoService/Converter/Metadata. 
    // Questo servizio ora elabora solo Matrici già pronte.
    public ImageOperationService(IImageAnalysisService analysis)
    {
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
    }

    // =======================================================================
    // 1. WARPING GEOMETRICO
    // =======================================================================

    public Mat GetSubPixelCenteredCanvas(Mat source, Point2D originalCenter, Size2D outputSize)
    {
        // Assicura input in Double
        Mat sourceDouble = source;
        bool disposeSource = false;
        
        if (source.Type() != MatType.CV_64FC1)
        {
            sourceDouble = new Mat();
            source.ConvertTo(sourceDouble, MatType.CV_64FC1);
            disposeSource = true;
        }

        try
        {
            double destCenterX = outputSize.Width / 2.0;
            double destCenterY = outputSize.Height / 2.0;
            double tx = destCenterX - originalCenter.X;
            double ty = destCenterY - originalCenter.Y;

            using Mat m = new Mat(2, 3, MatType.CV_64FC1);
            m.Set(0, 0, 1.0); m.Set(0, 1, 0.0); m.Set(0, 2, tx);
            m.Set(1, 0, 0.0); m.Set(1, 1, 1.0); m.Set(1, 2, ty);

            var cvSize = new Size((int)outputSize.Width, (int)outputSize.Height);
            
            Mat result = new Mat(cvSize, MatType.CV_64FC1, new Scalar(double.NaN));
            
            Cv2.WarpAffine(
                sourceDouble, 
                result, 
                m, 
                cvSize, 
                InterpolationFlags.Lanczos4, 
                BorderTypes.Constant, 
                new Scalar(double.NaN)
            );

            return result;
        }
        finally
        {
            if (disposeSource) sourceDouble.Dispose();
        }
    }

    // =======================================================================
    // 2. TEMPLATE MATCHING
    // =======================================================================

    // NOTA: Qui ricevevamo FitsImageData. Ora riceviamo Mat.
    // Chi chiama questo metodo deve aver già convertito l'immagine.
    public (Mat Template, Point2D RefinedCenter) ExtractRefinedTemplate(Mat fullImg, Point2D roughGuess, int radius)
    {
        if (fullImg == null || fullImg.Empty()) return (new Mat(), roughGuess);

        // Estrai crop iniziale
        Rect2D roi = CreateSafeRoi(fullImg, roughGuess, radius);
        if (roi.Width <= 0) throw new InvalidOperationException("ROI non valida (fuori immagine)");

        var cvRoi = new Rect((int)roi.X, (int)roi.Y, (int)roi.Width, (int)roi.Height);
        using Mat crop = new Mat(fullImg, cvRoi);
        
        // Raffina il centro
        Point2D local = _analysis.FindCenterOfLocalRegion(crop);
        Point2D global = new Point2D(local.X + roi.X, local.Y + roi.Y);

        // Estrai il template definitivo
        Rect2D templRect = CreateSafeRoi(fullImg, global, radius);
        var cvTemplRect = new Rect((int)templRect.X, (int)templRect.Y, (int)templRect.Width, (int)templRect.Height);
        
        using Mat tempRaw = new Mat(fullImg, cvTemplRect);
        
        // Normalizza
        Mat templateF = NormalizeAndConvertToFloat(tempRaw);
        
        return (templateF, global);
    }

    public Point2D? FindTemplatePosition(Mat fullImage, Mat templateF, Point2D expectedCenter, int searchRadius)
    {
        if (fullImage.Empty() || templateF.Empty()) return null;

        int searchW = searchRadius * 3;
        Rect2D searchRect = CreateSafeRoi(fullImage, expectedCenter, searchW);
        
        if (searchRect.Width <= templateF.Width || searchRect.Height <= templateF.Height) return null;

        var cvSearchRect = new Rect((int)searchRect.X, (int)searchRect.Y, (int)searchRect.Width, (int)searchRect.Height);
        using Mat searchRegion = new Mat(fullImage, cvSearchRect);
        using Mat searchF = NormalizeAndConvertToFloat(searchRegion);
        
        using Mat res = new Mat();
        Cv2.MatchTemplate(searchF, templateF, res, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out Point maxLoc);

        if (maxVal < 0.5) return null;

        double matchCx = maxLoc.X + (templateF.Width / 2.0);
        double matchCy = maxLoc.Y + (templateF.Height / 2.0);
        Point2D roughLocal = new Point2D(matchCx, matchCy);

        // Raffinamento sub-pixel
        Rect2D refineRect = CreateSafeRoi(searchRegion, roughLocal, searchRadius);
        Point2D subPixelCenter;
        
        if (refineRect.Width > 0 && refineRect.Height > 0)
        {
            var cvRefineRect = new Rect((int)refineRect.X, (int)refineRect.Y, (int)refineRect.Width, (int)refineRect.Height);
            using Mat refineCrop = new Mat(searchRegion, cvRefineRect);
            
            Point2D localRefined = _analysis.FindCenterOfLocalRegion(refineCrop);
            subPixelCenter = new Point2D(localRefined.X + refineRect.X, localRefined.Y + refineRect.Y);
        }
        else 
        {
            subPixelCenter = roughLocal;
        }

        return new Point2D(subPixelCenter.X + searchRect.X, subPixelCenter.Y + searchRect.Y);
    }

    // =======================================================================
    // 3. STACKING (PURE MATH)
    // =======================================================================

    /// <summary>
    /// Esegue lo stacking matematico su una lista di matrici OpenCV.
    /// Non si preoccupa di caricamento file o header. Restituisce una Matrice Result.
    /// </summary>
    public async Task<Mat> ComputeStackAsync(List<Mat> sources, StackingMode mode)
    {
        if (sources == null || sources.Count == 0) throw new ArgumentException("Nessuna immagine da stackare");
        
        int width = sources[0].Width;
        int height = sources[0].Height;
        Mat resultMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(0)); // Restituita al chiamante

        await Task.Run(() =>
        {
            if (mode == StackingMode.Sum || mode == StackingMode.Average)
            {
                using Mat validCountMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));
                using Mat onesMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(1));
                
                foreach (var currentMat in sources)
                {
                    // Assumiamo che le matrici in input siano già Double (CV_64FC1).
                    // Se non lo sono, il chiamante doveva convertirle.
                    // (Opzionale: aggiungere check/conversione qui se non ci fidiamo).
                    
                    using Mat nonNanMask = new Mat();
                    Cv2.Compare(currentMat, currentMat, nonNanMask, CmpType.EQ); // Maschera Not-NaN
                    
                    Cv2.Add(resultMat, currentMat, resultMat, mask: nonNanMask);
                    Cv2.Add(validCountMat, onesMat, validCountMat, mask: nonNanMask);
                }
                
                if (mode == StackingMode.Average)
                {
                    using Mat safeDivisor = validCountMat.Clone();
                    Cv2.Max(safeDivisor, 1.0, safeDivisor);
                    Cv2.Divide(resultMat, safeDivisor, resultMat, scale: 1, dtype: MatType.CV_64FC1);
                }
            }
            else if (mode == StackingMode.Median)
            {
                // Mediana Pixel-by-Pixel (Parallelizzata a strip)
                // Nota: sources è List<Mat>, quindi accesso diretto senza RawToMatRect
                
                int stripHeight = 50; 
                Parallel.For(0, (height + stripHeight - 1) / stripHeight, stripIndex =>
                {
                    int yStart = stripIndex * stripHeight;
                    int currentStripH = Math.Min(stripHeight, height - yStart);
                    int bufSize = width * currentStripH;

                    // Buffer locali thread-safe
                    double[][] srcBuffers = new double[sources.Count][];
                    double[] destBuffer = new double[bufSize];
                    double[] pixelSorter = new double[sources.Count]; // Riutilizzato per ogni pixel

                    // Copia dati dalle matrici ai buffer
                    for (int k = 0; k < sources.Count; k++)
                    {
                        srcBuffers[k] = new double[bufSize];
                        // Copia rettangolo in un colpo solo se contiguo, o riga per riga
                        // Per semplicità qui copiamo riga per riga
                        for(int r=0; r<currentStripH; r++)
                        {
                            IntPtr srcPtr = sources[k].Ptr(yStart + r);
                            Marshal.Copy(srcPtr, srcBuffers[k], r * width, width);
                        }
                    }

                    // Calcolo Mediana
                    for (int i = 0; i < bufSize; i++)
                    {
                        int count = 0;
                        for (int k = 0; k < sources.Count; k++)
                        {
                            double val = srcBuffers[k][i];
                            if (!double.IsNaN(val)) pixelSorter[count++] = val;
                        }

                        if (count == 0) {
                            destBuffer[i] = double.NaN;
                        } else {
                            Array.Sort(pixelSorter, 0, count);
                            destBuffer[i] = (count % 2 == 0) 
                                ? (pixelSorter[count/2 - 1] + pixelSorter[count/2]) * 0.5 
                                : pixelSorter[count/2];
                        }
                    }

                    // Scrittura risultato nella Matrice finale (Lock necessario se non scriviamo su zone distinte?
                    // No, stiamo scrivendo su yStart diverse, quindi è safe).
                    for(int r=0; r<currentStripH; r++)
                    {
                        IntPtr destPtr = resultMat.Ptr(yStart + r);
                        Marshal.Copy(destBuffer, r * width, destPtr, width);
                    }
                });
            }
        });

        return resultMat;
    }

    // --- HELPER PRIVATI ---

    private Mat NormalizeAndConvertToFloat(Mat source)
    {
        Mat floatMat = new Mat();
        source.ConvertTo(floatMat, MatType.CV_32FC1);
        Cv2.Normalize(floatMat, floatMat, 0, 1, NormTypes.MinMax);
        return floatMat;
    }

    private Rect2D CreateSafeRoi(Mat mat, Point2D center, int radius)
    {
        int size = radius * 2;
        int sx = Math.Max(0, (int)(center.X - radius));
        int sy = Math.Max(0, (int)(center.Y - radius));
        int sw = Math.Min(size, mat.Width - sx);
        int sh = Math.Min(size, mat.Height - sy);
        return new Rect2D(sx, sy, sw, sh);
    }
}