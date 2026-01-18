using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
// Assicurati che il namespace sia corretto per la tua struttura
using KomaLab.ViewModels.Nodes; 

namespace KomaLab.ViewModels.Shared;

public partial class SequenceNavigator : ObservableObject, IImageNavigator
{
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayIndex))]
    // 1. FIX: Notifichiamo che le proprietà booleane sono cambiate
    [NotifyPropertyChangedFor(nameof(CanMoveNext))] 
    [NotifyPropertyChangedFor(nameof(CanMovePrevious))]
    // 2. Aggiorniamo lo stato dei comandi (abilitato/disabilitato)
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand))]
    private int _currentIndex;

    [ObservableProperty]
    // Anche se TotalCount cambia, potrebbe cambiare la visibilità delle frecce
    [NotifyPropertyChangedFor(nameof(CanMoveNext))]
    [NotifyPropertyChangedFor(nameof(CanMovePrevious))]
    [NotifyPropertyChangedFor(nameof(CanMove))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand))]
    private int _totalCount;

    [ObservableProperty] private bool _isLooping;
    [ObservableProperty] private int _intervalMs = 250;

    public int DisplayIndex => CurrentIndex + 1;
    public bool CanMove => TotalCount > 1;

    // --- Proprietà per la Visibilità delle Frecce ---
    // Queste vengono ricalcolate e la UI avvisata grazie agli attributi sopra
    public bool CanMoveNext => TotalCount > 0 && CurrentIndex < TotalCount - 1;
    public bool CanMovePrevious => CurrentIndex > 0;

    public event EventHandler<int>? IndexChanged;

    public SequenceNavigator()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (s, e) => MoveNextInternal(loop: true);
    }

    public void UpdateStatus(int index, int total)
    {
        // Impostiamo TotalCount prima per evitare calcoli errati nei trigger
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

    // I Comandi usano le proprietà CanMove... per abilitare/disabilitare il click
    [RelayCommand(CanExecute = nameof(CanMoveNext))]
    public void Next() => MoveNextInternal(loop: false);

    [RelayCommand(CanExecute = nameof(CanMovePrevious))]
    public void Previous() => MovePreviousInternal();

    // Implementazione IImageNavigator
    public async Task MoveNextAsync() { Next(); await Task.CompletedTask; }
    public async Task MovePreviousAsync() { Previous(); await Task.CompletedTask; }

    // --- Logica Interna ---

    private void MoveNextInternal(bool loop)
    {
        if (TotalCount == 0) return;

        // Se è l'animazione automatica (loop = true), usiamo il modulo % per ricominciare
        // Se è manuale (loop = false), ci fermiamo alla fine (anche se il CanExecute dovrebbe già prevenirlo)
        if (loop)
        {
            CurrentIndex = (CurrentIndex + 1) % TotalCount;
        }
        else
        {
            if (CurrentIndex < TotalCount - 1) 
                CurrentIndex++;
        }
        
        IndexChanged?.Invoke(this, CurrentIndex);
    }

    private void MovePreviousInternal()
    {
        if (TotalCount == 0) return;
        
        if (CurrentIndex > 0)
            CurrentIndex--;
            
        IndexChanged?.Invoke(this, CurrentIndex);
    }
    
    public void Stop() { IsLooping = false; _timer.Stop(); }
}