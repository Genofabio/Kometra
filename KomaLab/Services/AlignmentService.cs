using KomaLab.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using KomaLab.Models;
using OpenCvSharp;
using Point = Avalonia.Point;
using Size = Avalonia.Size;
using MathNet.Numerics; 
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Statistics;

namespace KomaLab.Services;

/// <summary>
/// Implementazione del servizio di calcolo dell'allineamento.
/// Contiene la logica di business pura.
/// </summary>
public class AlignmentService : IAlignmentService
{
    #region Logica di Business (Interfaccia Principale)

    public async Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        Size imageSize)
    {
        var newCoordinates = currentCoordinates.ToList();
        
        // TODO: Questa è ancora la logica di simulazione/placeholder.
        // Dovrà essere sostituita con chiamate a GetCenterOfLocalRegion, etc.
        await Task.Delay(500); // Simula lavoro
        Random rand = new Random();

        if (mode == AlignmentMode.Automatic)
        {
            for(int i = 0; i < newCoordinates.Count; i++)
            {
                double x = rand.NextDouble() * imageSize.Width;
                double y = rand.NextDouble() * imageSize.Height;
                newCoordinates[i] = new Point(x, y);
            }
        }
        else if (mode == AlignmentMode.Guided)
        {
            for(int i = 0; i < newCoordinates.Count; i++)
            {
                if (newCoordinates[i] == null)
                {
                    double x = rand.NextDouble() * imageSize.Width;
                    double y = rand.NextDouble() * imageSize.Height;
                    newCoordinates[i] = new Point(x, y);
                }
            }
        }
        
        return newCoordinates;
    }

    public bool CanCalculate(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        int totalCount)
    {
        var coordinateList = currentCoordinates.ToList();
        if (coordinateList.Count == 0)
            return false;
        
        switch (mode)
        {
            case AlignmentMode.Automatic:
                return true; 

            case AlignmentMode.Guided:
                if (totalCount == 1)
                {
                    return coordinateList[0].HasValue;
                }
                
                bool hasFirstAndLast = coordinateList.FirstOrDefault().HasValue && 
                                       coordinateList.LastOrDefault().HasValue;
                
                bool hasAllGuided = coordinateList.All(e => e.HasValue);
                return hasFirstAndLast || hasAllGuided;

            case AlignmentMode.Manual:
                bool hasAllManual = coordinateList.All(e => e.HasValue);
                return hasAllManual;

            default:
                return false;
        }
    }

    #endregion

    #region Workflow di Image Processing

    public Point GetCenterOfLocalRegion(
        FitsImageData fitsData,
        CenteringMethod centerFunc,
        double thresholdRatio = 0.1,
        int minArea = 10,
        int padding = 0)
    {
        // 1. Carica i dati FITS originali in float
        using Mat imageMat = LoadFitsDataAsMat(fitsData);
        if (imageMat.Empty()) return new Point(fitsData.ImageSize.Width / 2.0, fitsData.ImageSize.Height / 2.0);

        double medianVal;
        if (imageMat.Type() == MatType.CV_32FC1)
        {
            imageMat.GetArray(out float[] data);
            
            medianVal = Statistics.Median(data.Select(x => (double)x));
        }
        else
        {
            imageMat.GetArray(out double[] data);
            
            medianVal = Statistics.Median(data);
        }

        // 4. threshold = median_val * (1 + threshold_ratio)
        double threshold = medianVal * (1 + thresholdRatio);

        // 5. Crea maschera binaria (float 0.0 o 1.0)
        using Mat maskF = new Mat();
        Cv2.Threshold(imageMat, maskF, threshold, 1.0, ThresholdTypes.Binary);

        // 6. Converti maschera in 8-bit
        using Mat mask8U = new Mat();
        maskF.ConvertTo(mask8U, MatType.CV_8UC1, 255.0);

        // 6b. Esegui l'analisi dei componenti connessi
        using Mat labels = new Mat();
        using Mat stats = new Mat(); 
        using Mat centroids = new Mat();

        int numFeatures = Cv2.ConnectedComponentsWithStats(
            mask8U, 
            labels, 
            stats, 
            centroids
        );

        if (numFeatures <= 1)
        {
            Debug.WriteLine("LocalRegion: Nessuna feature trovata.");
            return new Point(imageMat.Width / 2.0, imageMat.Height / 2.0);
        }
        
        // 7. Trova la regione più grande (saltando lo sfondo, etichetta 0)
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

        // 8. Recupera il Bounding Box
        int minCol = stats.At<int>(largestRegionLabel, (int)ConnectedComponentsTypes.Left);
        int minRow = stats.At<int>(largestRegionLabel, (int)ConnectedComponentsTypes.Top);
        int w = stats.At<int>(largestRegionLabel, (int)ConnectedComponentsTypes.Width);
        int h = stats.At<int>(largestRegionLabel, (int)ConnectedComponentsTypes.Height);
        
        // 9. Applica padding e ritaglia
        int minRowP = Math.Max(minRow - padding, 0);
        int maxRowP = Math.Min(minRow + h + padding, imageMat.Rows);
        int minColP = Math.Max(minCol - padding, 0);
        int maxColP = Math.Min(minCol + w + padding, imageMat.Cols);

        Rect roiRect = new Rect(minColP, minRowP, maxColP - minColP, maxRowP - minRowP);
        
        using Mat regionCrop = new Mat(imageMat, roiRect);
        
        // 10. Chiama la funzione di centraggio richiesta
        Point cropCenter;
        switch (centerFunc)
        {
            case CenteringMethod.Centroid:
                cropCenter = GetCenterByCentroid(regionCrop);
                break;
                
            case CenteringMethod.GaussianFit:
                cropCenter = GetCenterByGaussianFit(regionCrop);
                break;
                
            case CenteringMethod.Peak:
            default:
                cropCenter = GetCenterByPeak(regionCrop);
                break;
        }
        
        // 11. Calcola coordinate globali
        double yCenter = cropCenter.Y + minRowP;
        double xCenter = cropCenter.X + minColP;

        return new Point(xCenter, yCenter);
    }

    public Mat CenterImageByCoords(Mat imageMat, Point centerPoint)
    {
        var x0 = centerPoint.X;
        var y0 = centerPoint.Y;

        var nx = imageMat.Width;
        var ny = imageMat.Height;

        var xShift = (nx / 2.0) - x0;
        var yShift = (ny / 2.0) - y0;

        // Matrice di trasformazione 2x3 per lo shift
        using var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set<double>(0, 0, 1);
        m.Set<double>(0, 1, 0);
        m.Set(0, 2, -xShift);
        m.Set<double>(1, 0, 0);
        m.Set<double>(1, 1, 1);
        m.Set(1, 2, -yShift);

        var centeredImage = new Mat();
    
        Cv2.WarpAffine(
            imageMat,
            centeredImage,
            m,
            imageMat.Size(), 
            InterpolationFlags.Cubic
        );
    
        return centeredImage;
    }

    #endregion

    #region Primitivi di Centraggio (Basso Livello)

    public Point GetCenterByCentroid(Mat imageMat, double sigma = 5.0)
    {
        if (imageMat.Empty())
        {
            Debug.WriteLine($"GetCenterByCentroid fallito: Mat vuota.");
            return new Point(-1, -1);
        }
        
        using Mat smoothedMat = new Mat();
        Cv2.GaussianBlur(imageMat, smoothedMat, new OpenCvSharp.Size(0, 0), sigmaX: sigma, sigmaY: sigma);

        Moments moments = Cv2.Moments(smoothedMat, binaryImage: false);

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
    
    public Point GetCenterByPeak(Mat imageMat, double sigma = 1.0)
    {
        // 1. Dati già caricati
        if (imageMat.Empty())
        {
            Debug.WriteLine($"GetCenterByPeak fallito: Mat vuota.");
            return new Point(-1, -1);
        }

        Mat matToProcess;
        // 2. Applica filtro Gaussiano (se sigma > 0)
        if (sigma > 0)
        {
            matToProcess = new Mat();
            Cv2.GaussianBlur(imageMat, matToProcess, new OpenCvSharp.Size(0, 0), sigmaX: sigma, sigmaY: sigma);
        }
        else
        {
            matToProcess = imageMat.Clone(); // Lavora su una copia se sigma è 0
        }

        // 3. Trova il massimo locale (picco discreto)
        Cv2.MinMaxLoc(matToProcess, out _, out _, out _, out OpenCvSharp.Point maxLoc);
        int x0 = maxLoc.X;
        int y0 = maxLoc.Y;

        // 4. Controlla i bordi
        if (y0 <= 0 || x0 <= 0 || y0 >= matToProcess.Rows - 1 || x0 >= matToProcess.Cols - 1)
        {
            matToProcess.Dispose();
            return new Point(x0, y0); // Picco sul bordo, restituisco il discreto
        }
        
        // 5. Fit parabolico 2D (separato)
        
        double subX, subY;
        var matType = matToProcess.Type();

        try
        {
            if (matType == MatType.CV_32FC1)
            {
                // Versione FLOAT
                float c = matToProcess.At<float>(y0, x0);     // Centro
                float r = matToProcess.At<float>(y0, x0 + 1); // Destra (Right)
                float l = matToProcess.At<float>(y0, x0 - 1); // Sinistra (Left)
                float u = matToProcess.At<float>(y0 - 1, x0); // Su (Up)
                float d = matToProcess.At<float>(y0 + 1, x0); // Giù (Down)

                double dx = (r - l) / 2.0;
                double dy = (d - u) / 2.0; // (Giù - Su) per l'asse Y
                double dxx = (r + l - 2 * c);
                double dyy = (d + u - 2 * c);

                // Evita divisione per zero (picco piatto o singolo hot pixel)
                if (Math.Abs(dxx) < 1e-6 || Math.Abs(dyy) < 1e-6)
                {
                    subX = x0;
                    subY = y0;
                }
                else
                {
                    subX = x0 - dx / dxx;
                    subY = y0 - dy / dyy;
                }
            }
            else if (matType == MatType.CV_64FC1)
            {
                // Versione DOUBLE
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
                {
                    subX = x0;
                    subY = y0;
                }
                else
                {
                    subX = x0 - dx / dxx;
                    subY = y0 - dy / dyy;
                }
            }
            else
            {
                // Tipo non supportato, fallback
                Debug.WriteLine("GetCenterByPeak: Tipo Mat non supportato per fit parabolico. Fallback a picco discreto.");
                subX = x0;
                subY = y0;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Fit parabolico fallito: {ex.Message}. Fallback a picco discreto.");
            subX = x0;
            subY = y0;
        }
        finally
        {
            // Pulisci il mat che abbiamo creato
            matToProcess.Dispose();
        }
        
        // Il risultato (subX, subY) è già in coordinate globali
        return new Point(subX, subY);
    }
    
    public Point GetCenterByGaussianFit(Mat imageMat, double thresholdRatio = 0.5, double sigma = 3.0)
    {
        if (imageMat.Empty())
        {
            Debug.WriteLine($"GetCenterByGaussianFit fallito: Mat vuota.");
            return new Point(-1, -1);
        }

        using Mat smoothedMat = new Mat();
        Cv2.GaussianBlur(imageMat, smoothedMat, new OpenCvSharp.Size(0, 0), sigmaX: sigma, sigmaY: sigma);

        Cv2.MinMaxLoc(smoothedMat, out double minVal, out double maxVal);
        double threshold = maxVal * thresholdRatio;

        using Mat maskF = new Mat();
        Cv2.Threshold(smoothedMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
            
        List<double[]> xDataList = [];
        List<double> yDataList = [];
        var matType = smoothedMat.Type();
            
        for (int y = 0; y < smoothedMat.Rows; y++)
        {
            for (int x = 0; x < smoothedMat.Cols; x++)
            {
                if (maskF.At<float>(y, x) > 0.5f)
                {
                    xDataList.Add([x, y]);
                    if (matType == MatType.CV_32FC1)
                        yDataList.Add(smoothedMat.At<float>(y, x));
                    else if (matType == MatType.CV_64FC1)
                        yDataList.Add(smoothedMat.At<double>(y, x));
                }
            }
        }
        
        double[][] xData = xDataList.ToArray(); 
        double[] yData = yDataList.ToArray();

        if (xData.Length < 6) 
        {
            Debug.WriteLine("GetCenterByGaussianFit: Pochi punti. Fallback a GetCenterByPeak.");
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
            return GetCenterByPeak(smoothedMat, sigma: 0);
        }
        
        double xoInit = xoInitNum / weightSum;
        double yoInit = yoInitNum / weightSum;
        double sigmaInit = 3.0;

        var initialGuess = Vector<double>.Build.Dense([ampInit, xoInit, yoInit, sigmaInit, sigmaInit, offsetInit]);
            
        var lowerBounds = Vector<double>.Build.Dense([0, 0, 0, 0.1, 0.1, minVal]);
        var upperBounds = Vector<double>.Build.Dense([ampInit * 2, imageMat.Width, imageMat.Height, imageMat.Width, imageMat.Height, maxVal
        ]);

        try
        {
            // --- INIZIO MODIFICA WLS (Weighted Least Squares) ---

            // 7. Calcola i pesi (1 / sqrt(varianza))
            //    Assumiamo varianza = segnale (rumore di Poisson)
            
            // Troviamo un valore "floor" per evitare divisioni per zero
            double minPositiveSignal = yData.Where(y => y > 0).DefaultIfEmpty(1.0).Min();

            double[] weights = new double[yData.Length];
            for (int i = 0; i < yData.Length; i++)
            {
                // Varianza = max(segnale, floor)
                double variance = Math.Max(yData[i], minPositiveSignal); 
                // Peso = 1 / sigma (deviazione standard)
                weights[i] = 1.0 / Math.Sqrt(variance); 
            }

            var yDataVector = Vector<double>.Build.Dense(yData);
            var weightsVector = Vector<double>.Build.Dense(weights); // Il nostro vettore di pesi

            // 8. Definisci la funzione 'costo' (Obiettivo)
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
                
                // Calcola i residui (errore)
                var residuals = yDataVector - modelZ;
                
                // Applica i pesi!
                var weightedResiduals = residuals.PointwiseMultiply(weightsVector);
                
                // L'errore è la Distanza Euclidea (L2-Norm) dei residui *pesati*
                return weightedResiduals.L2Norm();
            };
            
            // 9. Esegui il fitting (invariato)
            Vector<double> pFit = FindMinimum.OfFunctionConstrained(
                objectiveFunc,
                lowerBounds,
                upperBounds,
                initialGuess,
                maxIterations: 100
            );
            // --- FINE MODIFICA WLS ---

            // 10. Estrai i parametri (invariato)
            double xoFit = pFit[1];
            double yoFit = pFit[2];

            if (double.IsNaN(xoFit) || double.IsNaN(yoFit) ||
                double.IsInfinity(xoFit) || double.IsInfinity(yoFit))
            {
                throw new Exception("Il risultato del Fit non è valido (NaN/Infinity).");
            }

            return new Point(xoFit, yoFit);
        }
        catch (Exception ex)
        {
            // ... (Blocco Fallback invariato) ...
            Debug.WriteLine($"GetCenterByGaussianFit: Fit fallito ({ex.Message}). Uso fallback (GetCenterByPeak).");
                
            using Mat regionCrop = Mat.Zeros(smoothedMat.Size(), smoothedMat.Type());
            using Mat mask8U = new Mat();
            maskF.ConvertTo(mask8U, MatType.CV_8UC1, 255.0);
            smoothedMat.CopyTo(regionCrop, mask8U);

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
            
            // Aggiungi altri tipi FITS comuni se necessario
            case 16: // short
                short[] flatDataS = ConvertJaggedToFlat<short>(rawJaggedData, width, height);
                imageMat = new Mat(height, width, MatType.CV_16SC1); // Signed 16-bit
                imageMat.SetArray(flatDataS);
                break;

            case 8: // byte
                byte[] flatDataB = ConvertJaggedToFlat<byte>(rawJaggedData, width, height);
                imageMat = new Mat(height, width, MatType.CV_8UC1); // Unsigned 8-bit
                imageMat.SetArray(flatDataB);
                break;

            default:
                Debug.WriteLine($"LoadFitsDataAsMat: BITPIX non supportato {bitpix}");
                imageMat = new Mat(); // Mat vuota
                break;
        }
        return imageMat;
    }

    /// <summary>
    /// Converte un array jagged (T[][]) dalla libreria FITS
    /// in un array flat (T[]) richiesto da OpenCV.
    /// </summary>
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

    #endregion
}