using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Kometra.Infrastructure;
using Kometra.Models.Export;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Visualization;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.Processing.Rendering;
using OpenCvSharp;

namespace Kometra.Services.ImportExport;

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

            // DETERMINAZIONE PERCORSO DI SCRITTURA (Fix per macOS)
            // Se siamo su Mac, scriviamo in Temp per bypassare i blocchi di sicurezza di AVAssetWriter
            bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            string actualWritePath = settings.OutputPath;
            
            if (isMac)
            {
                string tempFileName = $"kometra_export_{Guid.NewGuid():N}{_formatProvider.GetExtension(settings.Container)}";
                actualWritePath = Path.Combine(Path.GetTempPath(), tempFileName);
                Console.WriteLine($"[Coordinator] macOS rilevato. Scrittura temporanea in: {actualWritePath}");
            }
        
            var filesList = sourceFiles as IReadOnlyList<FitsFileReference> ?? sourceFiles.ToList();
            int total = filesList.Count;
            int current = 0;

            try
            {
                foreach (var fileRef in filesList)
                {
                    token.ThrowIfCancellationRequested();

                    using Mat scientificMat = await LoadFitsToScientificMat(fileRef);
                    if (scientificMat.Empty()) continue; 

                    using Mat scaledMat = ApplyRescale(scientificMat, settings.ScaleFactor);

                    if (!isInitialized)
                    {
                        int fourCc = _formatProvider.GetFourCC(settings.Codec, settings.Container);
                        var bestApi = _formatProvider.GetBestAPI(settings.Container, settings.Codec);

                        _encoder.Initialize(
                            actualWritePath, 
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
                    progress?.Report((double)current / (total + 1) * 100.0);
                }
                
                success = true; 
            }
            catch (OperationCanceledException) { success = false; throw; }
            catch (Exception ex) { success = false; throw new Exception($"Errore export: {ex.Message}", ex); }
            finally
            {
                // 1. Chiusura encoder e rilascio file
                _encoder.Dispose();

                if (success)
                {
                    // 2. Forza il flush fisico
                    await FinalizeAndVerifyDiskSyncAsync(actualWritePath, token);

                    // 3. SPOSTAMENTO FINALE (Solo su Mac)
                    if (isMac && File.Exists(actualWritePath))
                    {
                        try 
                        {
                            Console.WriteLine($"[Coordinator] Spostamento video finale in: {settings.OutputPath}");
                            if (File.Exists(settings.OutputPath)) File.Delete(settings.OutputPath);
                            File.Move(actualWritePath, settings.OutputPath);
                        }
                        catch (Exception ex)
                        {
                            throw new IOException($"Errore durante lo spostamento del file video finale: {ex.Message}", ex);
                        }
                    }
                
                    progress?.Report(100.0);
                }
                else if (File.Exists(actualWritePath))
                {
                    try { await Task.Delay(200); File.Delete(actualWritePath); } catch { }
                }
            }
        }, token);
    }

    private async Task FinalizeAndVerifyDiskSyncAsync(string filePath, CancellationToken token)
    {
        const int maxRetries = 15;
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
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    fs.Flush(flushToDisk: true);
                    if (fs.Length > 0) return; 
                }
            }
            catch (IOException) { }

            await Task.Delay(500, token);
        }
    }

    private async Task<Mat> LoadFitsToScientificMat(FitsFileReference fileRef)
    {
        var data = await _dataManager.GetDataAsync(fileRef.FilePath);
        var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
        if (imageHdu == null) return new Mat();

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

        return _converter.RawToMat(
            imageHdu.PixelData, 
            _metadata.GetDoubleValue(header, "BSCALE", 1.0), 
            _metadata.GetDoubleValue(header, "BZERO", 0.0), 
            depth);
    }

    private Mat ApplyRescale(Mat source, double factor)
    {
        int rawW = (int)(source.Width * factor);
        int rawH = (int)(source.Height * factor);

        // Allineamento a multipli di 32 (Fondamentale per chip M1/M2/M3)
        int w = (rawW / 32) * 32;
        int h = (rawH / 32) * 32;

        w = Math.Max(32, w);
        h = Math.Max(32, h);

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