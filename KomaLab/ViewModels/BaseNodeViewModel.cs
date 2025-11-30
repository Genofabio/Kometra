using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using System;

namespace KomaLab.ViewModels;

/// <summary>
/// Gestisce SOLO la posizione, la selezione e l'identità del nodo.
/// Non sa nulla di immagini FITS.
/// </summary>
public abstract partial class BaseNodeViewModel : ObservableObject
{
    protected readonly BaseNodeModel Model;

    // --- Eventi per comunicare con la Board (Disaccoppiamento) ---
    public event Action<BaseNodeViewModel>? RequestRemove;
    public event Action<BaseNodeViewModel>? RequestBringToFront;

    // --- Proprietà Posizionali ---
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

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _zIndex;

    // --- Costruttore ---
    protected BaseNodeViewModel(BaseNodeModel model)
    {
        Model = model;
        Title = model.Title;
    }

    // --- Comandi ---
    [RelayCommand]
    private void RemoveSelf()
    {
        // Invece di chiamare ParentBoard.RemoveNode(this), alziamo la mano.
        RequestRemove?.Invoke(this);
    }

    // --- Metodi di gestione UI/Layout ---
    public void MoveNode(Vector screenDelta, double currentScale)
    {
        // Passiamo la scala come parametro, così il nodo non deve conoscere la Board.
        if (currentScale == 0) return;
        X += screenDelta.X / currentScale;
        Y += screenDelta.Y / currentScale;
    }

    public void BringToFront()
    {
        RequestBringToFront?.Invoke(this);
    }
}