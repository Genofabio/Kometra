using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using System;

namespace KomaLab.ViewModels;

// 1. AGGIUNGI L'INTERFACCIA IDisposable QUI
public abstract partial class BaseNodeViewModel : ObservableObject, IDisposable
{
    private bool _disposedValue;
    
    protected readonly BaseNodeModel Model;

    public event Action<BaseNodeViewModel>? RequestRemove;
    public event Action<BaseNodeViewModel>? RequestBringToFront;

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
    
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _zIndex;

    protected BaseNodeViewModel(BaseNodeModel model)
    {
        Model = model;
        Title = model.Title;
    }
    
    ~BaseNodeViewModel()
    {
        Dispose(false);
    }

    [RelayCommand]
    private void RemoveSelf()
    {
        RequestRemove?.Invoke(this);
    }

    public void MoveNode(Vector screenDelta, double currentScale)
    {
        if (currentScale == 0) return;
        X += screenDelta.X / currentScale;
        Y += screenDelta.Y / currentScale;
    }

    public void BringToFront()
    {
        RequestBringToFront?.Invoke(this);
    }

    // --- IMPLEMENTAZIONE IDISPOSABLE (Pattern Standard) ---
    
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this); 
    }
    
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                RequestRemove = null;
                RequestBringToFront = null;
            }
            
            _disposedValue = true;
        }
    }
}