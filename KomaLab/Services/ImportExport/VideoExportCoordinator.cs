using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Infrastructure;
using KomaLab.Models.Export;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Rendering;
using OpenCvSharp;

namespace KomaLab.Services.ImportExport;

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

                    // Caricamento frame scientifico (Mat float/double)
                    // Nota: LoadFitsToScientificMat ora gestisce MEF internamente
                    using Mat scientificMat = await LoadFitsToScientificMat(fileRef);
                    
                    if (scientificMat.Empty()) 
                    {
                        // Skip se il frame è vuoto o non valido
                        continue; 
                    }

                    using Mat scaledMat = ApplyRescale(scientificMat, settings.ScaleFactor);

                    if (!isInitialized)
                    {
                        int fourCc = _formatProvider.GetFourCC(settings.Codec, settings.Container);
                        var bestApi = _formatProvider.GetBestAPI(settings.Container, settings.Codec);

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
                
                    if (frame8Bit.Empty()) continue;

                    using Mat colorFrame = new Mat();
                    Cv2.CvtColor(frame8Bit, colorFrame, ColorConversionCodes.GRAY2BGR);

                    _encoder.WriteFrame(colorFrame);

                    current++;
                    // Lasciamo lo spazio visivo per la sincronizzazione
                    progress?.Report((double)current / (total + 1) * 100.0);
                }
                
                success = true; 
            }
            catch (OperationCanceledException) { success = false; throw; }
            catch (Exception ex) { success = false; throw new Exception($"Errore export: {ex.Message}", ex); }
            finally
            {
                // 1. CHIUSURA ENCODER (Rilascia l'handle della libreria video)
                _encoder.Dispose();

                if (success)
                {
                    // 2. FORZA IL FLUSH FISICO E VERIFICA L'INTEGRITÀ (Cross-Platform)
                    await FinalizeAndVerifyDiskSyncAsync(settings.OutputPath, token);
                
                    // 3. ORA È COMPLETATO AL 100%
                    progress?.Report(100.0);
                }
                else if (!string.IsNullOrEmpty(settings.OutputPath) && File.Exists(settings.OutputPath))
                {
                    try { await Task.Delay(200); File.Delete(settings.OutputPath); } catch { }
                }
            }
        }, token);
    }

    /// <summary>
    /// Forza la scrittura fisica dei buffer su disco usando metodi .NET cross-platform
    /// e verifica che il file sia realmente accessibile e completo.
    /// </summary>
    private async Task FinalizeAndVerifyDiskSyncAsync(string filePath, CancellationToken token)
    {
        const int maxRetries = 20;
        for (int i = 0; i < maxRetries; i++)
        {
            token.ThrowIfCancellationRequested();

            if (!File.Exists(filePath)) 
            { 
                await Task.Delay(500, token); 
                continue; 
            }

            try
            {
                // Apriamo il file in modalità esclusiva (FileShare.None)
                // Se l'OS o un encoder asincrono lo stanno ancora toccando, questo fallisce.
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // IL CUORE DEL FIX: Flush(true) forza lo svuotamento dei buffer 
                    // del file system verso l'hardware fisico del disco.
                    fs.Flush(flushToDisk: true);

                    // VERIFICA DI REALTÀ: 
                    // Se riusciamo a leggere l'ultimo byte, la finalizzazione dell'indice è avvenuta.
                    if (fs.Length > 0)
                    {
                        fs.Seek(-1, SeekOrigin.End);
                        fs.ReadByte(); 
                        
                        // Se arriviamo qui senza eccezioni, il file è stabile.
                        return; 
                    }
                }
            }
            catch (IOException)
            {
                // Il sistema operativo o il backend video non hanno ancora rilasciato il file.
                // Aspettiamo e riproviamo.
            }

            await Task.Delay(500, token);
        }
    }

    // --- Metodi Helper ---

    private async Task<Mat> LoadFitsToScientificMat(FitsFileReference fileRef)
    {
        var data = await _dataManager.GetDataAsync(fileRef.FilePath);
        
        // [MODIFICA MEF] Accesso sicuro all'HDU immagine
        var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
        if (imageHdu == null) 
        {
            // Restituisce una Mat vuota se non ci sono immagini, che verrà gestita dal loop principale
            return new Mat();
        }

        // Priorità Header: RAM > HDU
        var header = fileRef.ModifiedHeader ?? imageHdu.Header;
        
        int bitpix = (int)_metadata.GetDoubleValue(header, "BITPIX", 16);
        FitsBitDepth depth = bitpix switch { 
            8 => FitsBitDepth.UInt8, 
            16 => FitsBitDepth.Int16, 
            32 => FitsBitDepth.Int32, 
            -32 => FitsBitDepth.Float, 
            -64 => FitsBitDepth.Double, 
            _ => FitsBitDepth.Int16 
        };

        // Usiamo i PixelData dell'HDU
        return _converter.RawToMat(
            imageHdu.PixelData, 
            _metadata.GetDoubleValue(header, "BSCALE", 1.0), 
            _metadata.GetDoubleValue(header, "BZERO", 0.0), 
            depth);
    }

    private Mat ApplyRescale(Mat source, double factor)
    {
        int w = (int)(source.Width * factor) & ~1;
        int h = (int)(source.Height * factor) & ~1;
        if (w == source.Width && h == source.Height) return source.Clone();
        Mat resized = new Mat();
        Cv2.Resize(source, resized, new Size(w, h), 0, 0, InterpolationFlags.Area);
        return resized;
    }

    private AbsoluteContrastProfile GetAdaptiveProfile(Mat mat, AbsoluteContrastProfile initial, ref SigmaContrastProfile? reference)
    {
        var stats = _presentation.GetPresentationRequirements(mat);
        reference ??= _presentation.GetRelativeProfile(initial, stats);
        return _presentation.GetAbsoluteProfile(reference, stats);
    }
}