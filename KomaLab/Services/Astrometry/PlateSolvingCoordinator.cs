using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry.Solving;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Astrometry;

public class PlateSolvingCoordinator : IPlateSolvingCoordinator
{
    private readonly IPlateSolvingService _solver;
    private readonly Dictionary<FitsFileReference, FitsHeader> _sessionCache = new();

    public PlateSolvingCoordinator(IPlateSolvingService solver)
    {
        _solver = solver ?? throw new ArgumentNullException(nameof(solver));
    }

    public async Task SolveSequenceAsync(
        IEnumerable<FitsFileReference> files,
        IProgress<AstrometryProgressReport> progress,
        CancellationToken token)
    {
        var fileList = files.ToList();
        int total = fileList.Count;
        int successCount = 0;

        for (int i = 0; i < total; i++)
        {
            token.ThrowIfCancellationRequested();

            var fileRef = fileList[i];
            var fileName = Path.GetFileName(fileRef.FilePath);

            // 1. NOTIFICA INIZIO (Solo Dati)
            // Non inviamo più la stringa "--- [FILE 1/10] ---"
            progress.Report(new AstrometryProgressReport
            {
                CurrentFileIndex = i + 1,
                TotalFiles = total,
                FileName = fileName,
                IsStarting = true,
                Message = fileName // Il VM userà questo per creare l'header grafico
            });

            // 2. DIAGNOSI
            var diagnosis = await _solver.DiagnoseIssuesAsync(fileRef);

            if (!diagnosis.IsReady)
            {
                // Tagghiamo il messaggio come SKIP affinché il VM sappia come formattarlo
                progress.Report(new AstrometryProgressReport
                {
                    CurrentFileIndex = i + 1,
                    IsError = true,
                    IsCompleted = true,
                    Message = $"SKIP:{string.Join(", ", diagnosis.MissingItems)}"
                });
                continue;
            }

            // 3. ESECUZIONE (Ponte per i log taggati CONFIG: e TOOL:)
            var liveLogBridge = new Progress<string>(msg => 
                progress.Report(new AstrometryProgressReport { Message = msg }));

            try
            {
                PlateSolvingResult result = await _solver.SolveFileAsync(fileRef, token, liveLogBridge);

                if (result.Success && result.SolvedHeader != null)
                {
                    _sessionCache[fileRef] = result.SolvedHeader;
                    successCount++;

                    // Notifichiamo il successo senza costruire la stringa WCS qui
                    progress.Report(new AstrometryProgressReport
                    {
                        CurrentFileIndex = i + 1,
                        TotalFiles = total,
                        IsCompleted = true,
                        Success = true,
                        Result = result,
                        Message = "STATUS:SUCCESS" 
                    });
                }
                else
                {
                    progress.Report(new AstrometryProgressReport
                    {
                        CurrentFileIndex = i + 1,
                        TotalFiles = total,
                        IsCompleted = true,
                        Success = false,
                        Result = result,
                        Message = $"STATUS:FAIL:{result.Message}"
                    });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                progress.Report(new AstrometryProgressReport
                {
                    CurrentFileIndex = i + 1,
                    IsError = true,
                    IsCompleted = true,
                    Message = $"SYSTEM_ERROR:{ex.Message}"
                });
            }
        }

        // 4. FINE SESSIONE (Solo dati grezzi per il riepilogo)
        progress.Report(new AstrometryProgressReport 
        { 
            Message = $"SUMMARY:{successCount}:{total}" 
        });
    }

    public void ApplyResults()
    {
        foreach (var kvp in _sessionCache)
        {
            kvp.Key.ModifiedHeader = kvp.Value;
        }
        _sessionCache.Clear();
    }

    public void ClearSession() => _sessionCache.Clear();

    public IReadOnlyDictionary<FitsFileReference, FitsHeader> GetPendingResults() 
        => _sessionCache;
}