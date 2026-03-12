using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Primitives;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.Processing.Engines;
using OpenCvSharp;

namespace Kometra.Services.Processing.Coordinators;

public class NodeStructureCoordinator : INodeStructureCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IGeometricEngine _geometricEngine;

    public NodeStructureCoordinator(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter,
        IGeometricEngine geometricEngine)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _geometricEngine = geometricEngine ?? throw new ArgumentNullException(nameof(geometricEngine));
    }

    /// <summary>
    /// Unifica tutti i file forniti in un'unica sequenza, centrando ogni immagine 
    /// in un canvas di dimensioni pari alla Bounding Box massima del set.
    /// </summary>
    public async Task<List<string>> JoinNodesAsync(List<FitsFileReference> allFiles)
    {
        if (allFiles == null || !allFiles.Any()) return new List<string>();

        // 1. ANALISI DIMENSIONI: Determiniamo il canvas target (il più grande tra tutti)
        int maxWidth = 0;
        int maxHeight = 0;

        foreach (var file in allFiles)
        {
            var header = await _dataManager.GetHeaderOnlyAsync(file.FilePath);
            maxWidth = Math.Max(maxWidth, _metadataService.GetIntValue(header, "NAXIS1", 0));
            maxHeight = Math.Max(maxHeight, _metadataService.GetIntValue(header, "NAXIS2", 0));
        }

        var targetSize = new Size2D(maxWidth, maxHeight);
        var resultPaths = new List<string>();

        // 2. ELABORAZIONE: Centratura universale per Blink perfetto
        for (int i = 0; i < allFiles.Count; i++)
        {
            var fileRef = allFiles[i];
            var dataPackage = await _dataManager.GetDataAsync(fileRef.FilePath);
            var hdu = dataPackage.FirstImageHdu ?? dataPackage.PrimaryHdu;
            
            if (hdu == null) continue;

            // Carichiamo i dati originali
            var header = fileRef.ModifiedHeader ?? hdu.Header;
            double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
            double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);
            
            using var sourceMat = _converter.RawToMat(hdu.PixelData, bScale, bZero, null);
            
            // Creiamo il canvas centrato (padding automatico con NaN)
            var sourceCenter = new Point2D(sourceMat.Width / 2.0, sourceMat.Height / 2.0);
            using var canvasMat = _geometricEngine.CropCentered(sourceMat, sourceCenter, targetSize);

            // Salvataggio in Floating Point (Float o Double)
            FitsBitDepth outputDepth = (canvasMat.Depth() == MatType.CV_64F) 
                ? FitsBitDepth.Double 
                : FitsBitDepth.Float;

            var rawResult = _converter.MatToRaw(canvasMat, outputDepth);
            
            // Reset BSCALE/BZERO per il nuovo file floating point
            var newHeader = _metadataService.CreateHeaderFromTemplate(header, rawResult, outputDepth);
            _metadataService.SetValue(newHeader, "BSCALE", 1.0);
            _metadataService.SetValue(newHeader, "BZERO", 0.0);
            _metadataService.AddValue(newHeader, "HISTORY", $"Node Join: Unified canvas {maxWidth}x{maxHeight}");

            var savedRef = await _dataManager.SaveAsTemporaryAsync(rawResult, newHeader, $"JoinedIdx_{i}");
            resultPaths.Add(savedRef.FilePath);
        }

        return resultPaths;
    }

    /// <summary>
    /// Restituisce i percorsi dei file per la separazione in nodi singoli.
    /// </summary>
    public List<string> SplitNode(List<FitsFileReference> allFiles)
    {
        return allFiles.Select(f => f.FilePath).ToList();
    }
}