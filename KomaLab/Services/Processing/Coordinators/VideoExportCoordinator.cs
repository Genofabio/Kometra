using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Rendering;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Coordinators;

public class VideoExportCoordinator : IVideoExportCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImagePresentationService _presentation;
    private readonly IFitsMetadataService _metadata; // Reintegrato come da standard di progetto

    public VideoExportCoordinator(
        IFitsDataManager dataManager, 
        IFitsOpenCvConverter converter,
        IImagePresentationService presentation,
        IFitsMetadataService metadata) 
    {
        _dataManager = dataManager;
        _converter = converter;
        _presentation = presentation;
        _metadata = metadata;
    }

    public async Task ExportVideoAsync(
        IEnumerable<FitsFileReference> sourceFiles, 
        string outputFilePath, 
        double fps,
        AbsoluteContrastProfile initialProfile,
        VisualizationMode mode,
        bool adaptiveStretch = true,
        CancellationToken token = default)
    {
        // Risoluzione errore Ambiguous Invocation: 
        // Castiamo esplicitamente la lambda a Func<Task> per aiutare il compilatore
        await Task.Run((Func<Task>)(async () =>
        {
            VideoWriter? writer = null;
            Mat? frame8Bit = null;
            var currentProfile = initialProfile;
            (double Mean, double StdDev)? lastMetrics = null;

            try
            {
                foreach (var fileRef in sourceFiles)
                {
                    token.ThrowIfCancellationRequested();

                    // 1. Caricamento dati
                    var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                    var headerToUse = fileRef.ModifiedHeader ?? data.Header;

                    // 2. Estrazione parametri via MetadataService (Uso standard)
                    double bScale = _metadata.GetDoubleValue(headerToUse, "BSCALE", 1.0);
                    double bZero = _metadata.GetDoubleValue(headerToUse, "BZERO", 0.0);

                    // 3. Conversione a Mat Scientifica
                    using Mat scientificMat = _converter.RawToMat(data.PixelData, bScale, bZero);
                    Cv2.PatchNaNs(scientificMat, 0.0);

                    // 4. Inizializzazione VideoWriter
                    if (writer == null)
                    {
                        var videoSize = new OpenCvSharp.Size(scientificMat.Width, scientificMat.Height);
                        writer = new VideoWriter(outputFilePath, VideoWriter.FourCC('M','J','P','G'), fps, videoSize, isColor: false);
                        frame8Bit = new Mat(scientificMat.Rows, scientificMat.Cols, MatType.CV_8UC1);
                        
                        if (!writer.IsOpened()) throw new IOException("Impossibile inizializzare il codec video.");
                    }

                    // 5. Adattamento Anti-Flicker
                    if (adaptiveStretch && lastMetrics.HasValue)
                    {
                        currentProfile = _presentation.GetAdaptedProfile(scientificMat, currentProfile, lastMetrics.Value);
                    }

                    // 6. Rendering & Scrittura
                    _presentation.RenderTo8Bit(scientificMat, frame8Bit!, currentProfile.BlackAdu, currentProfile.WhiteAdu, mode);
                    writer.Write(frame8Bit);

                    if (adaptiveStretch)
                        lastMetrics = _presentation.GetPresentationRequirements(scientificMat);
                }
            }
            finally
            {
                writer?.Dispose();
                frame8Bit?.Dispose();
            }
        }));
    }
}