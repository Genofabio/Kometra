using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;

namespace KomaLab.ViewModels;

/// <summary>
/// Classe base astratta per qualsiasi oggetto che può essere
/// posizionato, selezionato e trascinato sulla BoardView.
/// </summary>
public abstract partial class BaseNodeViewModel : ObservableObject
{
    // --- Campi ---
    
    /// <summary>
    /// Altezza stimata della UI (barra del titolo, ecc.) in pixel.
    /// </summary>
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
    
    // --- 1. CORREZIONE ---
    // La logica di calcolo ora vive qui, nella classe base.
    // Non è più 'abstract'.
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

    // --- 2. CORREZIONE ---
    // Questa è l'UNICA proprietà che i figli
    // devono implementare.
    protected abstract Size NodeContentSize { get; }


    // --- Costruttore ---
    
    protected BaseNodeViewModel(BoardViewModel parentBoard, BaseNodeModel model)
    {
        ParentBoard = parentBoard;
        Model = model; 
        Title = model.Title;
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