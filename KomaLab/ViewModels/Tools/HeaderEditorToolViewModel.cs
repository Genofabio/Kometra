using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits; 
using KomaLab.Services.Fits;
using KomaLab.ViewModels.Items;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.ViewModels.Tools;

public partial class HeaderEditorToolViewModel : ObservableObject, IDisposable
{
    private readonly ImageNodeViewModel _sourceNode;
    private readonly IFitsMetadataService _metadataService; 
    private readonly IFitsIoService _ioService; // NUOVO: Necessario per leggere header

    private readonly List<FitsHeaderEditorRow> _allItems = new();
    private CancellationTokenSource? _healthCheckCts;
    private bool _isDisposed;
    
    // Cache dell'header originale per ricostruire quello modificato
    private FitsHeader? _loadedHeader; 

    public event Action? RequestScrollToSelection;

    // --- COLORI STATUS ---
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#03A077"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E6606A"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#808080"));

    // --- PROPRIETÀ OBSERVABLE ---

    [ObservableProperty] private ObservableCollection<FitsHeaderEditorRow> _filteredItems = new();
    [ObservableProperty] private FitsHeaderEditorRow? _selectedItem;
    [ObservableProperty] private string _currentFileName = "N/A";
    [ObservableProperty] private string _imageCounterText = "";
    [ObservableProperty] private bool _isMultipleImages;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isChecking = false;
    
