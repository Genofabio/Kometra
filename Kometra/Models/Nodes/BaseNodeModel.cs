using System;

namespace Kometra.Models.Nodes;

// ---------------------------------------------------------------------------
// FILE: BaseNodeModel.cs
// DESCRIZIONE:
// Classe base astratta per i dati persistenti dei nodi.
// Contiene l'identificativo univoco (ID) necessario per ricostruire le
// connessioni dopo il caricamento da disco.
// ---------------------------------------------------------------------------

public abstract class BaseNodeModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; }
    public double Y { get; set; }
    public string Title { get; set; } = string.Empty;
}