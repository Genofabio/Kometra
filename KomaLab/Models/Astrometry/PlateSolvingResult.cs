namespace KomaLab.Models.Astrometry;

// ---------------------------------------------------------------------------
// FILE: PlateSolvingResult.cs
// DESCRIZIONE:
// DTO (Data Transfer Object) che rappresenta l'esito di un'operazione di
// Plate Solving. Contiene lo stato di successo, messaggi utente e il log
// completo del processo (utile per debug o console output).
// ---------------------------------------------------------------------------

public class PlateSolvingResult
{
    /// <summary>
    /// Indica se il plate solving ha avuto successo (WCS calcolato).
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Messaggio sintetico per l'utente (es. "Timeout", "ASTAP non trovato", "OK").
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Output completo (StdOut + StdErr) del processo di solving.
    /// </summary>
    public string FullLog { get; set; } = string.Empty;
}