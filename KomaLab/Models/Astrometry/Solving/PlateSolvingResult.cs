using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Models.Astrometry.Solving;

// ---------------------------------------------------------------------------
// FILE: PlateSolvingResult.cs
// DESCRIZIONE:
// DTO (Data Transfer Object) che rappresenta l'esito di un'operazione di
// Plate Solving. Contiene lo stato di successo, messaggi utente e il log
// completo del processo (utile per debug o console output).
// ---------------------------------------------------------------------------

public class PlateSolvingResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string FullLog { get; set; } = "";
    
    public FitsHeader? SolvedHeader { get; set; } 
}