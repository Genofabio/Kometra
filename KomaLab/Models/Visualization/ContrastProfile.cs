namespace KomaLab.Models.Visualization;

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
/// <param name="BlackADU">Il valore del nero in unità ADU (es. 450).</param>
/// <param name="WhiteADU">Il valore del bianco in unità ADU (es. 20000).</param>
public record AbsoluteContrastProfile(double BlackADU, double WhiteADU) : ContrastProfile;

/// <summary>
/// Profilo Relativo: I valori sono ESPLICITAMENTE relativi (percentuali o sigma).
/// </summary>
/// <param name="LowerPercentile">Soglia inferiore relativa (es. 0.05 per 5%).</param>
/// <param name="UpperPercentile">Soglia superiore relativa (es. 0.99 per 99%).</param>
public record RelativeContrastProfile(double LowerPercentile, double UpperPercentile) : ContrastProfile;