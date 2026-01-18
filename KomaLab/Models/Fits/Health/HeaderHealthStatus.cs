namespace KomaLab.Models.Fits.Health;

/// <summary>
/// Rappresenta lo stato di salute di uno specifico gruppo di metadati FITS.
/// </summary>
public enum HeaderHealthStatus
{
    /// <summary> L'analisi non è ancora stata eseguita o l'header è vuoto. </summary>
    Pending,
    
    /// <summary> Il dato è presente e formalmente corretto. </summary>
    Valid,
    
    /// <summary> Il dato è presente ma presenta incongruenze o valori fuori norma. </summary>
    Warning,
    
    /// <summary> Il dato è del tutto assente o palesemente errato. </summary>
    Invalid
}