using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models;
using OpenCvSharp;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;
using Size = Avalonia.Size;

namespace KomaLab.Services;

public class ImageOperationService : IImageOperationService
{
    private readonly IFitsDataConverter _converter;
    private readonly IImageAnalysisService _analysis;

    public ImageOperationService(
        IFitsDataConverter converter, 
        IImageAnalysisService analysis)
    {
        _converter = converter;
        _analysis = analysis;
    }

    // =======================================================================
    // 1. WARPING (Spostamento Sub-pixel)
    // =======================================================================

    public Mat GetSubPixelCenteredCanvas(Mat source, Point originalCenter, Size outputSize)
    {
        double destCenterX = outputSize.Width / 2.0;
        double destCenterY = outputSize.Height / 2.0;
    
        double tx = destCenterX - originalCenter.X;
        double ty = destCenterY - originalCenter.Y;
    
        // Matrice di trasformazione Affine (Double)
        using Mat m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, 1.0); m.Set(0, 1, 0.0); m.Set(0, 2, tx);
        m.Set(1, 0, 0.0); m.Set(1, 1, 1.0); m.Set(1, 2, ty);
    
        // Risultato inizializzato a NaN
        // CAST INT: OpenCvSharp.Size vuole int, Avalonia.Size ha double
        var cvSize = new OpenCvSharp.Size((int)outputSize.Width, (int)outputSize.Height);
        
