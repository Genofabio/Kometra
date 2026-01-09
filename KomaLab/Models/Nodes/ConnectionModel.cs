using System;

namespace KomaLab.Models.Nodes;

// ---------------------------------------------------------------------------
// FILE: ConnectionModel.cs
// DESCRIZIONE:
// Rappresenta un collegamento tra due nodi nel grafo.
// Utilizza gli ID (Guid) per mantenere i riferimenti anche dopo il salvataggio/caricamento.
// ---------------------------------------------------------------------------

public class ConnectionModel
{
    /// <summary>
    /// L'ID del nodo da cui parte il collegamento (Output).
    /// </summary>
    public Guid SourceNodeId { get; set; }

    /// <summary>
    /// L'ID del nodo a cui arriva il collegamento (Input).
    /// </summary>
    public Guid TargetNodeId { get; set; }
}