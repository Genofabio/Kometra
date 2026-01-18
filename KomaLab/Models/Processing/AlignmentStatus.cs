namespace KomaLab.Models.Processing;

public enum AlignmentStatus
{
    Idle,        // Pronto / In attesa
    Running,     // Analisi o Salvataggio in corso
    Success,     // Analisi completata / Salvataggio riuscito
    Warning,     // Risolto con qualche incertezza
    Error,       // Fallimento o Errore tecnico
    Cancelled    // Interrotto dall'utente
}