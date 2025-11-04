using System.Collections.Generic;

namespace KomaLab.Models;

/// <summary>
/// Modello per un nodo che gestisce una "pila" di immagini.
/// </summary>
public class MultipleImagesNodeModel : BaseNodeModel
{
    /// <summary>
    /// La lista dei percorsi (su disco o 'avares://') 
    /// delle immagini nella pila.
    /// </summary>
    public List<string> ImagePaths { get; set; } = new();
}