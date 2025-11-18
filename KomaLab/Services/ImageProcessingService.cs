using KomaLab.Models;
using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Statistics;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nom.tam.fits;
using Point = Avalonia.Point;

namespace KomaLab.Services;

/// <summary>
/// Implementazione del servizio di calcolo scientifico.
/// Gestisce Image Processing (OpenCV) e Fitting Matematico (MathNet).
/// </summary>
public class ImageProcessingService : IImageProcessingService
{
    // =======================================================================
    // 1. WORKFLOW INTELLIGENTI (High Level Processing)
    // =======================================================================
    
    public Point GetCenterOfLocalRegion(Mat regionMat)
    {
        if (regionMat.Empty())
        {
            Debug.WriteLine("[LocalRegion] ERRORE: Matrice di input vuota.");
            return new Point(0, 0);
        }
        
        using Mat workingMat = new Mat();
        if (regionMat.Type() != MatType.CV_64FC1)
            regionMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else
            regionMat.CopyTo(workingMat);

        // 1. Statistica Robusta (su workingMat float)
        using Mat statsMat = new Mat();
        Cv2.GaussianBlur(workingMat, statsMat, new Size(3, 3), 0);
        Cv2.MinMaxLoc(statsMat, out double minVal, out double maxVal);

        double dynamicRange = maxVal - minVal;

        if (dynamicRange <= 1e-6) 
        {
            Debug.WriteLine("[LocalRegion] FALLBACK: Immagine piatta. Ritorno centro geometrico.");
            return new Point(workingMat.Width / 2.0, workingMat.Height / 2.0);
        }

        // 2. Parametri Dinamici
        double threshold = minVal + (dynamicRange * 0.2); 
        int minArea = 5; 

        Debug.WriteLine($"[LocalRegion] Statistiche: Min={minVal:F2}, Max={maxVal:F2}, Soglia={threshold:F2}");

        // 3. Blob Detection
        using Mat maskF = new Mat();
        Cv2.Threshold(workingMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
        
        using Mat mask8U = new Mat();
        maskF.ConvertTo(mask8U, MatType.CV_8UC1, 255.0);

        using Mat labels = new Mat();
        using Mat stats = new Mat(); 
        using Mat centroids = new Mat();
        int numFeatures = Cv2.ConnectedComponentsWithStats(mask8U, labels, stats, centroids);

        if (numFeatures <= 1) 
        {
            Debug.WriteLine("[LocalRegion] FALLBACK: Nessuna feature trovata sopra la soglia.");
            return new Point(workingMat.Width / 2.0, workingMat.Height / 2.0);
        }
        
        int bestLabel = -1;
        int maxArea = -1;
        for (int i = 1; i < numFeatures; i++)
        {
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area >= minArea && area > maxArea) { maxArea = area; bestLabel = i; }
        }

        if (bestLabel == -1) 
        {
            Debug.WriteLine($"[LocalRegion] FALLBACK: Nessuna feature supera l'area minima di {minArea} pixel.");
            return new Point(workingMat.Width / 2.0, workingMat.Height / 2.0);
        }
        
        // 4. Crop e Padding
        int bX = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Left);
        int bY = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Top);
        int bW = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Width);
        int bH = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Height);
        
        int paddingX = Math.Max(2, (int)(bW * 0.25));
        int paddingY = Math.Max(2, (int)(bH * 0.25));
        int pX = Math.Max(bX - paddingX, 0);
        int pY = Math.Max(bY - paddingY, 0);
        int pW = Math.Min(bX + bW + paddingX, workingMat.Cols) - pX;
        int pH = Math.Min(bY + bH + paddingY, workingMat.Rows) - pY;

        Rect starRect = new Rect(pX, pY, pW, pH);
        // starCrop ora è un crop di 'workingMat', quindi è già Float
        using Mat starCrop = new Mat(workingMat, starRect);
        
        // 5. Calcolo
        Point centerInStarCrop;
        try
        {
            // starCrop è Float, quindi GaussianFit non crasherà
            centerInStarCrop = GetCenterByGaussianFit(starCrop);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalRegion] FALLBACK: {ex.Message}");
            centerInStarCrop = new Point(starRect.Width / 2.0, starRect.Height / 2.0);
        }

        return new Point(centerInStarCrop.X + starRect.X, centerInStarCrop.Y + starRect.Y);
    }
    
    // =======================================================================
    // 2. ALGORITMI DI CENTRAGGIO (Math Core)
    // =======================================================================
    
    public Point GetCenterByGaussianFit(Mat rawMat, double sigma = 3.0)
    {
        if (rawMat.Empty()) return new Point(-1, -1);

        // 1. PROMOZIONE A FLOAT PRIMA DI TUTTO
        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1)
             rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else
             rawMat.CopyTo(workingMat);

        // 2. Pre-processing (Blur su Float)
        using Mat smoothedMat = new Mat();
        if (sigma > 0)
            Cv2.GaussianBlur(workingMat, smoothedMat, new Size(0, 0), sigma, sigma);
        else
            workingMat.CopyTo(smoothedMat);
        
        // 3. Soglia Automatica (FWHM)
        Cv2.MinMaxLoc(smoothedMat, out double minVal, out double maxVal);
        double dynamicRange = maxVal - minVal;

        if (dynamicRange <= 1e-6) 
            return GetCenterByPeak(smoothedMat, sigma: 0); 

        double threshold = minVal + (dynamicRange * 0.5); 

        // 4. Estrazione Punti
        using Mat maskF = new Mat();
        Cv2.Threshold(smoothedMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
        
        using Mat mask8U = new Mat();
        maskF.ConvertTo(mask8U, MatType.CV_8UC1, 255.0);
        
        // Ottimizzazione FindNonZero corretta
        using Mat locationsMat = new Mat();
        Cv2.FindNonZero(mask8U, locationsMat);
        locationsMat.GetArray(out OpenCvSharp.Point[] locations);

        if (locations.Length < 6) return GetCenterByPeak(smoothedMat, sigma: 0);

        List<double[]> xDataList = new List<double[]>(locations.Length);
        List<double> yDataList = new List<double>(locations.Length);
        
        // smoothedMat è SEMPRE CV_32FC1 qui
        var indexer = smoothedMat.GetGenericIndexer<float>();
        foreach (var p in locations)
        {
            xDataList.Add(new double[] { p.X, p.Y });
            yDataList.Add(indexer[p.Y, p.X]);
        }
        
        // 5. MathNet Fit
        double[][] xData = xDataList.ToArray();
        double[] yData = yDataList.ToArray();
        
        double xoInitNum = 0, yoInitNum = 0, weightSum = 0;
        for(int i = 0; i < yData.Length; i++)
        {
            double w = Math.Max(0, yData[i] - minVal); 
            xoInitNum += xData[i][0] * w;
            yoInitNum += xData[i][1] * w;
            weightSum += w;
        }
        if (weightSum <= 1e-9) return GetCenterByPeak(smoothedMat, sigma: 0);

        var initialGuess = Vector<double>.Build.Dense([
            dynamicRange, xoInitNum / weightSum, yoInitNum / weightSum, 
            3.0, 3.0, minVal
        ]);
        
        var lowerBounds = Vector<double>.Build.Dense([0, 0, 0, 0.1, 0.1, -double.MaxValue]);
        var upperBounds = Vector<double>.Build.Dense([double.MaxValue, smoothedMat.Width, smoothedMat.Height, smoothedMat.Width, smoothedMat.Height, double.MaxValue]);

        try
        {
            double minSig = yData.Where(y => y > 0).DefaultIfEmpty(1.0).Min();
            var weightsVector = Vector<double>.Build.Dense(yData.Length, i => 1.0 / Math.Sqrt(Math.Max(Math.Abs(yData[i]), minSig)));
            var yDataVector = Vector<double>.Build.Dense(yData);

            Func<Vector<double>, double> objectiveFunc = (p) =>
            {
                double amp = p[0], xo = p[1], yo = p[2], sX = Math.Max(Math.Abs(p[3]), 1e-6), sY = Math.Max(Math.Abs(p[4]), 1e-6), off = p[5];
                var model = Vector<double>.Build.Dense(xData.Length, i => 
                {
                    double dx = (xData[i][0] - xo);
                    double dy = (xData[i][1] - yo);
                    return off + amp * Math.Exp(-0.5 * ((dx*dx)/(sX*sX) + (dy*dy)/(sY*sY)));
                });
                return (yDataVector - model).PointwiseMultiply(weightsVector).L2Norm();
            };

            var result = FindMinimum.OfFunctionConstrained(objectiveFunc, lowerBounds, upperBounds, initialGuess, maxIterations: 100);
            
            if (double.IsNaN(result[1]) || double.IsNaN(result[2])) throw new Exception("NaN Fit");
            return new Point(result[1], result[2]);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GaussianFit Fallback: {ex.Message}");
            using Mat mask8UFallback = new Mat();
            maskF.ConvertTo(mask8UFallback, MatType.CV_8UC1, 255.0);
            using Mat masked = Mat.Zeros(smoothedMat.Size(), smoothedMat.Type());
            smoothedMat.CopyTo(masked, mask8UFallback);
            return GetCenterByPeak(masked, sigma: 0);
        }
    }

    public Point GetCenterByPeak(Mat rawMat, double sigma = 1.0) 
    {
        if (rawMat.Empty()) return new Point(-1, -1);
        
        // 1. PROMOZIONE A FLOAT PRIMA DI TUTTO
        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1)
             rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else
             rawMat.CopyTo(workingMat);
             
        // 2. Blur su Float
        using Mat blurredMat = new Mat();
        if (sigma > 0)
            Cv2.GaussianBlur(workingMat, blurredMat, new Size(0, 0), sigma, sigma);
        else
            workingMat.CopyTo(blurredMat);
        
        Cv2.MinMaxLoc(blurredMat, out _, out _, out _, out OpenCvSharp.Point maxLoc);
        int x0 = maxLoc.X;
        int y0 = maxLoc.Y;

        if (y0 <= 0 || x0 <= 0 || y0 >= blurredMat.Rows - 1 || x0 >= blurredMat.Cols - 1)
            return new Point(x0, y0); 
        
        try
        {
            var idx = blurredMat.GetGenericIndexer<float>();
            float c = idx[y0, x0], r = idx[y0, x0+1], l = idx[y0, x0-1], u = idx[y0-1, x0], d = idx[y0+1, x0];
            double dxx = r + l - 2*c;
            double dyy = d + u - 2*c;
            double subX = x0, subY = y0;
            if (Math.Abs(dxx) >= 1e-6) subX = x0 - ((r - l) / 2.0) / dxx;
            if (Math.Abs(dyy) >= 1e-6) subY = y0 - ((d - u) / 2.0) / dyy;
            return new Point(subX, subY);
        }
        catch { return new Point(x0, y0); }
    }

    public Point GetCenterByCentroid(Mat rawMat, double sigma = 5.0)
    {
        if (rawMat.Empty()) return new Point(-1, -1);
        
        // 1. PROMOZIONE A FLOAT PRIMA DI TUTTO
        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1)
             rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else
             rawMat.CopyTo(workingMat);

        // 2. Blur su Float
        using Mat blurredMat = new Mat();
        if (sigma > 0)
            Cv2.GaussianBlur(workingMat, blurredMat, new Size(0, 0), sigma, sigma);
        else
            workingMat.CopyTo(blurredMat);

        Moments moments = Cv2.Moments(blurredMat, binaryImage: false);

        if (moments.M00 != 0)
            return new Point(moments.M10 / moments.M00, moments.M01 / moments.M00);
    
        return new Point(blurredMat.Width / 2.0, blurredMat.Height / 2.0);
    }
    

    // =======================================================================
    // 3. TRASFORMAZIONI GEOMETRICHE (Warp & Shift)
    // =======================================================================

    public Mat GetSubPixelCenteredCanvas(Mat source, Point originalCenter, Size outputSize)
    {
        double destCenterX = outputSize.Width / 2.0;
        double destCenterY = outputSize.Height / 2.0;
    
        double tx = destCenterX - originalCenter.X;
        double ty = destCenterY - originalCenter.Y;
    
        // Usa CV_64FC1 (Double) anche per la matrice di trasformazione
        using Mat m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, 1.0); m.Set(0, 1, 0.0); m.Set(0, 2, tx);
        m.Set(1, 0, 0.0); m.Set(1, 1, 1.0); m.Set(1, 2, ty);
    
        // Crea il risultato direttamente in Double, inizializzato a NaN (trasparente per i FITS)
        Mat result = new Mat(outputSize, MatType.CV_64FC1, new Scalar(double.NaN));
        
        using Mat sourceDouble = new Mat();
        if (source.Type() != MatType.CV_64FC1)
            source.ConvertTo(sourceDouble, MatType.CV_64FC1);
        else
            source.CopyTo(sourceDouble);
    
        // --- MIGLIORAMENTO: Usa Lanczos4 invece di Cubic ---
        // Lanczos4 è molto meglio per preservare i dettagli puntiformi (stelle) 
        // ed evita l'effetto "blur" causato dallo shift sub-pixel su dati interi.
        Cv2.WarpAffine(
            sourceDouble, 
            result, 
            m, 
            outputSize, 
            InterpolationFlags.Lanczos4, 
            BorderTypes.Transparent, 
            new Scalar(double.NaN)
        );

        return result;
    }
    
    public Rect FindValidDataBox(Mat imageMat)
    {
        using Mat nanMask = new Mat();
        
        if (imageMat.Depth() != MatType.CV_32F && imageMat.Depth() != MatType.CV_64F)
            return new Rect(0, 0, imageMat.Width, imageMat.Height);

        Cv2.Compare(imageMat, imageMat, nanMask, CmpType.EQ);
        Rect validDataBox = Cv2.BoundingRect(nanMask);

        if (validDataBox.Width <= 0 || validDataBox.Height <= 0) return new Rect();
        return validDataBox;
    }

    // =======================================================================
    // 4. FITS I/O & CONVERSIONE (Bridge)
    // =======================================================================

    public Mat LoadFitsDataAsMat(FitsImageData fitsData)
    {
        if (fitsData == null) return new Mat();

        var rawJaggedData = (Array[])fitsData.RawData;
        int width = (int)fitsData.ImageSize.Width;
        int height = (int)fitsData.ImageSize.Height;
        int bitpix = fitsData.FitsHeader.GetIntValue("BITPIX");

        // 1. RECUPERO PARAMETRI DI CORREZIONE FISICA
        // Se non ci sono, i default sono: scala=1, zero=0
        double bscale = fitsData.FitsHeader.GetDoubleValue("BSCALE", 1.0);
        double bzero = fitsData.FitsHeader.GetDoubleValue("BZERO", 0.0);

        Mat imageMat = new Mat(); // Matrice finale (Double)

        switch (bitpix)
        {
            // --- CASO FLOAT/DOUBLE ---
            // Di solito hanno bscale=1 e bzero=0, ma li applichiamo per sicurezza.
            case -32: 
                float[] flatDataF = ConvertJaggedToFlat<float>(rawJaggedData, width, height);
                using (Mat tempF = new Mat(height, width, MatType.CV_32FC1))
                {
                    tempF.SetArray(flatDataF);
                    // Converte a Double e applica la formula: Valore = (Raw * bscale) + bzero
                    tempF.ConvertTo(imageMat, MatType.CV_64FC1, bscale, bzero);
                }
                break;
                
            case -64:
                double[] flatDataD = ConvertJaggedToFlat<double>(rawJaggedData, width, height);
                using (Mat tempD = new Mat(height, width, MatType.CV_64FC1))
                {
                    tempD.SetArray(flatDataD);
                    tempD.ConvertTo(imageMat, MatType.CV_64FC1, bscale, bzero);
                }
                break;

            // --- CASO INTERI (Il colpevole dello sfarfallio) ---
            // Qui bzero corregge i valori negativi trasformandoli in positivi corretti.
            
            case 8:
                byte[] flatDataB = ConvertJaggedToFlat<byte>(rawJaggedData, width, height);
                using (Mat temp8 = new Mat(height, width, MatType.CV_8UC1))
                {
                    temp8.SetArray(flatDataB);
                    temp8.ConvertTo(imageMat, MatType.CV_64FC1, bscale, bzero);
                }
                break;

            case 16:
                short[] flatDataS = ConvertJaggedToFlat<short>(rawJaggedData, width, height);
                using (Mat temp16 = new Mat(height, width, MatType.CV_16SC1))
                {
                    temp16.SetArray(flatDataS);
                    // Esempio critico: Se il pixel è -32768 e bzero è 32768 -> diventa 0.0
                    temp16.ConvertTo(imageMat, MatType.CV_64FC1, bscale, bzero);
                }
                break;

            case 32:
                int[] flatDataI = ConvertJaggedToFlat<int>(rawJaggedData, width, height);
                using (Mat temp32 = new Mat(height, width, MatType.CV_32SC1))
                {
                    temp32.SetArray(flatDataI);
                    temp32.ConvertTo(imageMat, MatType.CV_64FC1, bscale, bzero);
                }
                break;

            case 64: // Long (Raro, fallback manuale)
                imageMat = new Mat(height, width, MatType.CV_64FC1);
                var indexer = imageMat.GetGenericIndexer<double>();
                for(int y = 0; y < height; y++) 
                {
                    long[] row = (long[])rawJaggedData[y];
                    for(int x = 0; x < width; x++) 
                    {
                        // Applichiamo manualmente la formula
                        indexer[y, x] = ((double)row[x] * bscale) + bzero;
                    }
                }
                break;

            default:
                Debug.WriteLine($"LoadFitsDataAsMat: BITPIX non supportato {bitpix}");
                break;
        }

        return imageMat;
    }
    
    public FitsImageData CreateFitsDataFromMat(Mat mat, FitsImageData originalData)
    {
        int width = mat.Width;
        int height = mat.Height;
        Array[] jaggedData = new Array[height];
        
        // --- FIXED: Salviamo SEMPRE come Double (BITPIX -64) per preservare la qualità ---
        
        using Mat temp = new Mat();
        if (mat.Type() != MatType.CV_64FC1)
                mat.ConvertTo(temp, MatType.CV_64FC1);
        else
                mat.CopyTo(temp);

        var indexer = temp.GetGenericIndexer<double>();
        for (int j = 0; j < height; j++) 
        {
            var row = new double[width];
            for (int i = 0; i < width; i++)
            {
                row[i] = indexer[j, i];
            }
            jaggedData[j] = row;
        }
        
        // Update Header
        var newHeader = new Header();
        foreach (object item in originalData.FitsHeader)
        {
            if (item is System.Collections.DictionaryEntry entry && entry.Value is HeaderCard card)
            {
                if (card.Key.ToUpper() == "BITPIX") continue; 
                newHeader.AddCard(card);
            }
        }
        // Forziamo BITPIX -64 (Double)
        newHeader.AddCard(new HeaderCard("BITPIX", -64, "Updated by Alignment (Double Precision)"));
        
        return new FitsImageData
        {
            RawData = jaggedData,
            FitsHeader = newHeader,
            ImageSize = new Avalonia.Size(width, height)
        };
    }

    public (double BlackPoint, double WhitePoint) CalculateClippedThresholds(FitsImageData fitsData)
    {
        int bitpix = fitsData.FitsHeader.GetIntValue("BITPIX");
        var jagged = (Array[])fitsData.RawData;

        // 1. RECUPERA I FATTORI DI CORREZIONE
        // FITS standard: se mancano, scale=1 e zero=0
        double bscale = fitsData.FitsHeader.GetDoubleValue("BSCALE", 1.0);
        double bzero = fitsData.FitsHeader.GetDoubleValue("BZERO", 0.0);

        // 2. Passali al calcolatore
        return CalculateQuantilesBySampling(jagged, bitpix, bscale, bzero);
    }
    
    // --- Helpers Privati ---

    private (double, double) CalculateQuantilesBySampling(
        Array[] jaggedData, 
        int bitpix, 
        double bscale, 
        double bzero, 
        int maxSamples = 20000)
    {
        if (jaggedData.Length == 0) return (0, 255);

        int height = jaggedData.Length;
        int width = jaggedData[0].Length;
        long totalPixels = (long)height * width;
    
        var samples = new List<double>(maxSamples);
        // Calcola un passo sicuro per saltare i pixel
        double step = Math.Max(1.0, totalPixels / (double)maxSamples);

        for (int i = 0; i < maxSamples; i++)
        {
            long pixelIndex = (long)(i * step); 
            if (pixelIndex >= totalPixels) break;

            int y = (int)(pixelIndex / width);
            int x = (int)(pixelIndex % width);

            try
            {
                // 1. Leggi il valore RAW (che per gli interi può essere negativo!)
                double rawVal = bitpix switch
                {
                    8 => ((byte[])jaggedData[y])[x],
                    16 => ((short[])jaggedData[y])[x],
                    32 => ((int[])jaggedData[y])[x],
                    -32 => ((float[])jaggedData[y])[x],
                    -64 => ((double[])jaggedData[y])[x],
                    64 => ((long[])jaggedData[y])[x],
                    _ => 0
                };
            
                // 2. APPLICA LA CORREZIONE FISICA
                // Trasforma il valore raw (es: -32768) nel valore fisico (es: 0.0)
                // Ora questo valore è COERENTE con quello che vede OpenCV nella Matrice.
                double physicalVal = (rawVal * bscale) + bzero;

                if (!double.IsNaN(physicalVal) && !double.IsInfinity(physicalVal))
                    samples.Add(physicalVal);
            }
            catch { }
        }
    
        return CalculateQuantiles(samples);
    }

    private (double, double) GetPercentilesFull<T>(T[][] data) where T : struct, IConvertible
    {
        var values = data.SelectMany(row => row.Select(p => Convert.ToDouble(p))
                         .Where(v => !double.IsNaN(v) && !double.IsInfinity(v) && v != 0));
        return CalculateQuantiles(values);
    }
    
    private (double, double) CalculateQuantiles(IEnumerable<double> pixelValues)
    {
        var list = pixelValues.ToList();
        if (list.Count == 0) return (0, 255);
        
        double b = Statistics.Quantile(list, 0.02);
        double w = Statistics.Quantile(list, 0.998);
        return (w <= b) ? (list.Min(), list.Max()) : (b, w);
    }

    private T[][] ConvertJaggedArray<T>(Array[] source) where T : struct
    {
        T[][] result = new T[source.Length][];
        for (int i = 0; i < source.Length; i++) result[i] = (T[])source[i];
        return result;
    }
    
    private T[] ConvertJaggedToFlat<T>(Array[] jaggedArray, int width, int height) where T : struct
    {
        T[] flatArray = new T[width * height];
        int k = 0;
        for (int j = 0; j < height; j++)
        {
            Array.Copy((T[])jaggedArray[j], 0, flatArray, k, width);
            k += width;
        }
        return flatArray;
    }
    
    public (Mat template, Point preciseCenter) ExtractRefinedTemplate(FitsImageData? data, Point roughGuess, int radius)
    {
        using Mat fullImg = LoadFitsDataAsMat(data);
        
        Rect roi = CreateSafeRoi(fullImg, roughGuess, radius);
        if (roi.Width <= 0) throw new Exception("ROI non valida per estrazione template");
        
        // 1. Trova centro preciso localmente
        using Mat crop = new Mat(fullImg, roi);
        Point local = GetCenterOfLocalRegion(crop);
        Point global = new Point(local.X + roi.X, local.Y + roi.Y);
        
        // 2. Estrai Template (Clonato per persistenza fuori dal using)
        Rect templRect = CreateSafeRoi(fullImg, global, radius);
        using Mat tempRaw = new Mat(fullImg, templRect);
        
        // Normalizziamo subito qui per l'uso nel matching
        Mat templateF = NormalizeAndConvertToFloat(tempRaw);
        
        return (templateF, global);
    }

    public Point? FindTemplatePosition(Mat fullImage, Mat templateF, Point expectedCenter, int searchRadius)
    {
        // A. Area di ricerca estesa
        int searchW = searchRadius * 3;
        Rect searchRect = CreateSafeRoi(fullImage, expectedCenter, searchW);

        // Check bordi
        if (searchRect.Width <= templateF.Width || searchRect.Height <= templateF.Height)
            return null; // Fallback gestito dal chiamante

        using Mat searchRegion = new Mat(fullImage, searchRect);
        using Mat searchF = NormalizeAndConvertToFloat(searchRegion);
        using Mat res = new Mat();

        // B. Match Template
        Cv2.MatchTemplate(searchF, templateF, res, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

        if (maxVal < 0.5) return null; // Match fallito

        // C. Calcolo posizione
        double matchCx = maxLoc.X + (templateF.Width / 2.0);
        double matchCy = maxLoc.Y + (templateF.Height / 2.0);
        
        // D. Raffinamento Sub-Pixel (opzionale ma consigliato)
        Point roughLocal = new Point(matchCx, matchCy);
        Rect refineRect = CreateSafeRoi(searchRegion, roughLocal, searchRadius); // Usa raggio originale per raffinare
        
        Point subPixelCenter;
        if (refineRect.Width > 0 && refineRect.Height > 0)
        {
            using Mat refineCrop = new Mat(searchRegion, refineRect);
            Point localRefined = GetCenterOfLocalRegion(refineCrop); // Riutilizza la logica robusta
            subPixelCenter = new Point(localRefined.X + refineRect.X, localRefined.Y + refineRect.Y);
        }
        else
        {
            subPixelCenter = roughLocal;
        }

        // E. Globalizzazione
        return new Point(subPixelCenter.X + searchRect.X, subPixelCenter.Y + searchRect.Y);
    }

    // Helper resi privati o interni al service
    private Mat NormalizeAndConvertToFloat(Mat source)
    {
        Mat floatMat = new Mat();
        MatType targetType = (source.Type() == MatType.CV_64FC1) ? MatType.CV_64FC1 : MatType.CV_32FC1;
    
        source.ConvertTo(floatMat, targetType); 
        Cv2.Normalize(floatMat, floatMat, 0, 1, NormTypes.MinMax);
    
        return floatMat;
    }

    private Rect CreateSafeRoi(Mat mat, Point center, int radius)
    {
        int size = radius * 2;
        int x = (int)(center.X - radius);
        int y = (int)(center.Y - radius);
        int sx = Math.Max(0, x);
        int sy = Math.Max(0, y);
        int sw = Math.Min(size, mat.Width - sx);
        int sh = Math.Min(size, mat.Height - sy);
        return new Rect(sx, sy, sw, sh);
    }
}