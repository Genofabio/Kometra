using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Services;
using Kometra.ViewModels.Nodes;

namespace Kometra.ViewModels.Shared;

public partial class SequenceNavigator : ObservableObject, IImageNavigator
{
    private readonly DispatcherTimer _timer;
    private readonly IConfigurationService? _configService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayIndex))]
    [NotifyPropertyChangedFor(nameof(CanMoveNext))] 
    [NotifyPropertyChangedFor(nameof(CanMovePrevious))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToFirstCommand), nameof(MoveToLastCommand))] 
    private int _currentIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMoveNext))]
    [NotifyPropertyChangedFor(nameof(CanMovePrevious))]
    [NotifyPropertyChangedFor(nameof(CanMove))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToFirstCommand), nameof(MoveToLastCommand))]
    private int _totalCount;

    [ObservableProperty] private bool _isLooping;
    
    // Il default diventa 100ms (10 FPS) se il servizio di configurazione non è disponibile
    [ObservableProperty] private int _intervalMs = 100; 

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

    // Determina se esiste un indice valido "nel futuro" o "nel passato"
    public bool CanMoveNext => GetNextValidIndex(CurrentIndex, false).HasValue;
    public bool CanMovePrevious => GetPreviousValidIndex(CurrentIndex).HasValue;

    public event EventHandler<int>? IndexChanged;

    // Costruttore con iniezione opzionale del servizio di configurazione
    public SequenceNavigator(IConfigurationService? configService = null)
    {
        _configService = configService;

        // Se il servizio di configurazione è disponibile, ascoltiamo i cambiamenti in tempo reale.
        // Così, se modifichi gli FPS nelle impostazioni, la velocità si aggiorna all'istante
        // anche se l'animazione è già in corso!
        if (_configService is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(IConfigurationService.Current) && IsLooping)
                {
                    UpdateTimerInterval();
                }
            };
        }

        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (s, e) => MoveNextInternal(loop: true);
    }

    /// <summary>
    /// Legge gli FPS dalle impostazioni e aggiorna l'intervallo in millisecondi del timer.
    /// </summary>
    private void UpdateTimerInterval()
    {
        // Recuperiamo gli FPS (fallback a 10 se il servizio è assente o qualcosa va storto)
        int fps = _configService?.Current?.AnimationFps ?? 10;
        
        // Protezione contro valori impossibili (zero o negativi)
        if (fps <= 0) fps = 10; 
        
        // Conversione matematica: millisecondi = 1000 / frame al secondo
        IntervalMs = 1000 / fps;
        
        _timer.Interval = TimeSpan.FromMilliseconds(IntervalMs);
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
    /// Forza il ricalcolo della disponibilità dei pulsanti.
    /// Da chiamare quando cambiano le regole di business (es. cambio modalità allineamento).
    /// </summary>
    public void RefreshState()
    {
        OnPropertyChanged(nameof(CanMoveNext));
        OnPropertyChanged(nameof(CanMovePrevious));
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        MoveToFirstCommand.NotifyCanExecuteChanged();
        MoveToLastCommand.NotifyCanExecuteChanged();
    }

    public void MoveTo(int index)
    {
        if (index >= 0 && index < TotalCount)
        {
            // Controllo di sicurezza: se l'indice target è filtrato, non ci andiamo
            if (IndexFilter != null && !IndexFilter(index)) return;

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
            // Aggiorniamo la velocità di riproduzione leggendo l'impostazione dell'utente
            UpdateTimerInterval();
            _timer.Start();
        }
        else 
        {
            _timer.Stop();
        }
    }

    // --- NAVIGAZIONE STANDARD ---

    [RelayCommand(CanExecute = nameof(CanMoveNext))]
    public void Next() => MoveNextInternal(loop: false);

    [RelayCommand(CanExecute = nameof(CanMovePrevious))]
    public void Previous() => MovePreviousInternal();

    // --- NAVIGAZIONE ESTREMI (Nuovi Metodi) ---

    [RelayCommand(CanExecute = nameof(CanMovePrevious))] // Se puoi andare indietro, puoi andare al primo
    public void MoveToFirst()
    {
        // Cerca il primo indice valido partendo da prima dell'inizio (-1)
        var firstValid = GetNextValidIndex(-1, false);
        if (firstValid.HasValue)
        {
            MoveTo(firstValid.Value);
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveNext))] // Se puoi andare avanti, puoi andare all'ultimo
    public void MoveToLast()
    {
        // Cerca l'ultimo indice valido partendo da dopo la fine (TotalCount)
        var lastValid = GetPreviousValidIndex(TotalCount);
        if (lastValid.HasValue)
        {
            MoveTo(lastValid.Value);
        }
    }

    // --- UTILITIES ASYNC ---

    public async Task MoveNextAsync() { Next(); await Task.CompletedTask; }
    public async Task MovePreviousAsync() { Previous(); await Task.CompletedTask; }

    // --- LOGICA INTERNA ---

    private void MoveNextInternal(bool loop)
    {
        if (TotalCount == 0) return;

        var next = GetNextValidIndex(CurrentIndex, loop);
        if (next.HasValue)
        {
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

    // --- LOGICA DI RICERCA SALTO (Smart Filter) ---

    private int? GetNextValidIndex(int startFrom, bool loop)
    {
        if (TotalCount <= 0) return null;

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
        if (TotalCount <= 0) return null;

        // Cerca all'indietro partendo da startFrom - 1
        for (int i = startFrom - 1; i >= 0; i--)
        {
            if (IndexFilter == null || IndexFilter(i)) return i;
        }
        return null;
    }
    
    public void Stop() { IsLooping = false; _timer.Stop(); }
}