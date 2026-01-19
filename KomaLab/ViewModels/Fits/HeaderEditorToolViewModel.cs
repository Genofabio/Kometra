using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Health;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.Metadata;
using KomaLab.ViewModels.Nodes;
using KomaLab.ViewModels.Shared;

namespace KomaLab.ViewModels.Fits;

public partial class HeaderEditorToolViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<FitsFileReference> _files;
    private readonly IHeaderEditorCoordinator _coordinator;
    private readonly IFitsHeaderHealthEvaluator _healthEvaluator;
    private readonly FitsHeaderUiMapper _mapper;

    private readonly List<FitsHeaderEditorRow> _allItems = new();
    private CancellationTokenSource? _healthCheckCts;
    private bool _isDisposed;

    // Fondamentale: teniamo traccia di QUALE file è attualmente caricato nelle "_allItems"
    private FitsFileReference? _currentFile;

    public event Action? RequestClose;
    public event Action? RequestScrollToSelection;
    public IImageNavigator Navigator { get; }

    [ObservableProperty] private ObservableCollection<FitsHeaderEditorRow> _filteredItems = new();
    [ObservableProperty] private FitsHeaderEditorRow? _selectedItem;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isChecking = false;
    [ObservableProperty] private ObservableCollection<HealthStatusPresenter> _healthChecks = new();

    public string CurrentFileName => ActiveFile?.FileName ?? "N/A";
    public string ImageCounterText => $"{Navigator.CurrentIndex + 1} / {Navigator.TotalCount}";
    public bool IsMultipleImages => Navigator.CanMove;
    
    private FitsFileReference? ActiveFile => (_files.Count > Navigator.CurrentIndex) ? _files[Navigator.CurrentIndex] : null;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public HeaderEditorToolViewModel(
        IReadOnlyList<FitsFileReference> files,
        IImageNavigator navigator,
        IHeaderEditorCoordinator coordinator,
        IFitsHeaderHealthEvaluator healthEvaluator,
        FitsHeaderUiMapper mapper)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        Navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _healthEvaluator = healthEvaluator ?? throw new ArgumentNullException(nameof(healthEvaluator));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));

        if (Navigator is SequenceNavigator sn)
        {
            sn.IndexChanged += OnNavigatorIndexChanged;
        }

        // Inizializzazione: il primo file diventa quello corrente
        _currentFile = ActiveFile;
        _ = LoadCurrentHeaderAsync();
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        // 1. Salviamo le modifiche fatte finora nel buffer del file che STAVAMO editando
        SaveToBuffer(_currentFile);

        // 2. Aggiorniamo il riferimento al nuovo file attivo
        _currentFile = ActiveFile;

        // 3. Carichiamo i dati del nuovo file
        await LoadCurrentHeaderAsync();
    }

    /// <summary>
    /// Salva esplicitamente lo stato di un file nel coordinatore.
    /// </summary>
    private void SaveToBuffer(FitsFileReference? file)
    {
        if (file != null && _allItems.Count > 0)
        {
            // Ricostruiamo l'header dalle righe attuali della UI
            var headerToBuffer = _mapper.ReconstructHeader(_allItems);
            if (headerToBuffer != null)
            {
                _coordinator.SaveToBuffer(file, headerToBuffer);
            }
        }
    }

    private async Task LoadCurrentHeaderAsync()
    {
        IsChecking = true;
        ResetHealthToWaiting(); 

        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(ImageCounterText));
        OnPropertyChanged(nameof(IsMultipleImages));
        NextImageCommand.NotifyCanExecuteChanged();
        PreviousImageCommand.NotifyCanExecuteChanged();

        if (_currentFile == null)
        {
            ClearEditor();
            return; 
        }

        try
        {
            _allItems.Clear();
            
            // Chiediamo l'header al coordinatore (che lo prenderà dal sandbox se esiste)
            FitsHeader? header = await _coordinator.GetHeaderAsync(_currentFile);

            if (header != null)
            {
                var rows = _mapper.MapToRows(header);
                _allItems.AddRange(rows);
            }

            ApplyFilter();
            await RefreshHealthCheckAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HeaderEditor] Error: {ex.Message}");
            ClearEditor();
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    public void ApplyChanges()
    {
        // Sincronizziamo l'ultimo file visualizzato
        SaveToBuffer(_currentFile);
        _coordinator.CommitAll();
        RequestClose?.Invoke();
    }

    // ... Resto dei comandi (NextImage, PreviousImage, ApplyFilter, AddNewKey, DeleteRow) rimangono uguali ...

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextImage() => await Navigator.MoveNextAsync();

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task PreviousImage() => await Navigator.MovePreviousAsync();

    private bool CanGoNext => Navigator.CanMoveNext;
    private bool CanGoBack => Navigator.CanMovePrevious;

    public async Task RefreshHealthCheckAsync()
    {
        _healthCheckCts?.Cancel();
        _healthCheckCts = new CancellationTokenSource();
        var token = _healthCheckCts.Token;
        IsChecking = true;

        try
        {
            await Task.Delay(250, token); 
            if (_allItems.Count == 0) return;

            var liveHeader = await Task.Run(() => _mapper.ReconstructHeader(_allItems), token);
            var report = await Task.Run(() => _healthEvaluator.Evaluate(liveHeader), token);

            if (token.IsCancellationRequested) return;

            var presenters = report.Checks
                .Select(item => new HealthStatusPresenter(item))
                .OrderByDescending(p => p.SortPriority)
                .ToList();

            HealthChecks = new ObservableCollection<HealthStatusPresenter>(presenters);
        }
        catch (OperationCanceledException) { }
        finally { IsChecking = false; }
    }

    private void ResetHealthToWaiting()
    {
        var pending = Enum.GetValues<HealthCheckType>()
            .Select(t => new HealthStatusPresenter(new HealthStatusItem(t, HeaderHealthStatus.Pending, "Analisi...")))
            .ToList();
        HealthChecks = new ObservableCollection<HealthStatusPresenter>(pending);
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim();

        // Se la query è vuota, mostriamo tutto nell'ordine originale (Fisico del FITS)
        if (string.IsNullOrWhiteSpace(query))
        {
            FilteredItems = new ObservableCollection<FitsHeaderEditorRow>(_allItems);
            return;
        }

        // Calcoliamo i risultati con un sistema di scoring
        var filteredResults = _allItems
            .Select(item => new 
            { 
                Row = item, 
                Score = CalculateMatchScore(item, query) 
            })
            .Where(x => x.Score > 0) // Escludiamo ciò che non c'entra nulla
            .OrderByDescending(x => x.Score) // Prima i match più importanti
            .ThenBy(x => x.Row.Key) // A parità di score, ordine alfabetico
            .Select(x => x.Row);

        FilteredItems = new ObservableCollection<FitsHeaderEditorRow>(filteredResults);
    }

    /// <summary>
    /// Calcola la rilevanza di una riga rispetto alla query.
    /// Priorità: Chiave (10) > Valore (5) > Commento (1).
    /// </summary>
    private int CalculateMatchScore(FitsHeaderEditorRow item, string query)
    {
        int score = 0;

        // Match nella Chiave: Massima priorità
        if (item.Key?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            score += 10;

        // Match nel Valore: Priorità media
        if (item.Value?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            score += 5;

        // Match nel Commento: Priorità bassa (ma utile per trovare il "brand" o descrizioni)
        if (item.Comment?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            score += 1;

        return score;
    }

    [RelayCommand]
    private void AddNewKey()
    {
        string newKeyName = !string.IsNullOrWhiteSpace(SearchText) ? SearchText.Trim().ToUpper() : "NEW_KEY";
        var newItem = new FitsHeaderEditorRow(newKeyName, "", "", false) { IsModified = true };

        int endIndex = _allItems.FindIndex(x => x.Key.Trim().ToUpper() == "END");
        if (endIndex >= 0) _allItems.Insert(endIndex, newItem);
        else _allItems.Add(newItem);

        SearchText = ""; ApplyFilter(); SelectedItem = newItem; _ = RefreshHealthCheckAsync(); 
        RequestScrollToSelection?.Invoke();
    }
    
    [RelayCommand]
    private void DeleteRow()
    {
        if (SelectedItem == null || SelectedItem.IsReadOnly) return;
        _allItems.Remove(SelectedItem);
        ApplyFilter();
        _ = RefreshHealthCheckAsync();
    }

    private void ClearEditor()
    {
        _allItems.Clear();
        FilteredItems.Clear();
        HealthChecks.Clear();
        IsChecking = false;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        if (Navigator is SequenceNavigator sn) sn.IndexChanged -= OnNavigatorIndexChanged;
        _healthCheckCts?.Cancel();
        _healthCheckCts?.Dispose();
        _coordinator.ClearSession();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}