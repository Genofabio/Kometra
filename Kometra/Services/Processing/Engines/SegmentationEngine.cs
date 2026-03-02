using System;
using System.Collections.Generic;
using System.Linq;
using Kometra.Models.Processing.Masking;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public class SegmentationEngine : ISegmentationEngine
{
    public Mat ComputeCometMask(Mat image, double backgroundLevel, double noiseStdDev, MaskingParameters p)
    {
        // --- LOGICA COMETA (Invariata) ---
        int h = image.Rows;
        int w = image.Cols;
        int cx = w / 2;
        int cy = h / 2;

        using Mat subImage = new Mat();
        Cv2.Subtract(image, new Scalar(backgroundLevel), subImage);

        double thresholdVal = p.CometThresholdSigma * Math.Max(noiseStdDev, 1e-6);
        using Mat binary = new Mat();
        Cv2.Threshold(subImage, binary, thresholdVal, 255, ThresholdTypes.Binary);
        
        using Mat binary8u = new Mat();
        binary.ConvertTo(binary8u, MatType.CV_8UC1);

        using Mat labels = new Mat();
        using Mat statsCC = new Mat();
        using Mat centroids = new Mat();
        int nLabels = Cv2.ConnectedComponentsWithStats(binary8u, labels, statsCC, centroids);

        int centerLabel = 0;
        int valAtCenter = labels.At<int>(cy, cx);
        
        if (valAtCenter > 0)
        {
            centerLabel = valAtCenter;
        }
        else
        {
            var freq = new Dictionary<int, int>();
            int rad = 5;
            for (int y = cy - rad; y <= cy + rad; y++)
            {
                for (int x = cx - rad; x <= cx + rad; x++)
                {
                    if (y < 0 || y >= h || x < 0 || x >= w) continue;
                    int lbl = labels.At<int>(y, x);
                    if (lbl > 0)
                    {
                        if (!freq.ContainsKey(lbl)) freq[lbl] = 0;
                        freq[lbl]++;
                    }
                }
            }
            if (freq.Count > 0) centerLabel = freq.Aggregate((l, r) => l.Value > r.Value ? l : r).Key;
        }

        Mat finalMask = new Mat(h, w, MatType.CV_8UC1, new Scalar(0));

        if (centerLabel > 0)
        {
            Cv2.Compare(labels, new Scalar(centerLabel), finalMask, CmpType.EQ);

            using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
            Cv2.MorphologyEx(finalMask, finalMask, MorphTypes.Close, closeKernel);

            if (p.CometDilation > 0)
            {
                using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
                Cv2.Dilate(finalMask, finalMask, dilateKernel, iterations: p.CometDilation);
            }
            
            var contours = Cv2.FindContoursAsArray(finalMask, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            Cv2.DrawContours(finalMask, contours, -1, new Scalar(255), thickness: -1);
        }

        return finalMask;
    }

    public Mat ComputeStarMask(Mat image, Mat cometMask, double backgroundLevel, double noiseStdDev, MaskingParameters p)
    {
        // 1. SOTTRAZIONE BACKGROUND & MASKING
        using Mat subImage = new Mat();
        Cv2.Subtract(image, new Scalar(backgroundLevel), subImage);
        
        // Rimuoviamo la cometa PRIMA di calcolare qualsiasi cosa
        subImage.SetTo(new Scalar(0), cometMask);

        // 2. THRESHOLD (Binarizzazione)
        double thresholdVal = p.StarThresholdSigma * Math.Max(noiseStdDev, 1e-6);
        
        using Mat binary = new Mat();
        Cv2.Threshold(subImage, binary, thresholdVal, 255, ThresholdTypes.Binary);
        
        Mat starMask = new Mat();
        binary.ConvertTo(starMask, MatType.CV_8UC1);

        // 3. PULIZIA RUMORE DINAMICA (Morphological Opening)
        // Usiamo il parametro dell'utente per definire la dimensione minima.
        // Assicuriamo che il kernel sia dispari (1, 3, 5, 7...) per avere un centro.
        // Se MinStarDiameter non esiste ancora in 'p', aggiungilo alla classe MaskingParameters!
        int kernelSize = Math.Max(1, p.MinStarDiameter); 
        if (kernelSize % 2 == 0) kernelSize++; 

        // Applichiamo il filtro solo se il diametro richiesto è > 1 pixel.
        // Questo passaggio elimina rumore e hot pixel PRIMA che vengano dilatati.
        if (kernelSize > 1)
        {
            using var cleanKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kernelSize, kernelSize));
            Cv2.MorphologyEx(starMask, starMask, MorphTypes.Open, cleanKernel);
        }

        // 4. DILATAZIONE UTENTE (Espansione)
        // Espandiamo le stelle sopravvissute per coprire gli aloni (glow).
        if (p.StarDilation > 0)
        {
            using var expandKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            Cv2.Dilate(starMask, starMask, expandKernel, iterations: p.StarDilation);
        }

        return starMask;
    }
}