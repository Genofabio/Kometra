namespace KomaLab.Models.Astrometry.Solving;

public enum PlateSolvingStatus
{
    Idle,           // Pronto
    Running,        // In esecuzione
    Success,        // Risolto perfettamente
    PartialSuccess, // Risolto ma con bassa confidenza o warning
    Failed,         // Fallito (nessuna soluzione)
    Cancelled,      // Interrotto dall'utente
    Error           // Errore tecnico (eccezione/crash)
}