using System;

namespace KomaLab.Models.Nodes;

// ---------------------------------------------------------------------------
// FILE: BaseNodeModel.cs
// DESCRIZIONE:
// Classe base astratta per i dati persistenti dei nodi.
// Contiene l'identificativo univoco (ID) necessario per ricostruire le
// connessioni dopo il caricamento da disco.
// ---------------------------------------------------------------------------

public abstract class BaseNodeModel
{
    /// <summary>
    /// Identificativo univoco del nodo.
    /// Viene generato automaticamente alla creazione.
    /// Fondamentale per mappare le connessioni (ConnectionModel) durante il salvataggio/caricamento.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Posizione orizzontale sulla Board (Left).
    /// </summary>
    public double X { get; set; }

    /// <summary>
    /// Posizione verticale sulla Board (Top).
    /// </summary>
    public double Y { get; set; }

    /// <summary>
    /// Titolo visualizzato nella barra del titolo del nodo/finestra.
    /// </summary>
    public string Title { get; set; } = string.Empty;
}