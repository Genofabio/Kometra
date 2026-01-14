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
using KomaLab.Models.Fits; // <--- NUOVO HEADER
using KomaLab.Services.Fits;
using KomaLab.ViewModels.Items;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.ViewModels.Tools;

// ---------------------------------------------------------------------------
// FILE: HeaderEditorToolViewModel.cs
// RUOLO: ViewModel Editor Metadati
// DESCRIZIONE:
// Gestisce l'interfaccia per la visualizzazione e modifica dell'header FITS.
// Aggiornato per usare FitsHeader interno (No nom.tam.fits).
// ---------------------------------------------------------------------------

public partial class HeaderEditorToolViewModel : ObservableObject, IDisposable
{
    private readonly ImageNodeViewModel _sourceNode;
    private readonly IFitsMetadataService _metadataService; 
    private readonly List<FitsHeaderEditorRow> _allItems = new();
    private CancellationTokenSource? _healthCheckCts;
    private bool _isDisposed;

    public event Action? RequestScrollToSelection;

    // --- COLORI STATUS (UI Logic) ---
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

    // --- HEALTH CHECK STATUS ---
    [ObservableProperty] private IBrush _dateStatusColor = PendingBrush;
    [ObservableProperty] private string _dateStatusText = "In attesa...";
    [ObservableProperty] private IBrush _locationStatusColor = PendingBrush;
    [ObservableProperty] private string _locationStatusText = "In attesa...";
    [ObservableProperty] private IBrush _wcsStatusColor = PendingBrush;
    [ObservableProperty] private string _wcsStatusText = "In attesa...";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    // --- COSTRUTTORE ---
    public HeaderEditorToolViewModel(ImageNodeViewModel sourceNode, IFitsMetadataService metadataService)
    {
        _sourceNode = sourceNode ?? throw new ArgumentNullException(nameof(sourceNode));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));

        IsMultipleImages = _sourceNode is MultipleImagesNodeViewModel;
        
        _sourceNode.PropertyChanged += OnSourceNodePropertyChanged;
        
        _ = LoadCurrentHeaderAsync();
    }

    private async void OnSourceNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageNodeViewModel.ActiveRenderer) || 
            e.PropertyName == "CurrentIndex" || 
            e.PropertyName == "CurrentImageText")
        {
            await LoadCurrentHeaderAsync();
        }
    }

    /// <summary>
    /// Carica l'header corrente dal renderer attivo del nodo sorgente.
    /// </summary>
    private async Task LoadCurrentHeaderAsync()
    {
        IsChecking = true;
        ResetStatusToWaiting(); 

        var header = _sourceNode.ActiveRenderer?.Data?.FitsHeader;

        if (_sourceNode is SingleImageNodeViewModel single) 
        { 
            CurrentFileName = single.Title ?? "N/A"; 
            ImageCounterText = "1 / 1"; 
        }
        else if (_sourceNode is MultipleImagesNodeViewModel multi) 
        { 
            int idx = multi.CurrentIndex;
            if (idx >= 0 && idx < multi.ImagePaths.Count) 
                CurrentFileName = System.IO.Path.GetFileName(multi.ImagePaths[idx]);
            ImageCounterText = multi.CurrentImageText; 
        }

        if (header == null)
        {
            _allItems.Clear();
            ApplyFilter();
            IsChecking = false;
            return; 
        }

        // DELEGA: Il servizio ora ritorna List<FitsHeaderEditorRow> usando il tuo modello
        var parsedItems = await Task.Run(() => _metadataService.ParseForEditor(header));

        _allItems.Clear();
        _allItems.AddRange(parsedItems);
        
        ApplyFilter();
        await RefreshHealthCheckAsync();
    }

    public async Task RefreshHealthCheckAsync()
    {
        _healthCheckCts?.Cancel();
        _healthCheckCts = new CancellationTokenSource();
        var token = _healthCheckCts.Token;
        IsChecking = true;

        try
        {
            await Task.Delay(150, token); 
            
            var currentHeader = _sourceNode.ActiveRenderer?.Data?.FitsHeader;
            if (currentHeader == null || _allItems.Count == 0) 
            { 
                IsChecking = false; 
                ResetStatusToWaiting(); 
                return; 
            }

            var result = await Task.Run(() => 
            {
                token.ThrowIfCancellationRequested();
                var status = new HealthCheckResult();
                
                // 1. DATA
                var dt = _metadataService.GetObservationDate(currentHeader);
                status.DateMsg = dt.HasValue ? $"Acquisizione: {dt.Value:yyyy-MM-dd HH:mm:ss}" : null;
                status.DateError = dt.HasValue ? null : "Timestamp mancante o non standard.";
                
                token.ThrowIfCancellationRequested();

                // 2. LOCATION
                var loc = _metadataService.GetObservatoryLocation(currentHeader);
                status.LocMsg = loc != null ? $"Osservatorio: {loc.Latitude:F3}, {loc.Longitude:F3}" : null;
                status.LocError = loc != null ? null : "Coordinate geografiche assenti.";

                token.ThrowIfCancellationRequested();

                // 3. WCS (Astrometria)
                var wcs = _metadataService.ExtractWcs(currentHeader);
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

    /// <summary>
    /// Restituisce un nuovo Header FITS (Tuo Modello) basato sulle modifiche.
    /// </summary>
    // MODIFICATO: Return type aggiornato a FitsHeader
    public FitsHeader GetUpdatedHeader()
    {
        var activeHeader = _sourceNode.ActiveRenderer?.Data?.FitsHeader;
        if (activeHeader == null) return new FitsHeader(); // Usiamo costruttore interno

        return _metadataService.ReconstructHeader(activeHeader, _allItems);
    }

    [RelayCommand]
    private void AddNewKey()
    {
        string newKeyName = !string.IsNullOrWhiteSpace(SearchText) ? SearchText.Trim().ToUpper() : "NEW_KEY";
        
        // MODIFICATO: Uso costruttore specifico di FitsHeaderEditorRow
        // che abbiamo creato nello Step 1 (imposta _isModified = true internamente se modifichi le proprietà, 
        // ma qui lo inizializziamo già modificato).
        var newItem = new FitsHeaderEditorRow(newKeyName, "0", "Aggiunta manuale", false);
        newItem.IsModified = true; // Forziamo il flag per abilitare il salvataggio
        
        var endIndex = _allItems.FindIndex(x => x.Key.Trim().ToUpper() == "END");
        if (endIndex >= 0) _allItems.Insert(endIndex, newItem);
        else _allItems.Add(newItem);

        SearchText = ""; 
        ApplyFilter(); 
        SelectedItem = newItem;
        _ = RefreshHealthCheckAsync();
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

    [RelayCommand] 
    private void NextImage() => (_sourceNode as MultipleImagesNodeViewModel)?.NextImageCommand.Execute(null);

    [RelayCommand] 
    private void PreviousImage() => (_sourceNode as MultipleImagesNodeViewModel)?.PreviousImageCommand.Execute(null);

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