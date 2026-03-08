using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kometra.Models.Nodes;

namespace Kometra.ViewModels.Nodes;

// ---------------------------------------------------------------------------
// FILE: ConnectionViewModel.cs
// DESCRIZIONE:
// ViewModel che rappresenta una connessione attiva tra due nodi.
// Collega il Model (dati persistenti) con i ViewModel dei nodi (stato vivo).
// Gestisce lo stato di evidenziazione (Highlight) basato sulla selezione dei nodi.
// ---------------------------------------------------------------------------

public partial class ConnectionViewModel : ObservableObject, IDisposable
{
    public ConnectionModel Model { get; }
    public BaseNodeViewModel Source { get; }
    public BaseNodeViewModel Target { get; }

    [ObservableProperty]
    private bool _isHighlighted;

    public ConnectionViewModel(ConnectionModel model, BaseNodeViewModel source, BaseNodeViewModel target)
    {
        Model = model;
        Source = source;
        Target = target;

        // Validazione ID (fondamentale per evitare errori di mappatura caricando da disco)
        if (source.Id != model.SourceNodeId || target.Id != model.TargetNodeId)
        {
            throw new ArgumentException("The provided nodes do not match the IDs in the ConnectionModel.");
        }

        // Sottoscrizione per l'estetica della selezione
        Source.PropertyChanged += OnNodeSelectionChanged;
        Target.PropertyChanged += OnNodeSelectionChanged;
        
        UpdateHighlight();
    }

    private void OnNodeSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BaseNodeViewModel.IsSelected))
        {
            UpdateHighlight();
        }
    }

    private void UpdateHighlight()
    {
        // Se selezioni il padre o il figlio, il filo si illumina. Semplice ed efficace.
        IsHighlighted = Source.IsSelected || Target.IsSelected;
    }
    
    public void Dispose()
    {
        // Pulizia degli eventi per non lasciare "fili appesi" in memoria
        Source.PropertyChanged -= OnNodeSelectionChanged;
        Target.PropertyChanged -= OnNodeSelectionChanged;
    }
}