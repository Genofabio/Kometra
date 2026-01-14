using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;
using OpenCvSharp;
// Per IFitsMetadataService

namespace KomaLab.Services.Processing;

// ---------------------------------------------------------------------------
// FILE: ImageOperationService.cs
// RUOLO: Operatore Pixel-Level
// DESCRIZIONE:
// Esegue manipolazioni "distruttive" o trasformative sui dati pixel:
// 1. Warping: Rotazione/Traslazione sub-pixel (Lanczos4).
// 2. Template Matching: Ricerca di pattern per l'allineamento.
// 3. Stacking: Somma/Media/Mediana di stack di immagini.
// ---------------------------------------------------------------------------

public class ImageOperationService : IImageOperationService
{
    private readonly IFitsImageDataConverter _converter;
    private readonly IFitsMetadataService _metadataService; // Nuovo servizio necessario
    private readonly IImageAnalysisService _analysis;

    public ImageOperationService(
        IFitsImageDataConverter converter, 
        IFitsMetadataService metadataService,
        IImageAnalysisService analysis)
    {
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
    }

    // =======================================================================
    // 1. WARPING GEOMETRICO
    // =======================================================================

    public Mat GetSubPixelCenteredCanvas(Mat source, Point2D originalCenter, Size2D outputSize)
    {
        // Calcolo matrice di traslazione (Affine 2x3) per centrare l'oggetto nel nuovo canvas
        double destCenterX = outputSize.Width / 2.0;
        double destCenterY = outputSize.Height / 2.0;
        double tx = destCenterX - originalCenter.X;
        double ty = destCenterY - originalCenter.Y;

        using Mat m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, 1.0); m.Set(0, 1, 0.0); m.Set(0, 2, tx);
        m.Set(1, 0, 0.0); m.Set(1, 1, 1.0); m.Set(1, 2, ty);

        var cvSize = new Size((int)outputSize.Width, (int)outputSize.Height);
        
