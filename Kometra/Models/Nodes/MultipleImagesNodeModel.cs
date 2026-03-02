using System.Collections.Generic;
using Kometra.Models.Fits;

namespace Kometra.Models.Nodes
{
    // ---------------------------------------------------------------------------
    // FILE: MultipleImagesNodeModel.cs
    // DESCRIZIONE:
    // Modello dati per un nodo di tipo "Stacking" o "Sequenza".
    // Mantiene la lista dei riferimenti ai file immagine da elaborare.
    // Non contiene i dati binari delle immagini, ma solo i puntatori (percorsi).
    // ---------------------------------------------------------------------------

    public class MultipleImagesNodeModel : BaseNodeModel
    {
        public List<string> ImagePaths { get; set; } = new();
    }
}