    // --- Stato pulsanti navigazione ---
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NextImageCommand))] private bool _canGoNext;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(PreviousImageCommand))] private bool _canGoBack;

    // --- HEALTH CHECK STATUS ---
    [ObservableProperty] private IBrush _dateStatusColor = PendingBrush;
    [ObservableProperty] private string _dateStatusText = "In attesa...";
    [ObservableProperty] private IBrush _locationStatusColor = PendingBrush;
    [ObservableProperty] private string _locationStatusText = "In attesa...";
    [ObservableProperty] private IBrush _wcsStatusColor = PendingBrush;
    [ObservableProperty] private string _wcsStatusText = "In attesa...";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public HeaderEditorToolViewModel(
        ImageNodeViewModel sourceNode, 
        IFitsMetadataService metadataService,
        IFitsIoService ioService) // Iniettato
    {
        _sourceNode = sourceNode ?? throw new ArgumentNullException(nameof(sourceNode));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));

        IsMultipleImages = _sourceNode is MultipleImagesNodeViewModel;
        
        _sourceNode.PropertyChanged += OnSourceNodePropertyChanged;
        
        _ = LoadCurrentHeaderAsync();
    }

    private async void OnSourceNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // ADATTAMENTO: Ascoltiamo ActiveFile invece di ActiveRenderer
        if (e.PropertyName == nameof(ImageNodeViewModel.ActiveFile) || 
            e.PropertyName == "CurrentIndex" || 
            e.PropertyName == "CurrentImageText")
        {
            await LoadCurrentHeaderAsync();
        }
    }

    private async Task LoadCurrentHeaderAsync()
    {
        IsChecking = true;
        ResetStatusToWaiting(); 

        // 1. Identificazione File Attivo
        var activeFile = _sourceNode.ActiveFile;

        // Gestione UI Navigazione
        if (_sourceNode is SingleImageNodeViewModel single) 
        { 
            CurrentFileName = activeFile?.FileName ?? "N/A"; 
            ImageCounterText = "1 / 1";
            CanGoNext = false;
            CanGoBack = false;
        }
        else if (_sourceNode is MultipleImagesNodeViewModel multi) 
        { 
            CurrentFileName = activeFile?.FileName ?? "N/A";
            ImageCounterText = multi.CurrentImageText; 
            
            CanGoBack = multi.CanShowPrevious;
            CanGoNext = multi.CanShowNext;
        }

        if (activeFile == null)
        {
            ClearEditor();
            return; 
        }

        try
        {
            // 2. Recupero Header (Memoria o Disco)
            // Se l'utente ha modifiche non salvate in RAM, usiamo quelle.
            if (activeFile.HasUnsavedChanges)
            {
                _loadedHeader = activeFile.UnsavedHeader;
            }
            else
            {
                // Altrimenti leggiamo dal disco
                _loadedHeader = await _ioService.ReadHeaderAsync(activeFile.FilePath);
            }

            if (_loadedHeader == null)
            {
                ClearEditor();
                return;
            }

            // 3. Parsing per Editor
            var parsedItems = await Task.Run(() => _metadataService.ParseForEditor(_loadedHeader));

            _allItems.Clear();
            _allItems.AddRange(parsedItems);
            
            ApplyFilter();
            await RefreshHealthCheckAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading header: {ex.Message}");
            ClearEditor();
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void ClearEditor()
    {
        _loadedHeader = null;
        _allItems.Clear();
        ApplyFilter();
        IsChecking = false;
    }

    // --- HEALTH CHECK (Usa _loadedHeader invece di ActiveRenderer) ---
    public async Task RefreshHealthCheckAsync()
    {
        _healthCheckCts?.Cancel();
        _healthCheckCts = new CancellationTokenSource();
        var token = _healthCheckCts.Token;
        IsChecking = true;

        try
        {
            await Task.Delay(150, token); 
            
            // Usiamo l'header caricato localmente
            if (_loadedHeader == null || _allItems.Count == 0) 
            { 
                IsChecking = false; 
                ResetStatusToWaiting(); 
                return; 
            }

            // Dobbiamo ricostruire l'header temporaneo basato sulle modifiche attuali nell'editor
            // per validare lo stato "live"
            var liveHeader = _metadataService.ReconstructHeader(_loadedHeader, _allItems);

            var result = await Task.Run(() => 
            {
                token.ThrowIfCancellationRequested();
                var status = new HealthCheckResult();
                
                var dt = _metadataService.GetObservationDate(liveHeader);
                status.DateMsg = dt.HasValue ? $"Acquisizione: {dt.Value:yyyy-MM-dd HH:mm:ss}" : null;
                status.DateError = dt.HasValue ? null : "Timestamp mancante o non standard.";
                
                token.ThrowIfCancellationRequested();
                var loc = _metadataService.GetObservatoryLocation(liveHeader);
                status.LocMsg = loc != null ? $"Osservatorio: {loc.Latitude:F3}, {loc.Longitude:F3}" : null;
                status.LocError = loc != null ? null : "Coordinate geografiche assenti.";
                
                token.ThrowIfCancellationRequested();
                var wcs = _metadataService.ExtractWcs(liveHeader);
                status.WcsMsg = (wcs != null && wcs.IsValid) ? $"Risolto ({wcs.PixelScaleArcsec:F2}\"/px)" : null;
                status.WcsError = (wcs != null && wcs.IsValid) ? null : "Soluzione WCS non valida.";
                
                return status;
            }, token);

            if (token.IsCancellationRequested) return;
            UpdateStatusIndicator(result.DateMsg, result.DateError, m => DateStatusText = m, c => DateStatusColor = c);
            UpdateStatusIndicator(result.LocMsg, result.LocError, m => LocationStatusText = m, c => LocationStatusColor = c);
            UpdateStatusIndicator(result.WcsMsg, result.WcsError, m => WcsStatusText = m, c => WcsStatusColor = c);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"[HealthCheck Error] {ex.Message}"); }
        finally { IsChecking = false; }
    }

    private void UpdateStatusIndicator(string? msg, string? error, Action<string> setText, Action<IBrush> setColor)
    {
        if (error == null) { setText(msg ?? ""); setColor(SuccessBrush); }
        else { setText(error); setColor(ErrorBrush); }
    }

    private void ResetStatusToWaiting()
    {
        DateStatusColor = PendingBrush; DateStatusText = "Analisi...";
        LocationStatusColor = PendingBrush; LocationStatusText = "Analisi...";
        WcsStatusColor = PendingBrush; WcsStatusText = "Analisi...";
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            FilteredItems = new ObservableCollection<FitsHeaderEditorRow>(_allItems);
        }
        else
        {
            var results = _allItems
                .Where(item => 
                    (item.Key?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (item.Value?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (item.Comment?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(item => item.Key?.StartsWith(query, StringComparison.OrdinalIgnoreCase) == true ? 0 : 1);
            
            FilteredItems = new ObservableCollection<FitsHeaderEditorRow>(results);
        }
    }

    // --- SALVATAGGIO ---

    /// <summary>
    /// Restituisce l'header ricostruito con le modifiche.
    /// Da chiamare quando l'utente preme "Applica" o "Salva".
    /// </summary>
    public FitsHeader? GetUpdatedHeader()
    {
        if (_loadedHeader == null) return null;
        return _metadataService.ReconstructHeader(_loadedHeader, _allItems);
    }

    /// <summary>
    /// Applica le modifiche all'oggetto FileReference attivo.
    /// Non scrive su disco, ma segna il file come "Modificato in RAM".
    /// </summary>
    [RelayCommand]
    public void ApplyChanges()
    {
        var activeFile = _sourceNode.ActiveFile;
        var newHeader = GetUpdatedHeader();

        if (activeFile != null && newHeader != null)
        {
            activeFile.UnsavedHeader = newHeader;
            
            // Opzionale: Notificare il nodo di ricaricare se necessario, 
            // ma dato che l'immagine (pixel) non cambia, spesso basta aggiornare i metadati.
        }
    }

    [RelayCommand]
    private void AddNewKey()
    {
        string newKeyName = !string.IsNullOrWhiteSpace(SearchText) ? SearchText.Trim().ToUpper() : "NEW_KEY";
        var newItem = new FitsHeaderEditorRow(newKeyName, "0", "Aggiunta manuale", false);
        newItem.IsModified = true;
        var endIndex = _allItems.FindIndex(x => x.Key.Trim().ToUpper() == "END");
        if (endIndex >= 0) _allItems.Insert(endIndex, newItem);
        else _allItems.Add(newItem);
        SearchText = ""; ApplyFilter(); SelectedItem = newItem; _ = RefreshHealthCheckAsync(); RequestScrollToSelection?.Invoke();
    }
    
    [RelayCommand]
    private void DeleteRow()
    {
        if (SelectedItem == null || SelectedItem.IsReadOnly) return;
        _allItems.Remove(SelectedItem);
        ApplyFilter();
        _ = RefreshHealthCheckAsync();
    }

    // --- NAVIGAZIONE ---

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextImage()
    {
        if (_sourceNode is MultipleImagesNodeViewModel multi && CanGoNext)
        {
            multi.NextImageCommand.Execute(null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousImage()
    {
        if (_sourceNode is MultipleImagesNodeViewModel multi && CanGoBack)
        {
            multi.PreviousImageCommand.Execute(null);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        
        _sourceNode.PropertyChanged -= OnSourceNodePropertyChanged;
        _healthCheckCts?.Cancel();
        _healthCheckCts?.Dispose();
        
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private class HealthCheckResult
    {
        public string? DateMsg, DateError, LocMsg, LocError, WcsMsg, WcsError;
    }
}