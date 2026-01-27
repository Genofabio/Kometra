using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Infrastructure;
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
    private readonly IVideoEncoder _encoder;
    private readonly IVideoFormatProvider _formatProvider;

    public VideoExportCoordinator(
        IFitsDataManager dataManager, 
        IFitsOpenCvConverter converter,
        IImagePresentationService presentation,
        IFitsMetadataService metadata,
        IVideoEncoder encoder,
        IVideoFormatProvider formatProvider) 
    {
        _dataManager = dataManager;
        _converter = converter;
        _presentation = presentation;
        _metadata = metadata;
        _encoder = encoder;
        _formatProvider = formatProvider;
    }

    public async Task ExportVideoAsync(
        IEnumerable<FitsFileReference> sourceFiles, 
        VideoExportSettings settings,
        AbsoluteContrastProfile initialProfile,
        IProgress<double>? progress = null, 
        CancellationToken token = default)
    {
        // Forziamo l'esecuzione in un Task separato (MTA Thread).
        // Essenziale per i codec H265 di Windows e per non bloccare la UI di Avalonia.
        await Task.Run(async () => 
        {
            SigmaContrastProfile? referenceSigmaProfile = null;
            bool isInitialized = false;
            bool success = false;
        
            var filesList = sourceFiles as IReadOnlyList<FitsFileReference> ?? sourceFiles.ToList();
            int total = filesList.Count;
            int current = 0;

            try
            {
                foreach (var fileRef in filesList)
                {
                    token.ThrowIfCancellationRequested();

                    using Mat scientificMat = await LoadFitsToScientificMat(fileRef);
                    using Mat scaledMat = ApplyRescale(scientificMat, settings.ScaleFactor);

                    if (!isInitialized)
                    {
                        // 1. Otteniamo il FourCC corretto per la combinazione scelta
                        int fourCc = _formatProvider.GetFourCC(settings.Codec, settings.Container);
    
                        // 2. Recuperiamo l'API (FFMPEG, MSMF, etc.) che ha superato i test di validazione
                        var bestApi = _formatProvider.GetBestAPI(settings.Container, settings.Codec);

                        // 3. Inizializzazione encoder con API specifica e isColor: true
                        _encoder.Initialize(
                            settings.OutputPath, 
                            settings.Fps, 
                            scaledMat.Width, 
                            scaledMat.Height, 
                            fourCc, 
                            bestApi); 
    
                        isInitialized = true;
                    }

                    var effectiveProfile = settings.AdaptiveStretch 
                        ? GetAdaptiveProfile(scaledMat, initialProfile, ref referenceSigmaProfile)
                        : initialProfile;

                    using Mat frame8Bit = new Mat();
                    _presentation.RenderTo8Bit(scaledMat, frame8Bit, effectiveProfile.BlackAdu, effectiveProfile.WhiteAdu, settings.Mode);
                
                    // CONVERSIONE BGR: Molti encoder hardware falliscono se ricevono 1 solo canale.
                    // Trasformiamo il frame renderizzato in 3 canali per compatibilità totale.
                    using Mat colorFrame = new Mat();
                    Cv2.CvtColor(frame8Bit, colorFrame, ColorConversionCodes.GRAY2BGR);

                    _encoder.WriteFrame(colorFrame);

                    current++;
                    progress?.Report((double)current / total * 100.0);
                }
                success = true;
            }
            catch (OperationCanceledException)
            {
                // Esportazione annullata dall'utente
            }
            catch (Exception ex)
            {
                // Logga o gestisci l'errore di elaborazione
                throw new Exception($"Errore durante l'esportazione video: {ex.Message}", ex);
            }
            finally
            {
                // Fondamentale: Il Dispose chiude il VideoWriter. 
                // Se non viene chiamato, l'header del file non viene scritto (file da 258 byte).
                _encoder.Dispose();

                // Se l'operazione è fallita o è stata annullata, puliamo il file parziale
                if (!success && File.Exists(settings.OutputPath))
                {
                    try { File.Delete(settings.OutputPath); } catch { /* Ignore */ }
                }
            }
        }); 
    }

    private async Task<Mat> LoadFitsToScientificMat(FitsFileReference fileRef)
    {
        var data = await _dataManager.GetDataAsync(fileRef.FilePath);
        var header = fileRef.ModifiedHeader ?? data.Header;
        int bitpix = (int)_metadata.GetDoubleValue(header, "BITPIX", 16);
        FitsBitDepth depth = bitpix switch { 32 => FitsBitDepth.Double, -64 => FitsBitDepth.Double, _ => FitsBitDepth.Float };
        double bScale = _metadata.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadata.GetDoubleValue(header, "BZERO", 0.0);
        return _converter.RawToMat(data.PixelData, bScale, bZero, depth);
    }

    private Mat ApplyRescale(Mat source, double factor)
    {
        if (Math.Abs(factor - 1.0) < 0.001) return source.Clone();
        
        // I codec H264/H265 richiedono dimensioni PARI. 
        // L'operatore '& ~1' azzera l'ultimo bit, garantendo numeri divisibili per 2.
        int newWidth = (int)(source.Width * factor) & ~1;
        int newHeight = (int)(source.Height * factor) & ~1;

        Mat resized = new Mat();
        Cv2.Resize(source, resized, new Size(newWidth, newHeight), 0, 0, InterpolationFlags.Area);
        return resized;
    }

    private AbsoluteContrastProfile GetAdaptiveProfile(Mat mat, AbsoluteContrastProfile initial, ref SigmaContrastProfile? reference)
    {
        var stats = _presentation.GetPresentationRequirements(mat);
        reference ??= _presentation.GetRelativeProfile(initial, stats);
        return _presentation.GetAbsoluteProfile(reference, stats);
    }
}