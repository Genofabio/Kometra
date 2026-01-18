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
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Metadata;
using KomaLab.ViewModels.Nodes;
using SequenceNavigator = KomaLab.ViewModels.Shared.SequenceNavigator;

namespace KomaLab.ViewModels.Fits;

/// <summary>
/// Gestisce la visualizzazione e la modifica dell'header FITS.
/// Implementa un Sandbox a livello di ViewModel e un'analisi dinamica della salute dei metadati.
/// </summary>
public partial class HeaderEditorToolViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<FitsFileReference> _files;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsHeaderHealthEvaluator _healthEvaluator;
    private readonly FitsHeaderUiMapper _mapper;

    // --- SANDBOX UI STATE ---
    // Conserviamo i ViewModel delle righe per mantenere lo stato "IsModified" durante la navigazione.
    private readonly Dictionary<FitsFileReference, List<FitsHeaderEditorRow>> _sessionSandbox = new();
    
    private FitsFileReference? _currentEditingFile;
    private readonly List<FitsHeaderEditorRow> _allItems = new();
    private CancellationTokenSource? _healthCheckCts;
    private bool _isDisposed;

    public event Action? RequestScrollToSelection;

    // --- COMPONENTI ---
    public IImageNavigator Navigator { get; }

    // --- PROPRIETÀ OBSERVABLE ---
    [ObservableProperty] private ObservableCollection<FitsHeaderEditorRow> _filteredItems = new();
    [ObservableProperty] private FitsHeaderEditorRow? _selectedItem;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isChecking = false;

    // --- NUOVA COLLEZIONE SALUTE (Architettura Dinamica) ---
    [ObservableProperty] 
    private ObservableCollection<HealthStatusPresenter> _healthChecks = new();

    // Helpers per la View (Binding)
    public string CurrentFileName => ActiveFile?.FileName ?? "N/A";
    public string ImageCounterText => $"{Navigator.CurrentIndex + 1} / {Navigator.TotalCount}";
    public bool IsMultipleImages => Navigator.CanMove;
    
    private FitsFileReference? ActiveFile => (_files.Count > Navigator.CurrentIndex) ? _files[Navigator.CurrentIndex] : null;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public HeaderEditorToolViewModel(
        IReadOnlyList<FitsFileReference> files,
        IImageNavigator navigator,
        IFitsDataManager dataManager,
        IFitsHeaderHealthEvaluator healthEvaluator,
        FitsHeaderUiMapper mapper)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        Navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _healthEvaluator = healthEvaluator ?? throw new ArgumentNullException(nameof(healthEvaluator));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));

        if (Navigator is SequenceNavigator sn)
        {
            sn.IndexChanged += OnNavigatorIndexChanged;
        }

        _currentEditingFile = ActiveFile;
        _ = LoadCurrentHeaderAsync();
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        SaveCurrentStateToSandbox();
        _currentEditingFile = ActiveFile;
        await LoadCurrentHeaderAsync();
    }

    private void SaveCurrentStateToSandbox()
    {
        if (_currentEditingFile == null) return;
        _sessionSandbox[_currentEditingFile] = new List<FitsHeaderEditorRow>(_allItems);
    }

    private async Task LoadCurrentHeaderAsync()
    {
        IsChecking = true;
        ResetStatusToWaiting(); 

        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(ImageCounterText));
        OnPropertyChanged(nameof(IsMultipleImages));

        NextImageCommand.NotifyCanExecuteChanged();
        PreviousImageCommand.NotifyCanExecuteChanged();

        if (ActiveFile == null)
        {
            ClearEditor();
            return; 
        }

        try
        {
            _allItems.Clear();

            if (_sessionSandbox.TryGetValue(ActiveFile, out var cachedRows))
            {
                _allItems.AddRange(cachedRows);
            }
            else
            {
                FitsHeader? headerToMap = ActiveFile.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(ActiveFile.FilePath);
                if (headerToMap != null)
                {
                    var rows = await Task.Run(() => _mapper.MapToRows(headerToMap));
                    _allItems.AddRange(rows);
                }
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

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextImage() => await Navigator.MoveNextAsync();

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task PreviousImage() => await Navigator.MovePreviousAsync();

    private bool CanGoNext => Navigator.CanMoveNext;
    private bool CanGoBack => Navigator.CanMovePrevious;

    [RelayCommand]
    public void ApplyChanges()
    {
        SaveCurrentStateToSandbox();
        foreach (var kvp in _sessionSandbox)
        {
            var fileRef = kvp.Key;
            var rowsVM = kvp.Value;
            var newHeader = _mapper.ReconstructHeader(rowsVM);

            if (fileRef != null && newHeader != null)
            {
                fileRef.ModifiedHeader = newHeader;
            }
        }
    }

    // --- NUOVA LOGICA DI ANALISI E ORDINAMENTO ---

    public async Task RefreshHealthCheckAsync()
    {
        _healthCheckCts?.Cancel();
        _healthCheckCts = new CancellationTokenSource();
        var token = _healthCheckCts.Token;
        IsChecking = true;

        try
        {
            await Task.Delay(250, token); 
            if (_allItems.Count == 0) { ResetStatusToWaiting(); return; }

            // 1. Genera Header dal ViewModel (dati attuali nell'interfaccia)
            var liveHeader = await Task.Run(() => _mapper.ReconstructHeader(_allItems), token);
            
            // 2. Valuta tramite il servizio Model-Puro
            var report = await Task.Run(() => _healthEvaluator.Evaluate(liveHeader), token);

            if (token.IsCancellationRequested) return;

            // 3. Trasforma in Presenter e ORDINA (Invalid > Warning > Valid > Pending)
            var sortedPresenters = report.Checks
                .Select(item => new HealthStatusPresenter(item))
                .OrderByDescending(p => p.SortPriority)
                .ToList();

            // 4. Aggiorna la collezione UI
            HealthChecks = new ObservableCollection<HealthStatusPresenter>(sortedPresenters);
        }
        catch (OperationCanceledException) { }
        finally { IsChecking = false; }
    }

    private void ResetStatusToWaiting()
    {
        // Genera una lista di stati "Pending" per tutti i tipi conosciuti
        var pendingChecks = Enum.GetValues<HealthCheckType>()
            .Select(type => new HealthStatusPresenter(new HealthStatusItem(type, HeaderHealthStatus.Pending, "Analisi in corso...")))
            .ToList();

        HealthChecks = new ObservableCollection<HealthStatusPresenter>(pendingChecks);
    }

    // --- GESTIONE RIGHE ED EDITOR ---

    private void ApplyFilter()
    {
        var query = SearchText?.Trim();
        var results = string.IsNullOrWhiteSpace(query) 
            ? _allItems 
            : _allItems.Where(item => 
                (item.Key?.Contains((string)query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Value?.Contains((string)query, StringComparison.OrdinalIgnoreCase) ?? false));

        FilteredItems = new ObservableCollection<FitsHeaderEditorRow>(results);
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
        IsChecking = false;
        HealthChecks.Clear();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        if (Navigator is SequenceNavigator sn) sn.IndexChanged -= OnNavigatorIndexChanged;
        
        _healthCheckCts?.Cancel();
        _healthCheckCts?.Dispose();
        _sessionSandbox.Clear();
        
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}