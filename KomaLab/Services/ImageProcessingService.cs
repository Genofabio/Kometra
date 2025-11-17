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
/// Contiene tutta la logica di OpenCV e Math.NET.
/// </summary>
public class ImageProcessingService : IImageProcessingService
{
    #region Workflow di Image Processing (Alto Livello)

    public Point GetCenterOfLocalRegion(
    FitsImageData fitsData,
    CenteringMethod centerFunc,
    double thresholdRatio = 0.1,
    int minArea = 10,
    int padding = 0)
{
    // 1. Carica i dati FITS (potrebbero avere NaN)
    using Mat imageMat = LoadFitsDataAsMat(fitsData);
    if (imageMat.Empty()) return new Point(fitsData.ImageSize.Width / 2.0, fitsData.ImageSize.Height / 2.0);

    // --- INIZIO REFACTORING (Il Tuo Piano) ---

    // 2. Trova il rettangolo dei dati validi (ignora i NaN esterni)
    Rect validDataBox = FindValidDataBox(imageMat);
    if (validDataBox.Width <= 0 || validDataBox.Height <= 0)
    {
        Debug.WriteLine("LocalRegion: Immagine interamente NaN.");
        return new Point(imageMat.Width / 2.0, imageMat.Height / 2.0);
    }

    // 3. Ritaglia l'immagine per ottenere SOLO i dati validi
    using Mat cleanMat = new Mat(imageMat, validDataBox);

    // --- Da ora in poi, lavoriamo SOLO su 'cleanMat' ---

    // 4. Calcola la mediana (ora non ci sono NaN esterni)
    double medianVal;
    if (cleanMat.Type() == MatType.CV_32FC1)
    {
        cleanMat.GetArray(out float[] data);
        var pixelValues = data.Select(x => (double)x)
                              .Where(val => !double.IsNaN(val) && !double.IsInfinity(val)); // Filtra NaN *interni*
        medianVal = pixelValues.Any() ? Statistics.Median(pixelValues) : 0.0;
    }
    else // Assumiamo CV_64FC1
    {
        cleanMat.GetArray(out double[] data);
        var pixelValues = data.Where(val => !double.IsNaN(val) && !double.IsInfinity(val));
        medianVal = pixelValues.Any() ? Statistics.Median(pixelValues) : 0.0;
    }

    // 5. Calcola soglia
    double threshold = medianVal * (1 + thresholdRatio);

    // 6. Crea maschera binaria (su 'cleanMat')
    using Mat maskF = new Mat();
    Cv2.Threshold(cleanMat, maskF, threshold, 1.0, ThresholdTypes.Binary);

    // 7. Converti maschera in 8-bit
    using Mat mask8U = new Mat();
    maskF.ConvertTo(mask8U, MatType.CV_8UC1, 255.0);

    // 8. Esegui l'analisi dei componenti connessi (su 'mask8U' da 'cleanMat')
    using Mat labels = new Mat();
    using Mat stats = new Mat(); 
    using Mat centroids = new Mat();
    int numFeatures = Cv2.ConnectedComponentsWithStats(mask8U, labels, stats, centroids);

    if (numFeatures <= 1)
    {
        Debug.WriteLine("LocalRegion: Nessuna feature trovata.");
        return new Point(imageMat.Width / 2.0, imageMat.Height / 2.0);
    }
    
    // 9. Trova la regione più grande
    int largestRegionLabel = -1;
    int largestArea = -1;
    for (int i = 1; i < numFeatures; i++)
    {
        int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
        if (area >= minArea && area > largestArea)
        {
            largestArea = area;
            largestRegionLabel = i;
        }
    }

    if (largestRegionLabel == -1)
    {
        Debug.WriteLine("LocalRegion: Nessuna feature sopra l'area minima trovata.");
        return new Point(imageMat.Width / 2.0, imageMat.Height / 2.0);
    }

    // 10. Recupera il Bounding Box (relativo a 'cleanMat')
    int minCol = stats.At<int>(largestRegionLabel, (int)ConnectedComponentsTypes.Left);
    int minRow = stats.At<int>(largestRegionLabel, (int)ConnectedComponentsTypes.Top);
    int w = stats.At<int>(largestRegionLabel, (int)ConnectedComponentsTypes.Width);
    int h = stats.At<int>(largestRegionLabel, (int)ConnectedComponentsTypes.Height);
    
    // 11. Applica padding (relativo a 'cleanMat')
    int minRowP = Math.Max(minRow - padding, 0);
    int maxRowP = Math.Min(minRow + h + padding, cleanMat.Rows);
    int minColP = Math.Max(minCol - padding, 0);
    int maxColP = Math.Min(minCol + w + padding, cleanMat.Cols);
    Rect roiRect = new Rect(minColP, minRowP, maxColP - minColP, maxRowP - minRowP);
    
    // 12. Crea il ritaglio finale (da 'cleanMat').
    //     Questo ritaglio è GARANTITO non contenere NaN.
    using Mat regionCrop = new Mat(cleanMat, roiRect);

    // 14. Applica la sfocatura
    double sigma = 0;
    if (centerFunc == CenteringMethod.Centroid) sigma = 5.0;
    if (centerFunc == CenteringMethod.Peak) sigma = 1.0;
    if (centerFunc == CenteringMethod.GaussianFit) sigma = 3.0;
    
    Mat preparedMat;
    if (sigma > 0)
    {
        preparedMat = new Mat();
        Cv2.GaussianBlur(regionCrop, preparedMat, new OpenCvSharp.Size(0, 0), sigmaX: sigma, sigmaY: sigma);
    }
    else
    {
        preparedMat = regionCrop.Clone();
    }
    
    // 15. Chiama la funzione di centraggio "stupida"
    Point cropCenter; // (coordinata relativa a 'regionCrop' / 'preparedMat')
    try
    {
        switch (centerFunc)
        {
            case CenteringMethod.Centroid:
                cropCenter = GetCenterByCentroid(preparedMat);
                break;
            case CenteringMethod.GaussianFit:
                cropCenter = GetCenterByGaussianFit(preparedMat);
                break;
            case CenteringMethod.Peak:
            default:
                cropCenter = GetCenterByPeak(preparedMat);
                break;
        }
    }
    finally
    {
        preparedMat.Dispose();
    }
    
    // 16. Calcola coordinate globali
    //     cropCenter = (x, y) relative a 'regionCrop'
    //     roiRect.X/Y = (x, y) relative a 'cleanMat'
    //     validDataBox.X/Y = (x, y) relative a 'imageMat'
    double yCenter = cropCenter.Y + roiRect.Y + validDataBox.Y;
    double xCenter = cropCenter.X + roiRect.X + validDataBox.X;

    return new Point(xCenter, yCenter);
}

