using System;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Nodes;

namespace KomaLab.ViewModels.Nodes;

public abstract partial class BaseNodeViewModel : ObservableObject, IDisposable
{
    private bool _isDisposed;
    protected readonly BaseNodeModel Model;

    public Guid Id => Model.Id;

    // --- PROPRIETÀ REALI (Model) ---
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

    // --- PROPRIETÀ VISUALI (UI State) ---
    // Queste servono per il feedback durante il drag prima del drop finale
    [ObservableProperty] private double _visualOffsetX;
    [ObservableProperty] private double _visualOffsetY;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _zIndex;

    // Eventi per la Board
    public event Action<BaseNodeViewModel>? RequestRemove;
    public event Action<BaseNodeViewModel>? RequestBringToFront;

    public virtual Size EstimatedTotalSize => new(200, 150);

    protected BaseNodeViewModel(BaseNodeModel model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Title = model.Title;
    }

    [RelayCommand]
    private void RemoveSelf() => RequestRemove?.Invoke(this);

    public void BringToFront() => RequestBringToFront?.Invoke(this);

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
                RequestRemove = null;
                RequestBringToFront = null;
            }
            _isDisposed = true;
        }
    }
}