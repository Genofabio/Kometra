using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models.Fits.Health;

namespace KomaLab.ViewModels.Fits;

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
        HealthCheckType.TimeReference => "Riferimento Temporale",
        HealthCheckType.ObservatoryLocation => "Geolocalizzazione Sito",
        HealthCheckType.OpticalConfiguration => "Configurazione Ottica",
        HealthCheckType.TargetPointers => "Puntamento Target",
        HealthCheckType.AstrometricSolution => "Soluzione Astrometrica",
        _ => "Controllo Sconosciuto"
    };

    /// <summary>
    /// Descrizione dettagliata mostrata al passaggio del mouse (Tooltip).
    /// </summary>
    public string Tooltip => _model.Type switch
    {
        HealthCheckType.TimeReference => 
            "Verifica le chiavi DATE-OBS o DATE. Indica il momento esatto dell'acquisizione (UT), indispensabile per il calcolo del tempo siderale e delle effemeridi.",
        
        HealthCheckType.ObservatoryLocation => 
            "Verifica le coordinate geografiche (SITELAT/LONG). Necessario per correggere la parallasse e calcolare le coordinate locali (Alt/Az).",
        
        HealthCheckType.OpticalConfiguration => 
            "Verifica FOCALLEN e PIXSIZE. Questi dati definiscono la scala dell'immagine (arcsec/pixel), parametro critico per qualsiasi analisi astrometrica.",
        
        HealthCheckType.TargetPointers => 
            "Verifica le coordinate RA/DEC inviate dalla montatura. Agiscono come suggerimento (hints) per velocizzare drasticamente il Plate Solving.",
        
        HealthCheckType.AstrometricSolution => 
            "Verifica la presenza di una soluzione WCS valida (CRVAL/CD Matrix). Indica se l'immagine è già stata mappata correttamente sulla sfera celeste.",
        
        _ => "Nessuna informazione aggiuntiva disponibile."
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