    public Mat CenterImageByCoords(Mat img, Point center)
    {
        if (img == null) throw new ArgumentNullException(nameof(img));

        int w = img.Width;    // colonne
        int h = img.Height;   // righe

        // coordinate del punto da centrare
        double x = center.X;
        double y = center.Y;

        // Centro "geom."
        double cx = (w - 1) / 2.0;
        double cy = (h - 1) / 2.0;

        // Traslazione (la tua logica, è corretta)
        double tx = cx - x;
        double ty = cy - y;

        // Padding totale
        int padXTotal = (int)Math.Ceiling(2.0 * Math.Abs(tx));
        int padYTotal = (int)Math.Ceiling(2.0 * Math.Abs(ty));

        // Nuova dimensione
        int newWidth  = w + padXTotal;
        int newHeight = h + padYTotal;

        // Nuovo centro geometrico
        double cxNew = (newWidth - 1) / 2.0;
        double cyNew = (newHeight - 1) / 2.0;

        // Traslazione finale (dal punto x,y originale al cxNew, cyNew)
        double final_tx = cxNew - x;
        double final_ty = cyNew - y;

        Mat M = new Mat(2, 3, MatType.CV_64FC1);
        M.Set(0, 0, 1.0); M.Set(0, 1, 0.0); M.Set(0, 2, final_tx);
        M.Set(1, 0, 0.0); M.Set(1, 1, 1.0); M.Set(1, 2, final_ty);

        // --- MODIFICA QUI ---
        Scalar paddingValue = new Scalar(double.NaN);
        Mat result = new Mat(newHeight, newWidth, img.Type(), paddingValue);

        Cv2.WarpAffine(img, result, M, new Size(newWidth, newHeight),
            InterpolationFlags.Cubic, 
            BorderTypes.Constant, 
            paddingValue); // Usa NaN
        // --- FINE MODIFICA ---

        // (La tua verifica è ottima per il debug)
        bool ok = VerifyCentering(img.Size(), center, M, result.Size());
        Console.WriteLine(ok ? "✓ Centro corretto" : "✗ Centro NON corretto");

        return result;
    }

public bool VerifyCentering(
    Size originalSize,
    Point originalCenterPoint,
    Mat transformMatrix,
    Size newSize,
    double tolerance = 1e-12
)
{
    double x = originalCenterPoint.X;
    double y = originalCenterPoint.Y;

    double m00 = transformMatrix.At<double>(0, 0);
    double m01 = transformMatrix.At<double>(0, 1);
    double m02 = transformMatrix.At<double>(0, 2);
    double m10 = transformMatrix.At<double>(1, 0);
    double m11 = transformMatrix.At<double>(1, 1);
    double m12 = transformMatrix.At<double>(1, 2);

    double xp = m00 * x + m01 * y + m02;
    double yp = m10 * x + m11 * y + m12;

    double cxNew = (newSize.Width - 1) / 2.0;
    double cyNew = (newSize.Height - 1) / 2.0;

    double dx = xp - cxNew;
    double dy = yp - cyNew;

    Console.WriteLine("----- VERIFY CENTERING -----");
    Console.WriteLine($"Original size:               {originalSize.Width} x {originalSize.Height}");
    Console.WriteLine($"New size:                    {newSize.Width} x {newSize.Height}");
    Console.WriteLine($"Original center declared:    ({x:F6}, {y:F6})");
    Console.WriteLine($"Transformed center:           ({xp:F12}, {yp:F12})");
    Console.WriteLine($"New image geometric center:   ({cxNew:F12}, {cyNew:F12})");
    Console.WriteLine($"Centering error dx:           {dx:E}");
    Console.WriteLine($"Centering error dy:           {dy:E}");
    Console.WriteLine("------------------------------");

    return Math.Abs(dx) <= tolerance && Math.Abs(dy) <= tolerance;
}


