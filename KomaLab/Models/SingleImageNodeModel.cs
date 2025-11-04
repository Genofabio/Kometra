namespace KomaLab.Models;

/// <summary>
/// Modello per un nodo che gestisce una singola immagine.
/// </summary>
public class SingleImageNodeModel : BaseNodeModel
{
    /// <summary>
    /// Il percorso (su disco o 'avares://') dell'immagine da caricare.
    /// </summary>
    public string ImagePath { get; set; } = "";
}