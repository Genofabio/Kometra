using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using KomaLab.Services.Astrometry;

namespace KomaLab.Services.Processing.Coordinators;

public class AstrometryCoordinator : IAstrometryCoordinator
{
    private readonly IPlateSolvingService _solver;

    public AstrometryCoordinator(IPlateSolvingService solver)
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

        for (int i = 0; i < total; i++)
        {
            // 1. Controllo cancellazione (Uscita immediata e pulita)
            token.ThrowIfCancellationRequested();

            var fileRef = fileList[i];
            var fileName = Path.GetFileName(fileRef.FilePath);

            // 2. DIAGNOSI PREVENTIVA
            // Verifichiamo se abbiamo RA, DEC e Focale prima di lanciare ASTAP
            var diagnosis = await _solver.DiagnoseIssuesAsync(fileRef.FilePath);

            // Report di "Inizio Lavoro" per il file corrente
            progress.Report(new AstrometryProgressReport
            {
                CurrentFileIndex = i + 1,
                TotalFiles = total,
                FileName = fileName,
                IsStarting = true,
                Diagnosis = diagnosis,
                Message = $"\n>>> Elaborazione: {fileName}"
            });

            // Se mancano metadati critici, segnaliamo l'errore e passiamo al prossimo file
            if (!diagnosis.IsReady)
            {
                progress.Report(new AstrometryProgressReport
                {
                    CurrentFileIndex = i + 1,
                    IsError = true,
                    IsCompleted = true,
                    Message = $"[!] Diagnosi fallita: parametri mancanti ({string.Join(", ", diagnosis.MissingItems)})"
                });
                continue;
            }

            // 3. ESECUZIONE PLATE SOLVING
            // Creiamo un ponte per inoltrare i log testuali di ASTAP in tempo reale
            var liveLogBridge = new Progress<string>(msg => 
                progress.Report(new AstrometryProgressReport { Message = msg }));

            try
            {
                // Chiamata al servizio (che gestisce la sandbox temporanea)
                // Restituisce il TUO oggetto PlateSolvingResult
                PlateSolvingResult result = await _solver.SolveFileAsync(fileRef, token, liveLogBridge);

                // 4. REPORT FINALE DEL FILE
                // Impacchettiamo il PlateSolvingResult nel report di progresso
                progress.Report(new AstrometryProgressReport
                {
                    CurrentFileIndex = i + 1,
                    TotalFiles = total,
                    IsCompleted = true,
                    Success = result.Success,
                    Result = result, // Inseriamo il tuo DTO qui!
                    Message = result.Success 
                        ? $"OK: Risolto con successo." 
                        : $"FALLITO: {result.Message}"
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                progress.Report(new AstrometryProgressReport
                {
                    CurrentFileIndex = i + 1,
                    IsError = true,
                    IsCompleted = true,
                    Message = $"ERRORE CRITICO: {ex.Message}"
                });
            }
        }
    }
}