    #endregion

    #region Primitivi di Centraggio (Basso Livello)

    public Point GetCenterByCentroid(Mat imageMat, double sigma = 5.0)
    {
        // 1. Dati già pronti (l'immagine è già ritagliata, pulita e sfocata)
        if (imageMat.Empty())
        {
            Debug.WriteLine($"GetCenterByCentroid fallito: Mat vuota.");
            return new Point(-1, -1);
        }
    
        // --- LOGICA RIMOSSA ---
        // 'GaussianBlur' e 'PatchNaNs' non sono più qui.
        // --- FINE ---

        // 2. Calcola il centro di massa
        //    (Assumiamo che 'imageMat' sia già stata sfocata 
        //     da chi l'ha chiamata, se necessario)
        Moments moments = Cv2.Moments(imageMat, binaryImage: false);

        double xCentroid, yCentroid;
        if (moments.M00 != 0)
        {
            xCentroid = moments.M10 / moments.M00;
            yCentroid = moments.M01 / moments.M00;
        }
        else
        {
            xCentroid = imageMat.Width / 2.0;
            yCentroid = imageMat.Height / 2.0;
        }
    
        return new Point(xCentroid, yCentroid);
    }
    
    // In Services/ImageProcessingService.cs

    public Point GetCenterByPeak(Mat matToProcess, double sigma = 1.0) // 'imageMat' rinominato in 'matToProcess'
    {
        // 1. Dati già pronti (ritagliati, puliti con PatchNaNs, e sfocati)
        if (matToProcess.Empty())
        {
            Debug.WriteLine($"GetCenterByPeak fallito: Mat vuota.");
            return new Point(-1, -1);
        }
        
        // --- LOGICA RIMOSSA ---
        // 'CropToValidData' non è più qui.
        // 'GaussianBlur' non è più qui.
        // 'PatchNaNs' non è più qui.
        // --- FINE ---

        // 2. Trova il massimo locale (picco discreto)
        Cv2.MinMaxLoc(matToProcess, out _, out _, out _, out OpenCvSharp.Point maxLoc);
        int x0 = maxLoc.X;
        int y0 = maxLoc.Y;

        // 4. Controlla i bordi
        if (y0 <= 0 || x0 <= 0 || y0 >= matToProcess.Rows - 1 || x0 >= matToProcess.Cols - 1)
        {
            // Non serve 'matToProcess.Dispose()' perché è gestita dal chiamante
            return new Point(x0, y0); // Picco sul bordo
        }
        
        // 5. Fit parabolico 2D (logica invariata)
        double subX, subY;
        var matType = matToProcess.Type();

        try
        {
            if (matType == MatType.CV_32FC1)
            {
                float c = matToProcess.At<float>(y0, x0);
                float r = matToProcess.At<float>(y0, x0 + 1);
                float l = matToProcess.At<float>(y0, x0 - 1);
                float u = matToProcess.At<float>(y0 - 1, x0);
                float d = matToProcess.At<float>(y0 + 1, x0);

                double dx = (r - l) / 2.0;
                double dy = (d - u) / 2.0;
                double dxx = (r + l - 2 * c);
                double dyy = (d + u - 2 * c);

                if (Math.Abs(dxx) < 1e-6 || Math.Abs(dyy) < 1e-6)
                { subX = x0; subY = y0; }
                else
                { subX = x0 - dx / dxx; subY = y0 - dy / dyy; }
            }
            else if (matType == MatType.CV_64FC1)
            {
                double c = matToProcess.At<double>(y0, x0);
                double r = matToProcess.At<double>(y0, x0 + 1);
                double l = matToProcess.At<double>(y0, x0 - 1);
                double u = matToProcess.At<double>(y0 - 1, x0);
                double d = matToProcess.At<double>(y0 + 1, x0);
                
                double dx = (r - l) / 2.0;
                double dy = (d - u) / 2.0;
                double dxx = (r + l - 2 * c);
                double dyy = (d + u - 2 * c);

                if (Math.Abs(dxx) < 1e-6 || Math.Abs(dyy) < 1e-6)
                { subX = x0; subY = y0; }
                else
                { subX = x0 - dx / dxx; subY = y0 - dy / dyy; }
            }
            else
            {
                Debug.WriteLine("GetCenterByPeak: Tipo Mat non supportato. Fallback.");
                subX = x0;
                subY = y0;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Fit parabolico fallito: {ex.Message}. Fallback.");
            subX = x0;
            subY = y0;
        }
        
        // Non c'è 'matToProcess.Dispose()' da chiamare
        
        return new Point(subX, subY);
    }
    
    // In Services/ImageProcessingService.cs

    public Point GetCenterByGaussianFit(Mat smoothedMat, double thresholdRatio = 0.5, double sigma = 3.0)
    {
        // 1. Dati già pronti (ritagliati, puliti con PatchNaNs, e sfocati)
        if (smoothedMat.Empty())
        {
            Debug.WriteLine($"GetCenterByGaussianFit fallito: Mat vuota.");
            return new Point(-1, -1);
        }

        // --- LOGICA RIMOSSA ---
        // 'CropToValidData' non è più qui.
        // 'GaussianBlur' non è più qui.
        // 'PatchNaNs' non è più qui.
        // --- FINE ---

        // 2. Trova MinMax (ora sicuro)
        Cv2.MinMaxLoc(smoothedMat, out double minVal, out double maxVal);
        double threshold = maxVal * thresholdRatio;

        // 3. Maschera (l'immagine è già sfocata)
        using Mat maskF = new Mat();
        Cv2.Threshold(smoothedMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
                
        // 4. Estrazione dati
        List<double[]> xDataList = [];
        List<double> yDataList = [];
        var matType = smoothedMat.Type();
                
        for (int y = 0; y < smoothedMat.Rows; y++)
        {
            for (int x = 0; x < smoothedMat.Cols; x++)
            {
                if (maskF.At<float>(y, x) > 0.5f) // Controlla la maschera
                {
                    xDataList.Add([x, y]);
                    // Prendi i dati dalla Mat sfocata
                    if (matType == MatType.CV_32FC1)
                        yDataList.Add(smoothedMat.At<float>(y, x));
                    else if (matType == MatType.CV_64FC1)
                        yDataList.Add(smoothedMat.At<double>(y, x));
                }
            }
        }
        
        double[][] xData = xDataList.ToArray(); 
        double[] yData = yDataList.ToArray();

        // 5. Ipotesi iniziale
        if (xData.Length < 6) 
        {
            Debug.WriteLine("GetCenterByGaussianFit: Pochi punti. Fallback a GetCenterByPeak.");
            // Chiama la versione 'stupida' di GetCenterByPeak
            return GetCenterByPeak(smoothedMat, sigma: 0); 
        }
                
        double ampInit = yData.Max() - yData.Min();
        double offsetInit = yData.Min();
        double xoInitNum = 0, yoInitNum = 0, weightSum = 0;
        for(int i = 0; i < yData.Length; i++)
        {
            double weight = yData[i];
            xoInitNum += xData[i][0] * weight;
            yoInitNum += xData[i][1] * weight;
            weightSum += weight;
        }
        
        if (Math.Abs(weightSum) < 1e-10) 
        {
            Debug.WriteLine("GetCenterByGaussianFit: Somma pesi zero. Fallback a GetCenterByPeak.");
            // Chiama la versione 'stupida' di GetCenterByPeak
            return GetCenterByPeak(smoothedMat, sigma: 0);
        }
        
        double xoInit = xoInitNum / weightSum;
        double yoInit = yoInitNum / weightSum;
        double sigmaInit = 3.0;

        var initialGuess = Vector<double>.Build.Dense([ampInit, xoInit, yoInit, sigmaInit, sigmaInit, offsetInit]);
                
        var lowerBounds = Vector<double>.Build.Dense([0, 0, 0, 0.1, 0.1, minVal]);
        var upperBounds = Vector<double>.Build.Dense([ampInit * 2, smoothedMat.Width, smoothedMat.Height, smoothedMat.Width, smoothedMat.Height, maxVal]);

        // 6. Esegui il Fitting (logica invariata)
        try
        {
            // ... (Logica WLS, objectiveFunc, FindMinimum.OfFunctionConstrained) ...
            // [Codice omesso per brevità, è identico a prima]
            double minPositiveSignal = yData.Where(y => y > 0).DefaultIfEmpty(1.0).Min();
            double[] weights = new double[yData.Length];
            for (int i = 0; i < yData.Length; i++)
            {
                double variance = Math.Max(yData[i], minPositiveSignal); 
                weights[i] = 1.0 / Math.Sqrt(variance); 
            }
            var yDataVector = Vector<double>.Build.Dense(yData);
            var weightsVector = Vector<double>.Build.Dense(weights); 
            Func<Vector<double>, double> objectiveFunc = (p) =>
            {
                double amp = p[0], xo = p[1], yo = p[2], sigX = p[3], sigY = p[4], offset = p[5];
                if (Math.Abs(sigX) < 1e-6) sigX = 1e-6;
                if (Math.Abs(sigY) < 1e-6) sigY = 1e-6;
                var modelZ = Vector<double>.Build.Dense(xData.Length);
                for (int i = 0; i < xData.Length; i++)
                {
                    double x = xData[i][0];
                    double y = xData[i][1];
                    double dx = (x - xo) / sigX;
                    double dy = (y - yo) / sigY;
                    modelZ[i] = offset + amp * Math.Exp(-0.5 * (dx * dx + dy * dy));
                }
                var residuals = yDataVector - modelZ;
                var weightedResiduals = residuals.PointwiseMultiply(weightsVector);
                return weightedResiduals.L2Norm();
            };
            Vector<double> pFit = FindMinimum.OfFunctionConstrained(
                objectiveFunc,
                lowerBounds,
                upperBounds,
                initialGuess,
                maxIterations: 100
            );
            double xoFit = pFit[1];
            double yoFit = pFit[2];
            // ... (Controllo NaN) ...
            if (double.IsNaN(xoFit) || double.IsNaN(yoFit) ||
                double.IsInfinity(xoFit) || double.IsInfinity(yoFit))
            {
                throw new Exception("Il risultato del Fit non è valido (NaN/Infinity).");
            }
            return new Point(xoFit, yoFit);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetCenterByGaussianFit: Fit fallito ({ex.Message}). Uso fallback.");
            
            // --- MODIFICA FALLBACK ---
            // Il fallback ora deve chiamare GetCenterByPeak sulla Mat
            // già sfocata che abbiamo ricevuto ('smoothedMat').
            // Ma prima dobbiamo mascherarla.
            using Mat mask8U = new Mat();
            maskF.ConvertTo(mask8U, MatType.CV_8UC1, 255.0);
            using Mat regionCrop = Mat.Zeros(smoothedMat.Size(), smoothedMat.Type());
            smoothedMat.CopyTo(regionCrop, mask8U);
            // --- FINE MODIFICA ---

            return GetCenterByPeak(regionCrop, sigma: 0);
        }
    }
        
    #endregion

    #region FITS Data Helpers

    public Mat LoadFitsDataAsMat(FitsImageData fitsData)
    {
        var rawJaggedData = (Array[])fitsData.RawData;
        var header = fitsData.FitsHeader;
        int bitpix = header.GetIntValue("BITPIX");
        int width = (int)fitsData.ImageSize.Width;
        int height = (int)fitsData.ImageSize.Height;
        
        Mat imageMat;

        switch (bitpix)
        {
            case -32: // Float
                float[] flatDataF = ConvertJaggedToFlat<float>(rawJaggedData, width, height);
                imageMat = new Mat(height, width, MatType.CV_32FC1);
                imageMat.SetArray(flatDataF);
                break;
                
            case -64: // Double
                double[] flatDataD = ConvertJaggedToFlat<double>(rawJaggedData, width, height);
                imageMat = new Mat(height, width, MatType.CV_64FC1);
                imageMat.SetArray(flatDataD);
                break;
            
            case 16: // short
                short[] flatDataS = ConvertJaggedToFlat<short>(rawJaggedData, width, height);
                imageMat = new Mat(height, width, MatType.CV_16SC1);
                imageMat.SetArray(flatDataS);
                break;

            case 8: // byte
                byte[] flatDataB = ConvertJaggedToFlat<byte>(rawJaggedData, width, height);
                imageMat = new Mat(height, width, MatType.CV_8UC1);
                imageMat.SetArray(flatDataB);
                break;

            default:
                Debug.WriteLine($"LoadFitsDataAsMat: BITPIX non supportato {bitpix}");
                imageMat = new Mat();
                break;
        }
        return imageMat;
    }

    private T[] ConvertJaggedToFlat<T>(Array[] jaggedArray, int width, int height) where T : struct
    {
        T[] flatArray = new T[width * height];
        int k = 0;
        
        for (int j = 0; j < height; j++)
        {
            T[] row = (T[])jaggedArray[j];
            Array.Copy(row, 0, flatArray, k, width);
            k += width;
        }
        return flatArray;
    }
    
    public FitsImageData CreateFitsDataFromMat(Mat mat, FitsImageData originalData)
    {
        // 1. Estrai i dati flat dalla Mat
        Array flatData;
        int width = mat.Width;
        int height = mat.Height;
        
        if (mat.Type() == MatType.CV_32FC1)
        {
            float[] data;
            mat.GetArray(out data);
            flatData = data;
        }
        else // Assumiamo CV_64FC1
        {
            double[] data;
            mat.GetArray(out data);
            flatData = data;
        }

        // 2. Riconverti in JaggedArray (T[][])
        Array[] jaggedData;
        if (flatData is float[] f)
        {
            jaggedData = new Array[height];
            for (int j = 0; j < height; j++)
            {
                jaggedData[j] = new float[width];
                Array.Copy(f, j * width, jaggedData[j], 0, width);
            }
        }
        else // double[]
        {
            var d = (double[])flatData;
            jaggedData = new Array[height];
            for (int j = 0; j < height; j++)
            {
                jaggedData[j] = new double[width];
                Array.Copy(d, j * width, jaggedData[j], 0, width);
            }
        }
        
        // 3. Clona l'header
        var newHeader = new Header();
        foreach (object item in originalData.FitsHeader) // Usa il dato originale
        {
            if (item is System.Collections.DictionaryEntry entry && entry.Value is HeaderCard card)
            {
                newHeader.AddCard(card);
            }
        }
        
        // 4. Crea il modello "puro"
        return new FitsImageData
        {
            RawData = jaggedData,
            FitsHeader = newHeader,
            ImageSize = new Avalonia.Size(width, height)
            // Non calcola le soglie, perché il ViewModel le ricalcolerà
        };
    }
    
     // --- METODI HELPER PRIVATI (Logica di calcolo) ---

    public (double BlackPoint, double WhitePoint) CalculateClippedThresholds(FitsImageData fitsData)
    {
        int bitpix = fitsData.FitsHeader.GetIntValue("BITPIX");
        var jaggedData = (Array[])fitsData.RawData;

        switch (bitpix)
        {
            case 8: 
                return GetPercentiles(ConvertJaggedArray<byte>(jaggedData));
            case 16: 
                return GetPercentiles(ConvertJaggedArray<short>(jaggedData));
            case 32: 
                return GetPercentiles(ConvertJaggedArray<int>(jaggedData));
            case -32: 
                return GetPercentiles(ConvertJaggedArray<float>(jaggedData));
            case -64: 
                return GetPercentiles(ConvertJaggedArray<double>(jaggedData));
            default:
                throw new NotSupportedException($"BITPIX non supportato per GetPercentiles: {bitpix}");
        }
    }

    private (double BlackPoint, double WhitePoint) GetPercentiles(byte[][] data)
    {
        if (data.Length == 0 || data[0].Length == 0) return (0, 255);
        var pixelValues = data.SelectMany(row => row.Select(pixel => (double)pixel)
                                                    .Where(val => val != 0.0)); // <-- AGGIUNGI
        return CalculateQuantiles(pixelValues);
    }

    private (double BlackPoint, double WhitePoint) GetPercentiles(short[][] data)
    {
        if (data.Length == 0 || data[0].Length == 0) return (0, 255);
        var pixelValues = data.SelectMany(row => row.Select(pixel => (double)pixel)
                                                    .Where(val => val != 0.0)); // <-- AGGIUNGI
        return CalculateQuantiles(pixelValues);
    }

    private (double BlackPoint, double WhitePoint) GetPercentiles(int[][] data)
    {
        if (data.Length == 0 || data[0].Length == 0) return (0, 255);
        var pixelValues = data.SelectMany(row => row.Select(pixel => (double)pixel)
                                                    .Where(val => val != 0.0)); // <-- AGGIUNGI
        return CalculateQuantiles(pixelValues);
    }

    private (double BlackPoint, double WhitePoint) GetPercentiles(float[][] data)
    {
        if (data.Length == 0 || data[0].Length == 0) return (0, 255);
        var pixelValues = data.SelectMany(row => row.Select(pixel => (double)pixel)
            .Where(val => !double.IsNaN(val) && !double.IsInfinity(val)));
    
        return CalculateQuantiles(pixelValues);
    }

    private (double BlackPoint, double WhitePoint) GetPercentiles(double[][] data)
    {
        if (data.Length == 0 || data[0].Length == 0) return (0, 255);
        var pixelValues = data.SelectMany(row => row
            .Where(val => !double.IsNaN(val) && !double.IsInfinity(val)));

        return CalculateQuantiles(pixelValues);
    }

    /// <summary>
    /// Metodo helper che esegue il calcolo dei quantili su uno stream di double.
    /// </summary>
    private (double BlackPoint, double WhitePoint) CalculateQuantiles(IEnumerable<double> pixelValues)
    {
        // 1. Materializza la lista (questa è l'allocazione LOH, che è inevitabile)
        //    Usiamo ToList() perché è leggermente più veloce di ToArray() per Statistics
        var pixelList = pixelValues.ToList();
        if (pixelList.Count == 0) return (0, 255);

        // 2. Calcola i quantili
        double blackPoint = Statistics.Quantile(pixelList, 0.02);  // 2%
        double whitePoint = Statistics.Quantile(pixelList, 0.998); // 99.8%

        // 3. Fallback se i valori sono invertiti o identici
        if (whitePoint <= blackPoint)
        {
            // Dobbiamo trovare min/max (costoso, ma solo come fallback)
            double min = pixelList.Min();
            double max = pixelList.Max();
            return (min, max);
        }
        
        return (blackPoint, whitePoint);
    }

    private T[][] ConvertJaggedArray<T>(Array[] source) where T : struct
    {
        T[][] result = new T[source.Length][];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = (T[])source[i];
        }
        return result;
    }
    
    // In Services/ImageProcessingService.cs

// --- AGGIUNGI QUESTO NUOVO METODO HELPER ---

    /// <summary>
    /// Trova il bounding box dei dati validi (non-NaN) in un'immagine.
    /// </summary>
    /// <param name="imageMat">L'immagine sorgente (potenzialmente con bordi NaN).</param>
    /// <returns>Un Rect che delinea i dati validi.</returns>
    public Rect FindValidDataBox(Mat imageMat)
    {
        // 1. Crea una maschera 8-bit.
        //    (NaN == NaN) è SEMPRE falso.
        //    Quindi, i pixel NaN saranno 0, tutti gli altri 255.
        using Mat nanMask = new Mat();
        Cv2.Compare(imageMat, imageMat, nanMask, OpenCvSharp.CmpType.EQ);

        // 2. Trova il bounding box di tutti i pixel non-NaN (valore 255)
        Rect validDataBox = Cv2.BoundingRect(nanMask);

        // 3. Controlla se abbiamo trovato qualcosa
        if (validDataBox.Width <= 0 || validDataBox.Height <= 0)
        {
            // L'immagine è interamente NaN
            return new Rect();
        }
    
        return validDataBox;
    }

    #endregion
}