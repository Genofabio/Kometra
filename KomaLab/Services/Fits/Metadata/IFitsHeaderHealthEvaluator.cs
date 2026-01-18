using KomaLab.Models.Fits;

namespace KomaLab.Services.Fits.Metadata;

/// <summary>
/// Definisce il contratto per l'analisi della "salute" scientifica di un header FITS.
/// </summary>
public interface IFitsHeaderHealthEvaluator
{
    /// <summary>
    /// Esegue una diagnosi completa dell'header fornito.
    /// </summary>
    HeaderHealthReport Evaluate(FitsHeader header);
}