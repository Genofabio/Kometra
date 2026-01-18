using System;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Astrometry.Solving;
using KomaLab.Models.Fits;

namespace KomaLab.Services.Astrometry;

public interface IPlateSolvingService
{
    /// <summary>
    /// Esegue una diagnosi preventiva sui metadati del file.
    /// Restituisce un oggetto strutturato, non una stringa.
    /// </summary>
    Task<AstrometryDiagnosis> DiagnoseIssuesAsync(string fitsFilePath);

    /// <summary>
    /// Esegue il Plate Solving.
    /// In caso di successo, aggiorna automaticamente la proprietà ModifiedHeader di fileRef.
    /// </summary>
    /// <param name="liveLog">Interfaccia standard per il report dei progressi testuali.</param>
    Task<PlateSolvingResult> SolveFileAsync(
        FitsFileReference fileRef, 
        CancellationToken token = default, 
        IProgress<string>? liveLog = null);
}