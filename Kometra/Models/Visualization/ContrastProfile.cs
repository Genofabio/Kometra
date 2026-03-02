namespace Kometra.Models.Visualization;

// ---------------------------------------------------------------------------
// FILE: ContrastProfile.cs
// DESCRIZIONE:
// Definisce i profili di contrasto utilizzando una gerarchia di tipi.
// Questo impedisce l'uso accidentale di valori relativi in contesti assoluti.
// ---------------------------------------------------------------------------

/// <summary>
/// Record base astratto (marker).
/// Non può essere istanziato direttamente. Serve a indicare che un oggetto è un profilo.
/// </summary>
public abstract record ContrastProfile;

/// <summary>
/// Profilo Assoluto: I valori sono ESPLICITAMENTE in ADU (es. pixel values).
/// </summary>
/// <param name="BlackAdu">Il valore del nero in unità ADU (es. 450).</param>
/// <param name="WhiteAdu">Il valore del bianco in unità ADU (es. 20000).</param>
public record AbsoluteContrastProfile(double BlackAdu, double WhiteAdu) : ContrastProfile;

public record SigmaContrastProfile(double KBlack, double KWhite) : ContrastProfile;

