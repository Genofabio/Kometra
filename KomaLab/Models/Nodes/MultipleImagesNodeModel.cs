using System.Collections.Generic;
using KomaLab.Models.Fits;

namespace KomaLab.Models.Nodes
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

        /// <summary>
        /// Mappa "Percorso File" -> "Header Modificato".
        /// Conserva le modifiche WCS/Metadata fatte in RAM per ripristinarle al caricamento del progetto.
        /// </summary>
        public Dictionary<string, FitsHeader> TempHeaders { get; set; } = new();
    }
}