        // Inizializza a NaN (o 0 se preferisci bordi neri, ma NaN è meglio per lo stacking successivo)
        Mat result = new Mat(cvSize, MatType.CV_64FC1, new Scalar(double.NaN));
        
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
            // Lanczos4: Interpolazione di alta qualità che preserva i dettagli stellari
            // BorderConstant + NaN permette di identificare i pixel "fuori campo" nello stack
            Cv2.WarpAffine(
                sourceDouble, 
                result, 
                m, 
                cvSize, 
                InterpolationFlags.Lanczos4, 
                BorderTypes.Constant, 
                new Scalar(double.NaN)
            );
        }
        finally
        {
            if (disposeSource) sourceDouble.Dispose();
        }
        return result;
    }

    // =======================================================================
    // 2. TEMPLATE MATCHING
    // =======================================================================

    public (Mat Template, Point2D RefinedCenter) ExtractRefinedTemplate(FitsImageData? data, Point2D roughGuess, int radius)
    {
        if (data == null) return (new Mat(), roughGuess);

        using Mat fullImg = _converter.RawToMat(data);
        
        // Estrai crop iniziale grezzo attorno al guess
        Rect2D roi = CreateSafeRoi(fullImg, roughGuess, radius);
        if (roi.Width <= 0) throw new InvalidOperationException("ROI non valida (fuori immagine)");

        var cvRoi = new Rect((int)roi.X, (int)roi.Y, (int)roi.Width, (int)roi.Height);
        using Mat crop = new Mat(fullImg, cvRoi);
        
        // Raffina il centro usando l'analisi (Gaussian/Peak) sul crop
        Point2D local = _analysis.FindCenterOfLocalRegion(crop);
        Point2D global = new Point2D(local.X + roi.X, local.Y + roi.Y);

        // Estrai il template definitivo centrato sul punto raffinato
        Rect2D templRect = CreateSafeRoi(fullImg, global, radius);
        var cvTemplRect = new Rect((int)templRect.X, (int)templRect.Y, (int)templRect.Width, (int)templRect.Height);
        
        using Mat tempRaw = new Mat(fullImg, cvTemplRect);
        
        // Normalizza per il matching (Float 0..1)
        Mat templateF = NormalizeAndConvertToFloat(tempRaw);
        
        return (templateF, global);
    }

    public Point2D? FindTemplatePosition(Mat fullImage, Mat templateF, Point2D expectedCenter, int searchRadius)
    {
        if (fullImage.Empty() || templateF.Empty()) return null;

        // Area di ricerca limitata per velocità e robustezza (evita falsi positivi lontani)
        int searchW = searchRadius * 3;
        Rect2D searchRect = CreateSafeRoi(fullImage, expectedCenter, searchW);
        
        if (searchRect.Width <= templateF.Width || searchRect.Height <= templateF.Height) return null;

        var cvSearchRect = new Rect((int)searchRect.X, (int)searchRect.Y, (int)searchRect.Width, (int)searchRect.Height);
        using Mat searchRegion = new Mat(fullImage, cvSearchRect);
        using Mat searchF = NormalizeAndConvertToFloat(searchRegion);
        
        using Mat res = new Mat();
        // Cross Correlation Normalizzata
        Cv2.MatchTemplate(searchF, templateF, res, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out Point maxLoc);

        // Soglia di confidenza minima
        if (maxVal < 0.5) return null;

        // Coordinate relative al searchRegion
        double matchCx = maxLoc.X + (templateF.Width / 2.0);
        double matchCy = maxLoc.Y + (templateF.Height / 2.0);
        Point2D roughLocal = new Point2D(matchCx, matchCy);

        // Ultimo step: raffinamento sub-pixel locale sulla regione trovata
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

        // Coordinate globali
        return new Point2D(subPixelCenter.X + searchRect.X, subPixelCenter.Y + searchRect.Y);
    }

    // =======================================================================
    // 3. STACKING
    // =======================================================================

    public async Task<FitsImageData> ComputeStackAsync(List<FitsImageData> sources, StackingMode mode)
    {
        if (sources == null || sources.Count == 0) throw new ArgumentException("Nessuna immagine da stackare");
        
        var refData = sources[0];
        int width = refData.Width;
        int height = refData.Height;
        using Mat resultMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));

        await Task.Run(() =>
        {
            if (mode == StackingMode.Sum || mode == StackingMode.Average)
            {
                // ... (Logica Somma/Media invariata, funziona correttamente) ...
                using Mat validCountMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));
                foreach (var sourceData in sources)
                {
                    using Mat currentMat = _converter.RawToMat(sourceData);
                    using Mat nonNanMask = new Mat();
                    Cv2.Compare(currentMat, currentMat, nonNanMask, CmpType.EQ);
                    Cv2.Add(resultMat, currentMat, resultMat, mask: nonNanMask);
                    using Mat onesMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(1));
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
                int stripHeight = 50; 
                for (int yStart = 0; yStart < height; yStart += stripHeight)
                {
                    int currentStripH = Math.Min(stripHeight, height - yStart);
                    Mat[] stripStack = new Mat[sources.Count];
                    
                    try
                    {
                        Parallel.For(0, sources.Count, i => stripStack[i] = _converter.RawToMatRect(sources[i], yStart, currentStripH));
                        
                        Parallel.For(0, currentStripH, 
                            () => new 
                            { 
                                // FIX: Inizializziamo correttamente gli array di supporto
                                SrcRows = new double[sources.Count][], 
                                DestRow = new double[width],
                                PixelValues = new double[sources.Count]
                            },
                            (yRel, _, buffers) =>
                            {
                                for(int k = 0; k < sources.Count; k++)
                                {
                                    // FIX: Controllo nullità prima di accedere a Length
                                    if(buffers.SrcRows[k] == null || buffers.SrcRows[k].Length < width)
                                        buffers.SrcRows[k] = new double[width];

                                    IntPtr srcPtr = stripStack[k].Ptr(yRel);
                                    Marshal.Copy(srcPtr, buffers.SrcRows[k], 0, width);
                                }

                                for (int x = 0; x < width; x++)
                                {
                                    int validCount = 0;
                                    for (int k = 0; k < sources.Count; k++)
                                    {
                                        double val = buffers.SrcRows[k][x];
                                        if (!double.IsNaN(val)) buffers.PixelValues[validCount++] = val;
                                    }

                                    if (validCount == 0) {
                                        buffers.DestRow[x] = double.NaN;
                                        continue;
                                    }

                                    // Calcolo mediana
                                    Array.Sort(buffers.PixelValues, 0, validCount);
                                    buffers.DestRow[x] = (validCount % 2 == 0) 
                                        ? (buffers.PixelValues[validCount / 2 - 1] + buffers.PixelValues[validCount / 2]) / 2.0 
                                        : buffers.PixelValues[validCount / 2];
                                }

                                IntPtr destPtr = resultMat.Ptr(yStart + yRel);
                                Marshal.Copy(buffers.DestRow, 0, destPtr, width);
                                return buffers;
                            },
                            _ => { }
                        );
                    }
                    finally { foreach (var m in stripStack) m?.Dispose(); }
                }
            }
        });

        var resultData = _converter.MatToFitsData(resultMat);
        _metadataService.TransferMetadata(refData.FitsHeader, resultData.FitsHeader);
        resultData.FitsHeader.AddCard(new nom.tam.fits.HeaderCard("HISTORY", $"Stacked using {mode} method from {sources.Count} frames", null));

        return resultData;
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