namespace KomaLab.Models.Nodes
{
    // ---------------------------------------------------------------------------
    // FILE: SingleImageNodeModel.cs
    // DESCRIZIONE:
    // Modello dati per un nodo che contiene una singola risorsa immagine.
    // Utilizzato per visualizzatori semplici, immagini di riferimento o master frame.
    // ---------------------------------------------------------------------------

    public class SingleImageNodeModel : BaseNodeModel
    {
        /// <summary>
        /// Percorso assoluto o relativo (URI) della singola immagine associata al nodo.
        /// </summary>
        public string ImagePath { get; set; } = string.Empty;
    }
}