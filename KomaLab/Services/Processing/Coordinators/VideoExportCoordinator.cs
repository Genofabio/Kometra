using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
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
    private readonly IFitsMetadataService _metadata;

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
        await Task.Run(async () =>
        {
            VideoWriter? writer = null;
            Mat? frame8Bit = null;
            
            // Lo "stile" di contrasto espresso in Sigma (Z-Score)
            SigmaContrastProfile? referenceSigmaProfile = null;

            try
            {
                foreach (var fileRef in sourceFiles)
                {
                    token.ThrowIfCancellationRequested();

                    // 1. Caricamento dati e metadati
                    var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                    var header = fileRef.ModifiedHeader ?? data.Header;

                    // 2. Decisione Bit-Depth (Ottimizzazione RAM/Precisione)
                    int bitpix = (int)_metadata.GetDoubleValue(header, "BITPIX", 16);
                    FitsBitDepth targetDepth = bitpix switch
                    {
                        32   => FitsBitDepth.Double, // Interi 32bit -> Double per evitare overflow/precisione
                        -64  => FitsBitDepth.Double, 
                        _    => FitsBitDepth.Float   // Default sicuro per 8, 16 e -32 bit
                    };
                    
                    double bScale = _metadata.GetDoubleValue(header, "BSCALE", 1.0);
                    double bZero = _metadata.GetDoubleValue(header, "BZERO", 0.0);

                    // 3. Conversione a Mat Scientifica (32/64 bit float)
                    using Mat scientificMat = _converter.RawToMat(data.PixelData, bScale, bZero, targetDepth);

                    // 4. Inizializzazione VideoWriter (solo al primo frame)
                    if (writer == null)
                    {
                        var videoSize = new OpenCvSharp.Size(scientificMat.Width, scientificMat.Height);
                        writer = new VideoWriter(outputFilePath, VideoWriter.FourCC('M','J','P','G'), fps, videoSize, isColor: false);
                        frame8Bit = new Mat(scientificMat.Rows, scientificMat.Cols, MatType.CV_8UC1);
                        
                        if (!writer.IsOpened()) throw new IOException("Impossibile inizializzare il codec video.");
                    }

                    // 5. LOGICA ANTI-FLICKER (Adaptive Stretch)
                    // Calcoliamo le statistiche correnti (Mean/StdDev)
                    var currentStats = _presentation.GetPresentationRequirements(scientificMat);
                    
                    AbsoluteContrastProfile effectiveProfile;

                    if (adaptiveStretch)
                    {
                        // Se è il primo frame, definiamo lo "stile" basandoci sul profilo iniziale
                        referenceSigmaProfile ??= _presentation.GetRelativeProfile(initialProfile, currentStats);

                        // Applichiamo lo stile alle statistiche del frame corrente
                        effectiveProfile = _presentation.GetAbsoluteProfile(referenceSigmaProfile, currentStats);
                    }
                    else
                    {
                        // Se non è adattivo, usiamo i valori ADU fissi del profilo iniziale
                        effectiveProfile = initialProfile;
                    }

                    // 6. Rendering & Scrittura
                    _presentation.RenderTo8Bit(scientificMat, frame8Bit!, effectiveProfile.BlackAdu, effectiveProfile.WhiteAdu, mode);
                    writer.Write(frame8Bit);
                }
            }
            finally
            {
                writer?.Dispose();
                frame8Bit?.Dispose();
            }
        }, token);
    }
}