using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.ViewModels.Shared;

public partial class SequenceNavigator : ObservableObject, IImageNavigator
{
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayIndex))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand))]
    private int _currentIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand))]
    private int _totalCount;

    [ObservableProperty] private bool _isLooping;
    [ObservableProperty] private int _intervalMs = 250;

    public int DisplayIndex => CurrentIndex + 1;
    public bool CanMove => TotalCount > 1;

    public event EventHandler<int>? IndexChanged;

    public SequenceNavigator()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (s, e) => MoveNext();
    }

    public void UpdateStatus(int index, int total)
    {
        TotalCount = total;
        CurrentIndex = index;
    }

    [RelayCommand]
    public void ToggleLoop()
    {
        IsLooping = !IsLooping;
        if (IsLooping)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(IntervalMs);
            _timer.Start();
        }
        else _timer.Stop();
    }

    [RelayCommand(CanExecute = nameof(CanMoveNext))]
    public void Next() => MoveNext();

    [RelayCommand(CanExecute = nameof(CanMovePrevious))]
    public void Previous() => MovePrevious();

    // Implementazione esplicita IImageNavigator per i Task
    public async System.Threading.Tasks.Task MoveNextAsync() { MoveNext(); await System.Threading.Tasks.Task.CompletedTask; }
    public async System.Threading.Tasks.Task MovePreviousAsync() { MovePrevious(); await System.Threading.Tasks.Task.CompletedTask; }

    public bool CanMoveNext => TotalCount > 0 && CurrentIndex < TotalCount - 1;
    public bool CanMovePrevious => CurrentIndex > 0;

    private void MoveNext()
    {
        if (TotalCount == 0) return;
        CurrentIndex = (CurrentIndex + 1) % TotalCount;
        IndexChanged?.Invoke(this, CurrentIndex);
    }

    private void MovePrevious()
    {
        if (TotalCount == 0) return;
        CurrentIndex = (CurrentIndex - 1 + TotalCount) % TotalCount;
        IndexChanged?.Invoke(this, CurrentIndex);
    }
    
    public void Stop() { IsLooping = false; _timer.Stop(); }
}