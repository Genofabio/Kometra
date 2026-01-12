using System;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Nodes; // Assumendo che BaseNodeModel sia qui

namespace KomaLab.ViewModels.Nodes;

// ---------------------------------------------------------------------------
// FILE: BaseNodeViewModel.cs
// RUOLO: ViewModel Astratto per Nodi
// DESCRIZIONE:
// Classe base per tutti i nodi del grafo.
// Responsabilità:
// 1. Wrapping del Model (Bridge Pattern): Espone i dati del Model alla View senza duplicarli (Low RAM).
// 2. Stato Visuale: Gestisce selezione, posizione (X,Y) e Z-Index.
// 3. Gestione Ciclo di Vita: Implementa IDisposable per pulire gli eventi e prevenire Memory Leaks.
// ---------------------------------------------------------------------------

public abstract partial class BaseNodeViewModel : ObservableObject, IDisposable
{
    // --- Stato Interno ---
    private bool _isDisposed;
    
    // Il Model è readonly: l'istanza non cambia, ma le sue proprietà sì.
    protected readonly BaseNodeModel Model;

    // --- AGGIUNTA FONDAMENTALE ---
    // Espone l'ID del Model all'esterno in sola lettura.
    // Necessario per trovare il nodo quando si caricano le connessioni.
    public Guid Id => Model.Id;

    // --- Eventi (Comunicazione leggera verso il Parent/Board) ---
    public event Action<BaseNodeViewModel>? RequestRemove;
    public event Action<BaseNodeViewModel>? RequestBringToFront;

    // --- Proprietà Proxy (Model Wrapping) ---
    // Scrivono direttamente sul Model e notificano la View.

    public double X
    {
        get => Model.X;
        set => SetProperty(Model.X, value, Model, (m, v) => m.X = v);
    }

    public double Y
    {
        get => Model.Y;
        set => SetProperty(Model.Y, value, Model, (m, v) => m.Y = v);
    }

    public string Title
    {
        get => Model.Title;
        set => SetProperty(Model.Title, value, Model, (m, v) => m.Title = v);
    }
    
    // --- Proprietà Visuali (Stato Transitorio UI) ---
    // Questi dati vivono solo nella RAM (non salvati nel Model).

    [ObservableProperty] 
    private double _visualOffsetX;

    [ObservableProperty] 
    private double _visualOffsetY;
    
    [ObservableProperty] 
    private bool _isSelected;

    [ObservableProperty] 
    private int _zIndex;

    // Dimensione stimata (virtuale, sovrascrivibile dai nodi specifici)
    public virtual Size EstimatedTotalSize => new(0, 0);

    // --- Costruttore ---

    protected BaseNodeViewModel(BaseNodeModel model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
    }

    // --- Comandi ---

    [RelayCommand]
    private void RemoveSelf()
    {
        // Chiede al CanvasViewModel di rimuovere questo nodo
        RequestRemove?.Invoke(this);
    }

    public void BringToFront()
    {
        RequestBringToFront?.Invoke(this);
    }

    // --- Implementazione IDisposable ---
    
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this); 
    }
    
    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                // Taglia i ponti con il padre per evitare Memory Leaks
                RequestRemove = null;
                RequestBringToFront = null;
            }
            _isDisposed = true;
        }
    }
}