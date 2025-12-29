using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models;
using KomaLab.Services.Data;
using OpenCvSharp;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;
using Size = Avalonia.Size;

namespace KomaLab.Services.Imaging;

public class ImageOperationService : IImageOperationService
{
    private readonly IFitsDataConverter _converter;
    private readonly IImageAnalysisService _analysis;

    public ImageOperationService(IFitsDataConverter converter, IImageAnalysisService analysis)
    {
        _converter = converter;
        _analysis = analysis;
    }

    public Mat GetSubPixelCenteredCanvas(Mat source, Point originalCenter, Size outputSize)
    {
        double destCenterX = outputSize.Width / 2.0;
        double destCenterY = outputSize.Height / 2.0;
        double tx = destCenterX - originalCenter.X;
        double ty = destCenterY - originalCenter.Y;

        using Mat m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, 1.0); m.Set(0, 1, 0.0); m.Set(0, 2, tx);
        m.Set(1, 0, 0.0); m.Set(1, 1, 1.0); m.Set(1, 2, ty);

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
            Cv2.WarpAffine(sourceDouble, result, m, cvSize, InterpolationFlags.Lanczos4, BorderTypes.Transparent, new Scalar(double.NaN));
        }
        finally
        {
            if (disposeSource) sourceDouble.Dispose();
        }
        return result;
    }

    public (Mat template, Point preciseCenter) ExtractRefinedTemplate(FitsImageData? data, Point roughGuess, int radius)
    {
        if (data == null) return (new Mat(), roughGuess);
        using Mat fullImg = _converter.RawToMat(data);
        Rect roi = CreateSafeRoi(fullImg, roughGuess, radius);
        if (roi.Width <= 0) throw new Exception("ROI non valida");

        var cvRoi = new OpenCvSharp.Rect((int)roi.X, (int)roi.Y, (int)roi.Width, (int)roi.Height);
        using Mat crop = new Mat(fullImg, cvRoi);
        Point local = _analysis.FindCenterOfLocalRegion(crop);
        Point global = new Point(local.X + roi.X, local.Y + roi.Y);

        Rect templRect = CreateSafeRoi(fullImg, global, radius);
        var cvTemplRect = new OpenCvSharp.Rect((int)templRect.X, (int)templRect.Y, (int)templRect.Width, (int)templRect.Height);
        using Mat tempRaw = new Mat(fullImg, cvTemplRect);
        Mat templateF = NormalizeAndConvertToFloat(tempRaw);
        return (templateF, global);
    }

    public Point? FindTemplatePosition(Mat fullImage, Mat templateF, Point expectedCenter, int searchRadius)
    {
        if (fullImage.Empty() || templateF.Empty()) return null;
        int searchW = searchRadius * 3;
        Rect searchRect = CreateSafeRoi(fullImage, expectedCenter, searchW);
        if (searchRect.Width <= templateF.Width || searchRect.Height <= templateF.Height) return null;

        var cvSearchRect = new OpenCvSharp.Rect((int)searchRect.X, (int)searchRect.Y, (int)searchRect.Width, (int)searchRect.Height);
        using Mat searchRegion = new Mat(fullImage, cvSearchRect);
        using Mat searchF = NormalizeAndConvertToFloat(searchRegion);
        using Mat res = new Mat();

        Cv2.MatchTemplate(searchF, templateF, res, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

        if (maxVal < 0.5) return null;

        double matchCx = maxLoc.X + (templateF.Width / 2.0);
        double matchCy = maxLoc.Y + (templateF.Height / 2.0);
        Point roughLocal = new Point(matchCx, matchCy);
        Rect refineRect = CreateSafeRoi(searchRegion, roughLocal, searchRadius);
        Point subPixelCenter;
        
        if (refineRect.Width > 0 && refineRect.Height > 0)
        {
            var cvRefineRect = new OpenCvSharp.Rect((int)refineRect.X, (int)refineRect.Y, (int)refineRect.Width, (int)refineRect.Height);
            using Mat refineCrop = new Mat(searchRegion, cvRefineRect);
            Point localRefined = _analysis.FindCenterOfLocalRegion(refineCrop);
            subPixelCenter = new Point(localRefined.X + refineRect.X, localRefined.Y + refineRect.Y);
        }
        else subPixelCenter = roughLocal;

        return new Point(subPixelCenter.X + searchRect.X, subPixelCenter.Y + searchRect.Y);
    }

    public async Task<FitsImageData> ComputeStackAsync(List<FitsImageData> sources, StackingMode mode)
    {
        if (sources == null || sources.Count == 0) throw new ArgumentException("Nessuna immagine");
        var refData = sources[0];
        int width = refData.Width;
        int height = refData.Height;
        using Mat resultMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));

        await Task.Run(() =>
        {
            if (mode == StackingMode.Sum || mode == StackingMode.Average)
            {
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
                if (mode == StackingMode.Average) Cv2.Divide(resultMat, validCountMat, resultMat, scale: 1, dtype: MatType.CV_64FC1);
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
                        var start = yStart;
                        Parallel.For(0, sources.Count, i => stripStack[i] = _converter.RawToMatRect(sources[i], start, currentStripH));
                        Parallel.For(0, currentStripH, yRel =>
                        {
                            var valuesToStack = new List<double>(sources.Count);
                            for (int x = 0; x < width; x++)
                            {
                                valuesToStack.Clear();
                                for (int k = 0; k < sources.Count; k++)
                                {
                                    double val = stripStack[k].At<double>(yRel, x);
                                    if (!double.IsNaN(val)) valuesToStack.Add(val);
                                }
                                if (valuesToStack.Count == 0) continue;
                                valuesToStack.Sort();
                                double median = (valuesToStack.Count % 2 == 0) ? (valuesToStack[valuesToStack.Count / 2 - 1] + valuesToStack[valuesToStack.Count / 2]) / 2.0 : valuesToStack[valuesToStack.Count / 2];
                                resultMat.Set(start + yRel, x, median);
                            }
                        });
                    }
                    finally { foreach (var m in stripStack) m?.Dispose(); }
                }
            }
        });
        return _converter.MatToFitsData(resultMat, refData);
    }

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
        int sx = Math.Max(0, (int)(center.X - radius));
        int sy = Math.Max(0, (int)(center.Y - radius));
        int sw = Math.Min(size, mat.Width - sx);
        int sh = Math.Min(size, mat.Height - sy);
        return new Rect(sx, sy, sw, sh);
    }
}