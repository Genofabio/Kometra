using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models.Nodes;

namespace KomaLab.ViewModels.Nodes;

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

        if (source.Id != model.SourceNodeId || target.Id != model.TargetNodeId)
        {
            throw new ArgumentException("I nodi forniti non corrispondono agli ID nel ConnectionModel.");
        }

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
        IsHighlighted = Source.IsSelected || Target.IsSelected;
    }
    
    public void Dispose()
    {
        Source.PropertyChanged -= OnNodeSelectionChanged;
        Target.PropertyChanged -= OnNodeSelectionChanged;
        GC.SuppressFinalize(this);
    }
}