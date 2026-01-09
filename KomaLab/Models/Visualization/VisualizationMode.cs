namespace KomaLab.Models.Visualization;

// ---------------------------------------------------------------------------
// FILE: VisualizationMode.cs
// DESCRIZIONE:
// Elenco delle funzioni di trasferimento (Transfer Functions) supportate
// per convertire i dati grezzi in livelli di luminosità visualizzabili.
// ---------------------------------------------------------------------------

/// <summary>
/// Definisce l'algoritmo matematico usato per lo "stretching" dell'istogramma.
/// </summary>
public enum VisualizationMode
{
    /// <summary>
    /// Mappatura Lineare standard.
    /// <para>Formula: <c>Out = (Val - Black) / (White - Black)</c></para>
    /// Mantiene fedelmente i rapporti di luminosità. Ideale per immagini planetarie o con range dinamico ridotto.
    /// </summary>
    Linear,

    /// <summary>
    /// Mappatura Logaritmica.
    /// <para>Formula: <c>Out = Log(1 + Val)</c> (scalato)</para>
    /// Comprime le alte luci per esaltare i dettagli deboli. Fondamentale per oggetti Deep Sky (nebulose, galassie) con nuclei molto luminosi.
    /// </summary>
    Logarithmic,

    /// <summary>
    /// Mappatura a Radice Quadrata.
    /// <para>Formula: <c>Out = Sqrt(Val)</c> (scalato)</para>
    /// Un compromesso tra Lineare e Logaritmico. Simile alla risposta dell'occhio umano (Gamma correction).
    /// </summary>
    SquareRoot
}