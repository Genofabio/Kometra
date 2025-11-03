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
    
    // Il Model per la posizione e il titolo (condiviso da tutti i nodi)
    protected readonly NodeModel Model; 
    
    // Riferimento al "mondo" genitore
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

    // --- Costruttore ---
    
    /// <summary>
    /// Costruttore protetto per la classe base.
    /// </summary>
    protected BaseNodeViewModel(BoardViewModel parentBoard, NodeModel model)
    {
        ParentBoard = parentBoard;
        Model = model;
        Title = model.Title;
    }

    // --- Comandi ---
    
    [RelayCommand]
    private void RemoveSelf()
    {
        // Questo funzionerà quando aggiorneremo la BoardView (Passo 4)
        ParentBoard.Nodes.Remove(this); 
    }

    // --- Metodi Pubblici ---
    
    /// <summary>
    /// Logica di trascinamento, comune a tutti i nodi.
    /// </summary>
    public void MoveNode(Vector screenDelta)
    {
        double currentScale = ParentBoard.Scale;
        if (currentScale == 0) return;

        X += screenDelta.X / currentScale;
        Y += screenDelta.Y / currentScale;
    }
}