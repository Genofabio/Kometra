using System.Collections.Generic;

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
        /// <summary>
        /// Elenco dei percorsi (URI) delle immagini incluse in questo nodo.
        /// Supporta percorsi assoluti su disco o URI specifici del framework 
        /// (es. 'avares://' per risorse incorporate in Avalonia/WPF).
        /// </summary>
        public List<string> ImagePaths { get; set; } = new();
    }
}