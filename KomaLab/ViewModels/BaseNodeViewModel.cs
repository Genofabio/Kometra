using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using System.Collections.Generic; // Per List
using System.Threading.Tasks; // Per Task

namespace KomaLab.ViewModels;

/// <summary>
/// Classe base astratta per qualsiasi oggetto che può essere
/// posizionato, selezionato e trascinato sulla BoardView.
/// </summary>
public abstract partial class BaseNodeViewModel : ObservableObject
{
    // --- Campi ---
    protected const double ESTIMATED_UI_HEIGHT = 60.0;
    protected readonly BaseNodeModel Model; 
    public BoardViewModel ParentBoard { get; }

    // --- Proprietà (Posizione e Stato) ---
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

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private bool _isSelected;
    
    public virtual Size EstimatedTotalSize
    {
        get
        {
            var contentSize = this.NodeContentSize;
            return new Size(
                contentSize.Width, 
                contentSize.Height + ESTIMATED_UI_HEIGHT);
        }
    }
    protected abstract Size NodeContentSize { get; }
    
    public abstract Task ResetThresholdsAsync();
    
    /// <summary>
    /// Recupera la lista dei dati FITS *attualmente* in memoria per questo nodo.
    /// (Saranno i dati originali o quelli già processati).
    /// </summary>
    public abstract Task<List<FitsImageData?>> GetCurrentDataAsync();

    /// <summary>
    /// Sostituisce i dati in memoria del nodo con una nuova lista di dati processati.
    /// </summary>
    public abstract Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData);

    // --- Costruttore ---
    protected BaseNodeViewModel(BoardViewModel parentBoard, BaseNodeModel model)
    {
        ParentBoard = parentBoard;
        Model = model; 
        Title = model.Title;
        
        this.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NodeContentSize))
            {
                OnPropertyChanged(nameof(EstimatedTotalSize));
            }
        };
    }

    // --- Comandi ---
    [RelayCommand]
    private void RemoveSelf()
    {
        ParentBoard.Nodes.Remove(this); 
    }

    // --- Metodi Pubblici ---
    public void MoveNode(Vector screenDelta)
    {
        double currentScale = ParentBoard.Scale;
        if (currentScale == 0) return;

        X += screenDelta.X / currentScale;
        Y += screenDelta.Y / currentScale;
    }
}