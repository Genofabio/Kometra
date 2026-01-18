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
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
}