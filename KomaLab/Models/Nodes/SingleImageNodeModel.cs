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

        /// <summary>
        /// Contiene l'header modificato durante la sessione ma non ancora applicato al file su disco.
        /// Se null, significa che non ci sono modifiche pendenti.
        /// </summary>
        public FitsHeader? TempHeader { get; set; }
    }
}