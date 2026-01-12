using System.Collections.Generic;
using System.Threading.Tasks;

namespace KomaLab.Services.Data;

public interface IFitsBatchService
{
    /// <summary>
    /// Prepara una lista di file per l'uso nella Board, validandoli e ordinandoli cronologicamente.
    /// </summary>
    Task<List<string>> PrepareBatchAsync(IEnumerable<string> paths);

    /// <summary>
    /// Verifica se un gruppo di file è compatibile (stesse dimensioni) per operazioni di stacking.
    /// </summary>
    Task<(bool IsCompatible, string? Error)> ValidateCompatibilityAsync(IEnumerable<string> paths);
}