        Mat result = new Mat(cvSize, MatType.CV_64FC1, new Scalar(double.NaN));
        
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
            Cv2.WarpAffine(
                sourceDouble, 
                result, 
                m, 
                cvSize, // Usa la size castata
                InterpolationFlags.Lanczos4, 
                BorderTypes.Transparent, 
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

    public (Mat template, Point preciseCenter) ExtractRefinedTemplate(FitsImageData? data, Point roughGuess, int radius)
    {
        if (data == null) return (new Mat(), roughGuess);

        using Mat fullImg = _converter.RawToMat(data);
        
        // Rect è Avalonia.Rect (double)
        Rect roi = CreateSafeRoi(fullImg, roughGuess, radius);
        if (roi.Width <= 0) throw new Exception("ROI non valida per estrazione template");
        
        // 1. Trova centro preciso localmente
        // CAST INT: Creiamo OpenCvSharp.Rect partendo da Avalonia.Rect
        var cvRoi = new OpenCvSharp.Rect((int)roi.X, (int)roi.Y, (int)roi.Width, (int)roi.Height);
        
        using Mat crop = new Mat(fullImg, cvRoi);
        Point local = _analysis.FindCenterOfLocalRegion(crop);
        Point global = new Point(local.X + roi.X, local.Y + roi.Y);
        
        // 2. Estrai Template Definitivo
        Rect templRect = CreateSafeRoi(fullImg, global, radius);
        var cvTemplRect = new OpenCvSharp.Rect((int)templRect.X, (int)templRect.Y, (int)templRect.Width, (int)templRect.Height);
        
        // Matrice restituita al chiamante (non usiamo 'using' qui)
        // Nota: se Normalizzi, crei una copia, quindi puoi fare 'using' sulla raw
        using Mat tempRaw = new Mat(fullImg, cvTemplRect);
        Mat templateF = NormalizeAndConvertToFloat(tempRaw);
        
        return (templateF, global);
    }

    public Point? FindTemplatePosition(Mat fullImage, Mat templateF, Point expectedCenter, int searchRadius)
    {
        if (fullImage.Empty() || templateF.Empty()) return null;

        // A. Area di ricerca estesa
        int searchW = searchRadius * 3;
        Rect searchRect = CreateSafeRoi(fullImage, expectedCenter, searchW);

        if (searchRect.Width <= templateF.Width || searchRect.Height <= templateF.Height)
            return null; 

        // CAST INT per il ritaglio
        var cvSearchRect = new OpenCvSharp.Rect((int)searchRect.X, (int)searchRect.Y, (int)searchRect.Width, (int)searchRect.Height);

        using Mat searchRegion = new Mat(fullImage, cvSearchRect);
        using Mat searchF = NormalizeAndConvertToFloat(searchRegion);
        using Mat res = new Mat();

        // B. Match Template
        Cv2.MatchTemplate(searchF, templateF, res, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

        if (maxVal < 0.5) return null; 

        // C. Calcolo posizione
        double matchCx = maxLoc.X + (templateF.Width / 2.0);
        double matchCy = maxLoc.Y + (templateF.Height / 2.0);
        
        // D. Raffinamento Sub-Pixel
        Point roughLocal = new Point(matchCx, matchCy);
        Rect refineRect = CreateSafeRoi(searchRegion, roughLocal, searchRadius); 
        
        Point subPixelCenter;
        if (refineRect.Width > 0 && refineRect.Height > 0)
        {
            var cvRefineRect = new OpenCvSharp.Rect((int)refineRect.X, (int)refineRect.Y, (int)refineRect.Width, (int)refineRect.Height);
            using Mat refineCrop = new Mat(searchRegion, cvRefineRect);
            
            // Delega al servizio di analisi
            Point localRefined = _analysis.FindCenterOfLocalRegion(refineCrop); 
            subPixelCenter = new Point(localRefined.X + refineRect.X, localRefined.Y + refineRect.Y);
        }
        else
        {
            subPixelCenter = roughLocal;
        }

        // E. Globalizzazione
        return new Point(subPixelCenter.X + searchRect.X, subPixelCenter.Y + searchRect.Y);
    }

    // =======================================================================
    // 3. STACKING
    // =======================================================================

    public async Task<FitsImageData> ComputeStackAsync(List<FitsImageData> sources, StackingMode mode)
    {
        if (sources == null || sources.Count == 0) 
            throw new ArgumentException("Nessuna immagine da processare");

        var refData = sources[0];
        int width = refData.Width; 
        int height = refData.Height;
        int count = sources.Count;

        using Mat resultMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));

        await Task.Run(() =>
        {
            if (mode == StackingMode.Sum || mode == StackingMode.Average)
            {
                using Mat validCountMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));

                foreach (var sourceData in sources)
                {
                    using Mat currentMat = _converter.RawToMat(sourceData);
                    
                    if (currentMat.Width != width || currentMat.Height != height) continue;

                    using Mat nonNanMask = new Mat();
                    Cv2.Compare(currentMat, currentMat, nonNanMask, CmpType.EQ); 
        
                    Cv2.Add(resultMat, currentMat, resultMat, mask: nonNanMask); 

                    using Mat onesMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(1));
                    Cv2.Add(validCountMat, onesMat, validCountMat, mask: nonNanMask);
                }

                if (mode == StackingMode.Average)
                {
                    Cv2.Divide(resultMat, validCountMat, resultMat, scale: 1, dtype: MatType.CV_64FC1);
                }
            }
            else if (mode == StackingMode.Median)
            {
                Mat[] stack = new Mat[count];
                try 
                {
                    for (int i = 0; i < count; i++)
                    {
                        stack[i] = _converter.RawToMat(sources[i]);
                    }

                    var resIndexer = resultMat.GetGenericIndexer<double>();
                    
                    Parallel.For(0, height, y =>
                    {
                        var valuesToStack = new List<double>(count); 

                        for (int x = 0; x < width; x++)
                        {
                            valuesToStack.Clear(); 

                            for (int k = 0; k < count; k++)
                            {
                                double val = stack[k].At<double>(y, x); 
                                if (!double.IsNaN(val))
                                {
                                    valuesToStack.Add(val);
                                }
                            }

                            if (valuesToStack.Count == 0)
                            {
                                resIndexer[y, x] = double.NaN;
                                continue; 
                            }

                            valuesToStack.Sort(); 

                            double median;
                            int nValid = valuesToStack.Count;
                            if (nValid % 2 == 0)
                            {
                                int mid = nValid / 2;
                                median = (valuesToStack[mid - 1] + valuesToStack[mid]) / 2.0;
                            }
                            else
                            {
                                median = valuesToStack[nValid / 2];
                            }

                            resIndexer[y, x] = median;
                        }
                    });
                }
                finally
                {
                    foreach (var m in stack) m.Dispose();
                }
            }
        });

        return _converter.MatToFitsData(resultMat, refData);
    }

    // --- Helpers Privati ---

    private Mat NormalizeAndConvertToFloat(Mat source)
    {
        Mat floatMat = new Mat();
        source.ConvertTo(floatMat, MatType.CV_32FC1); 
        Cv2.Normalize(floatMat, floatMat, 0, 1, NormTypes.MinMax);
        return floatMat;
    }

    private Rect CreateSafeRoi(Mat mat, Point center, int radius)
    {
        int size = radius * 2;
        // CAST INT: Le coordinate dei pixel sono intere
        int x = (int)(center.X - radius);
        int y = (int)(center.Y - radius);
        int sx = Math.Max(0, x);
        int sy = Math.Max(0, y);
        int sw = Math.Min(size, mat.Width - sx);
        int sh = Math.Min(size, mat.Height - sy);
        
        return new Rect(sx, sy, sw, sh);
    }
}