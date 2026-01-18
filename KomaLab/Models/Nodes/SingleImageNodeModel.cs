using KomaLab.Models.Fits;

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
        public string ImagePath { get; set; } = string.Empty;
    }
}