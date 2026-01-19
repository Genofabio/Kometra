using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    [NotifyPropertyChangedFor(nameof(CanMoveNext))] 
    [NotifyPropertyChangedFor(nameof(CanMovePrevious))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand))]
    private int _currentIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMoveNext))]
    [NotifyPropertyChangedFor(nameof(CanMovePrevious))]
    [NotifyPropertyChangedFor(nameof(CanMove))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand))]
    private int _totalCount;

    [ObservableProperty] private bool _isLooping;
    [ObservableProperty] private int _intervalMs = 250;

    // Filtro per indici accessibili (es. solo 0 e l'ultimo in modalità guidata)
    private Predicate<int>? _indexFilter;
    public Predicate<int>? IndexFilter 
    { 
        get => _indexFilter;
        set 
        {
            if (SetProperty(ref _indexFilter, value))
            {
                RefreshState();
            }
        }
    }

    public int DisplayIndex => CurrentIndex + 1;
    public bool CanMove => TotalCount > 1;

    // Determina se esiste un indice valido "nel futuro" o "nel passato" rispetto a quello attuale
    public bool CanMoveNext => GetNextValidIndex(CurrentIndex, false).HasValue;
    public bool CanMovePrevious => GetPreviousValidIndex(CurrentIndex).HasValue;

    public event EventHandler<int>? IndexChanged;

    public SequenceNavigator()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (s, e) => MoveNextInternal(loop: true);
    }

    public void UpdateStatus(int index, int total)
    {
        _totalCount = total;
        _currentIndex = index;
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CurrentIndex));
        OnPropertyChanged(nameof(DisplayIndex));
        RefreshState();
    }

    /// <summary>
    /// Forza il ricalcolo della disponibilità dei pulsanti Next/Previous.
    /// Da chiamare quando cambiano le regole di business (es. cambio modalità allineamento).
    /// </summary>
    public void RefreshState()
    {
        OnPropertyChanged(nameof(CanMoveNext));
        OnPropertyChanged(nameof(CanMovePrevious));
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
    }

    public void MoveTo(int index)
    {
        if (index >= 0 && index < TotalCount)
        {
            CurrentIndex = index;
            IndexChanged?.Invoke(this, CurrentIndex);
        }
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
    public void Next() => MoveNextInternal(loop: false);

    [RelayCommand(CanExecute = nameof(CanMovePrevious))]
    public void Previous() => MovePreviousInternal();

    public async Task MoveNextAsync() { Next(); await Task.CompletedTask; }
    public async Task MovePreviousAsync() { Previous(); await Task.CompletedTask; }

    private void MoveNextInternal(bool loop)
    {
        if (TotalCount == 0) return;

        var next = GetNextValidIndex(CurrentIndex, loop);
        if (next.HasValue)
        {
            // Salto immediato all'indice valido (es. da 0 a 10)
            CurrentIndex = next.Value;
            IndexChanged?.Invoke(this, CurrentIndex);
        }
        else if (loop) 
        {
            Stop();
        }
    }

    private void MovePreviousInternal()
    {
        if (TotalCount == 0) return;

        var prev = GetPreviousValidIndex(CurrentIndex);
        if (prev.HasValue)
        {
            CurrentIndex = prev.Value;
            IndexChanged?.Invoke(this, CurrentIndex);
        }
    }

    // --- LOGICA DI RICERCA SALTO ---

    private int? GetNextValidIndex(int startFrom, bool loop)
    {
        if (TotalCount <= 1) return null;

        // Cerca il primo indice che soddisfa il filtro DOPO quello attuale
        for (int i = startFrom + 1; i < TotalCount; i++)
        {
            if (IndexFilter == null || IndexFilter(i)) return i;
        }

        // Se non trovato e siamo in loop, ricomincia da capo
        if (loop)
        {
            for (int i = 0; i <= startFrom; i++)
            {
                if (IndexFilter == null || IndexFilter(i)) return i;
            }
        }

        return null;
    }

    private int? GetPreviousValidIndex(int startFrom)
    {
        if (TotalCount <= 1) return null;

        for (int i = startFrom - 1; i >= 0; i--)
        {
            if (IndexFilter == null || IndexFilter(i)) return i;
        }
        return null;
    }
    
    public void Stop() { IsLooping = false; _timer.Stop(); }
}