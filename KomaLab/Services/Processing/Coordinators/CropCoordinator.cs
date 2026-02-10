using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Batch;
using KomaLab.Services.Processing.Engines;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Coordinators;

public class CropCoordinator : ICropCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadataService;
    private readonly IGeometricEngine _geometricEngine;
    private readonly IBatchProcessingService _batchService;

    public CropCoordinator(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IGeometricEngine geometricEngine,
        IBatchProcessingService batchService)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _geometricEngine = geometricEngine ?? throw new ArgumentNullException(nameof(geometricEngine));
        _batchService = batchService ?? throw new ArgumentNullException(nameof(batchService));
    }

    public async Task<Size2D> AnalyzeSequenceLimitsAsync(IEnumerable<FitsFileReference> files)
    {
        int minW = int.MaxValue;
        int minH = int.MaxValue;
        bool anyFound = false;

        foreach (var file in files)
        {
            var header = file.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(file.FilePath);
            if (header == null) continue;

            int w = _metadataService.GetIntValue(header, "NAXIS1");
            int h = _metadataService.GetIntValue(header, "NAXIS2");

            if (w > 0 && h > 0)
            {
                if (w < minW) minW = w;
                if (h < minH) minH = h;
                anyFound = true;
            }
        }

        if (!anyFound) return new Size2D(1000, 1000); // Fallback sicuro
        
        return new Size2D(minW, minH);
    }

    public async Task<List<string>> ExecuteCropBatchAsync(
        IEnumerable<FitsFileReference> files,
        List<Point2D?> centers,
        Size2D cropSize,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var fileList = files.ToList();

        // Validazione
        if (cropSize.Width <= 0 || cropSize.Height <= 0) 
            throw new ArgumentException("Dimensioni di ritaglio non valide.");

        if (centers.Count != fileList.Count)
            throw new ArgumentException("La lista dei centri deve corrispondere al numero di file.");

        // Definizione della logica per singolo frame
        Action<Mat, Mat, FitsHeader, int> cropProcessor = (src, dst, header, index) =>
        {
            // 1. Recupera il centro
            Point2D center = centers[index] ?? new Point2D(src.Width / 2.0, src.Height / 2.0);

            // 2. Esegue il ritaglio geometrico
            // Nota: Se _geometricEngine ha un metodo diverso (es. CropCenteredTo), adatta qui.
            // Qui assumiamo che CropCentered restituisca una nuova Mat da copiare in dst.
            using var croppedMat = _geometricEngine.CropCentered(src, center, cropSize);
            croppedMat.CopyTo(dst);

            // 3. Aggiorna l'Header FITS
            UpdateFitsMetadata(header, center, cropSize);
        };

        // Esecuzione batch
        return await _batchService.ProcessFilesAsync(
            fileList, 
            "Cropped", 
            cropProcessor, 
            progress, 
            token);
    }

    private void UpdateFitsMetadata(FitsHeader header, Point2D cropCenter, Size2D cropSize)
    {
        // A. Aggiorna Dimensioni
        _metadataService.AddValue(header, "NAXIS1", (int)cropSize.Width, "Cropped Width");
        _metadataService.AddValue(header, "NAXIS2", (int)cropSize.Height, "Cropped Height");

        // B. Aggiorna WCS (CRPIX)
        // Calcolo dell'origine del crop (angolo in alto a sinistra)
        double cropOriginX = cropCenter.X - (cropSize.Width / 2.0);
        double cropOriginY = cropCenter.Y - (cropSize.Height / 2.0);

        double crpix1 = _metadataService.GetDoubleValue(header, "CRPIX1", double.NaN);
        double crpix2 = _metadataService.GetDoubleValue(header, "CRPIX2", double.NaN);

        if (!double.IsNaN(crpix1))
        {
            _metadataService.AddValue(header, "CRPIX1", crpix1 - cropOriginX, "Ref pixel adj.");
        }
        
        if (!double.IsNaN(crpix2))
        {
            _metadataService.AddValue(header, "CRPIX2", crpix2 - cropOriginY, "Ref pixel adj.");
        }

        // C. Aggiunta History (Corretto usando AddValue)
        string historyMsg = $"KomaLab Crop: Center=({cropCenter.X:F1},{cropCenter.Y:F1}) Size={cropSize.Width}x{cropSize.Height}";
        _metadataService.AddValue(header, "HISTORY", historyMsg, string.Empty);
    }
}