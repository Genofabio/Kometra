namespace Kometra.Models.Astrometry.Solving;

/// <summary>
/// Oggetto di trasporto per il progresso dell'astrometria.
/// Unisce i metadati della coda con l'esito scientifico (PlateSolvingResult).
/// </summary>
public class AstrometryProgressReport
{
    // --- Metadati della Coda ---
    public int CurrentFileIndex { get; set; }
    public int TotalFiles { get; set; }
    public string? FileName { get; set; }

    // --- Stream di Messaggi ---
    public string? Message { get; set; }
    
    // --- Stati di Controllo (Flag per la UI) ---
    public bool IsStarting { get; set; }    // Inizio analisi di un nuovo file
    public bool IsCompleted { get; set; }   // Fine elaborazione del file corrente
    public bool IsError { get; set; }       // Errore bloccante o fallimento critico
    
    // Questa è la proprietà che mancava!
    public bool Success { get; set; }       // Esito sintetico del solving

    // --- Dati Dettagliati (DTOs) ---
    public AstrometryDiagnosis? Diagnosis { get; set; }
    public PlateSolvingResult? Result { get; set; } // Il tuo DTO originale con log e header
}