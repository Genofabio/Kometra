using CommunityToolkit.Mvvm.ComponentModel;
using Kometra.Models.Fits.Health;
using Kometra.Infrastructure; // Aggiunto per localizzazione

namespace Kometra.ViewModels.Fits;

/// <summary>
/// Wrapper di presentazione per un singolo controllo di salute.
/// Isola le stringhe UI e la logica di ordinamento dal modello scientifico.
/// </summary>
public class HealthStatusPresenter : ObservableObject
{
    private readonly HealthStatusItem _model;

    public HealthStatusPresenter(HealthStatusItem model)
    {
        _model = model;
    }

    // Proprietà pass-through dal modello
    public HeaderHealthStatus Status => _model.Status;
    public string Message => _model.Message;
    public HealthCheckType Type => _model.Type;

    /// <summary>
    /// Titolo della Card mostrato nella UI.
    /// </summary>
    public string Title => _model.Type switch
    {
        HealthCheckType.TimeReference => LocalizationManager.Instance["HealthTitleTime"],
        HealthCheckType.ObservatoryLocation => LocalizationManager.Instance["HealthTitleLocation"],
        HealthCheckType.OpticalConfiguration => LocalizationManager.Instance["HealthTitleOptics"],
        HealthCheckType.TargetPointers => LocalizationManager.Instance["HealthTitlePointers"],
        HealthCheckType.AstrometricSolution => LocalizationManager.Instance["HealthTitleAstrometry"],
        _ => LocalizationManager.Instance["HealthTitleUnknown"]
    };

    /// <summary>
    /// Descrizione dettagliata mostrata al passaggio del mouse (Tooltip).
    /// </summary>
    public string Tooltip => _model.Type switch
    {
        HealthCheckType.TimeReference => LocalizationManager.Instance["HealthTooltipTime"],
        HealthCheckType.ObservatoryLocation => LocalizationManager.Instance["HealthTooltipLocation"],
        HealthCheckType.OpticalConfiguration => LocalizationManager.Instance["HealthTooltipOptics"],
        HealthCheckType.TargetPointers => LocalizationManager.Instance["HealthTooltipPointers"],
        HealthCheckType.AstrometricSolution => LocalizationManager.Instance["HealthTooltipAstrometry"],
        _ => LocalizationManager.Instance["HealthTooltipNone"]
    };

    /// <summary>
    /// Logica di ordinamento: gli errori (Invalid) devono salire in cima.
    /// Ordine gravità (decrescente): Invalid (3), Warning (2), Valid (1), Pending (0).
    /// A parità di gravità, usa l'ordine logico predefinito dell'Enum HealthCheckType.
    /// </summary>
    public int SortPriority
    {
        get
        {
            // Valore di base basato sulla gravità dello stato
            int gravityScore = Status switch
            {
                HeaderHealthStatus.Invalid => 1000,
                HeaderHealthStatus.Warning => 500,
                HeaderHealthStatus.Valid => 100,
                _ => 0
            };

            // Sottraiamo il tipo per mantenere l'ordine logico (0-4) all'interno dello stesso livello di gravità
            return gravityScore - (int)_model.Type;
        }
    }
}