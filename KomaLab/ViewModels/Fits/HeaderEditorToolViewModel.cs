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
/// Architettura disaccoppiata: comunica con i dati tramite IReadOnlyList e IImageNavigator.
/// </summary>
public partial class HeaderEditorToolViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<FitsFileReference> _files;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsHeaderHealthEvaluator _healthEvaluator;
    private readonly FitsHeaderUiMapper _mapper;

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

    // --- STATO SALUTE HEADER ---
    [ObservableProperty] private HeaderHealthStatus _dateStatus = HeaderHealthStatus.Pending;
    [ObservableProperty] private string _dateStatusText = "In attesa...";
    [ObservableProperty] private HeaderHealthStatus _locationStatus = HeaderHealthStatus.Pending;
    [ObservableProperty] private string _locationStatusText = "In attesa...";
    [ObservableProperty] private HeaderHealthStatus _wcsStatus = HeaderHealthStatus.Pending;
    [ObservableProperty] private string _wcsStatusText = "In attesa...";

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

        // Sottoscrizione all'evento del navigatore (se disponibile)
        if (Navigator is SequenceNavigator sn)
        {
            sn.IndexChanged += OnNavigatorIndexChanged;
        }

        _ = LoadCurrentHeaderAsync();
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        await LoadCurrentHeaderAsync();
    }

    /// <summary>
    /// Carica l'header basandosi sulla posizione corrente del navigatore.
    /// </summary>
    private async Task LoadCurrentHeaderAsync()
    {
        IsChecking = true;
        ResetStatusToWaiting(); 

        // Notifica alla UI che i dati del file attivo sono cambiati
        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(ImageCounterText));
        OnPropertyChanged(nameof(IsMultipleImages));

        // Aggiorna lo stato dei comandi proxy
        NextImageCommand.NotifyCanExecuteChanged();
        PreviousImageCommand.NotifyCanExecuteChanged();

        if (ActiveFile == null)
        {
            ClearEditor();
            return; 
        }

        try
        {
            // Caricamento Header (Priorità alla RAM se modificato, altrimenti disco)
            FitsHeader? headerToMap = ActiveFile.ModifiedHeader ?? 
                                     await _dataManager.GetHeaderOnlyAsync(ActiveFile.FilePath);

            if (headerToMap == null)
            {
                ClearEditor();
                return;
            }

            // Mapping verso le righe dell'editor
            var rows = await Task.Run(() => _mapper.MapToRows(headerToMap));
            _allItems.Clear();
            _allItems.AddRange(rows);
            
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

    // --- LOGICA DI NAVIGAZIONE PROXY ---

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextImage() => await Navigator.MoveNextAsync();

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task PreviousImage() => await Navigator.MovePreviousAsync();

    private bool CanGoNext => Navigator.CanMoveNext;
    private bool CanGoBack => Navigator.CanMovePrevious;

    // --- APPLICAZIONE MODIFICHE ---

    [RelayCommand]
    public void ApplyChanges()
    {
        var newHeader = _mapper.ReconstructHeader(_allItems);
        if (ActiveFile != null && newHeader != null)
        {
            // Salvataggio in RAM: il nodo rifletterà i cambiamenti al prossimo ricaricamento
            ActiveFile.ModifiedHeader = newHeader;
            _ = RefreshHealthCheckAsync();
        }
    }

    // --- ANALISI SALUTE ---

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

            var liveHeader = await Task.Run(() => _mapper.ReconstructHeader(_allItems), token);
            var report = await Task.Run(() => _healthEvaluator.Evaluate(liveHeader), token);

            if (token.IsCancellationRequested) return;

            DateStatus = report.Date.Status;
            DateStatusText = report.Date.Message;
            LocationStatus = report.Location.Status;
            LocationStatusText = report.Location.Message;
            WcsStatus = report.Wcs.Status;
            WcsStatusText = report.Wcs.Message;
        }
        catch (OperationCanceledException) { }
        finally { IsChecking = false; }
    }

    private void ResetStatusToWaiting()
    {
        DateStatus = HeaderHealthStatus.Pending; DateStatusText = "Analisi...";
        LocationStatus = HeaderHealthStatus.Pending; LocationStatusText = "Analisi...";
        WcsStatus = HeaderHealthStatus.Pending; WcsStatusText = "Analisi...";
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
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        if (Navigator is SequenceNavigator sn) sn.IndexChanged -= OnNavigatorIndexChanged;
        _healthCheckCts?.Cancel();
        _healthCheckCts?.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}