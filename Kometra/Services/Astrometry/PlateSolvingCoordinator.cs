using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Astrometry.Solving;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;

namespace Kometra.Services.Astrometry;

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

        try
        {
            for (int i = 0; i < total; i++)
            {
                // Verifica interruzione all'inizio di ogni iterazione
                token.ThrowIfCancellationRequested();

                var fileRef = fileList[i];
                var fileName = Path.GetFileName(fileRef.FilePath);

                // 1. NOTIFICA INIZIO
                progress.Report(new AstrometryProgressReport
                {
                    CurrentFileIndex = i + 1,
                    TotalFiles = total,
                    FileName = fileName,
                    IsStarting = true,
                    Message = fileName
                });

                // 2. DIAGNOSI
                var diagnosis = await _solver.DiagnoseIssuesAsync(fileRef);

                if (!diagnosis.IsReady)
                {
                    progress.Report(new AstrometryProgressReport
                    {
                        CurrentFileIndex = i + 1,
                        IsError = true,
                        IsCompleted = true,
                        Message = $"SKIP:{string.Join(", ", diagnosis.MissingItems)}"
                    });
                    continue;
                }

                // 3. ESECUZIONE
                var liveLogBridge = new Progress<string>(msg => 
                    progress.Report(new AstrometryProgressReport { Message = msg }));

                try
                {
                    PlateSolvingResult result = await _solver.SolveFileAsync(fileRef, token, liveLogBridge);

                    if (result.Success && result.SolvedHeader != null)
                    {
                        _sessionCache[fileRef] = result.SolvedHeader;
                        successCount++;

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
                        // Se siamo stati cancellati durante l'esecuzione di SolveFileAsync,
                        // evitiamo di riportare un fallimento generico perché seguirà il segnale di interruzione.
                        if (!token.IsCancellationRequested)
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
                }
                catch (OperationCanceledException)
                {
                    // Rilanciamo per gestire l'uscita pulita dal loop principale
                    throw;
                }
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
        }
        catch (OperationCanceledException)
        {
            // Notifichiamo l'annullamento dell'intera sessione
            progress.Report(new AstrometryProgressReport
            {
                Message = "EVENT:CANCELLED"
            });
            throw;
        }
        finally
        {
            // 4. FINE SESSIONE 
            // Essendo nel finally, questo è garantito essere l'ultimo report inviato,
            // sia in caso di successo totale, sia in caso di errore o cancellazione.
            progress.Report(new AstrometryProgressReport 
            { 
                Message = $"SUMMARY:{successCount}:{total}" 
            });
        }
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