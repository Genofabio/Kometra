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
using KomaLab.ViewModels.Nodes;
using nom.tam.fits;

namespace KomaLab.ViewModels.Tools;

// ---------------------------------------------------------------------------
// FILE: HeaderEditorToolViewModel.cs
// RUOLO: ViewModel Editor Metadati
// DESCRIZIONE:
// Gestisce l'interfaccia per la visualizzazione e modifica dell'header FITS.
// 
// MIGLIORAMENTI APPLICATI:
// 1. Memory Safety: Implementa IDisposable per sganciare gli eventi dal SourceNode.
// 2. Dependency Injection: Il servizio viene iniettato tramite interfaccia.
// 3. Robustezza: Gestione dei task asincroni con CancellationToken per evitare race conditions.
// 4. Separation of Concerns: Logica di ricostruzione header delegata interamente al Service.
// ---------------------------------------------------------------------------

public partial class HeaderEditorToolViewModel : ObservableObject, IDisposable
{
    private readonly ImageNodeViewModel _sourceNode;
    private readonly IFitsMetadataService _metadataService; // Iniettato tramite interfaccia
    private readonly List<FitsHeaderItem> _allItems = new();
    private CancellationTokenSource? _healthCheckCts;
    private bool _isDisposed;

    public event Action? RequestScrollToSelection;

    // --- COLORI STATUS (UI Logic) ---
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#03A077"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E6606A"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#808080"));

    // --- PROPRIETÀ OBSERVABLE ---

    [ObservableProperty] private ObservableCollection<FitsHeaderItem> _filteredItems = new();
    [ObservableProperty] private FitsHeaderItem? _selectedItem;
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
        
        // Sottoscrizione eventi (da sganciare nel Dispose)
        _sourceNode.PropertyChanged += OnSourceNodePropertyChanged;
        
        // Inizializzazione asincrona sicura
        _ = LoadCurrentHeaderAsync();
    }

    private async void OnSourceNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Monitoriamo i cambi di indice (per MultipleImages) o di renderer
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

        // Gestione Identificativi UI
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

        // DELEGA: Il parsing dell'header è logica di business del Service
        var parsedItems = await Task.Run(() => _metadataService.ParseForEditor(header));

        _allItems.Clear();
        _allItems.AddRange(parsedItems);
        
        ApplyFilter();
        await RefreshHealthCheckAsync();
    }

    /// <summary>
    /// Esegue un'analisi di integrità (Health Check) sui dati astronomici attuali.
    /// </summary>
    public async Task RefreshHealthCheckAsync()
    {
        _healthCheckCts?.Cancel();
        _healthCheckCts = new CancellationTokenSource();
        var token = _healthCheckCts.Token;
        IsChecking = true;

        try
        {
            await Task.Delay(150, token); // Debounce per non sovraccaricare durante la digitazione
            
            var currentHeader = _sourceNode.ActiveRenderer?.Data?.FitsHeader;
            if (currentHeader == null || _allItems.Count == 0) 
            { 
                IsChecking = false; 
                ResetStatusToWaiting(); 
                return; 
            }

            // Analisi in background tramite i parser del Service
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

            // Aggiornamento visuale degli indicatori
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
            FilteredItems = new ObservableCollection<FitsHeaderItem>(_allItems);
        }
        else
        {
            var results = _allItems
                .Where(item => 
                    (item.Key?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (item.Value?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (item.Comment?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(item => item.Key?.StartsWith(query, StringComparison.OrdinalIgnoreCase) == true ? 0 : 1);
            
            FilteredItems = new ObservableCollection<FitsHeaderItem>(results);
        }
    }

    /// <summary>
    /// Restituisce un nuovo Header FITS basato sulle modifiche apportate nella UI.
    /// </summary>
    public Header GetUpdatedHeader()
    {
        var activeHeader = _sourceNode.ActiveRenderer?.Data?.FitsHeader;
        if (activeHeader == null) return new Header();

        // DELEGA: Il servizio sa come iniettare correttamente i dati nell'oggetto FITS
        return _metadataService.ReconstructHeader(activeHeader, _allItems);
    }

    [RelayCommand]
    private void AddNewKey()
    {
        string newKeyName = !string.IsNullOrWhiteSpace(SearchText) ? SearchText.Trim().ToUpper() : "NEW_KEY";
        var newItem = new FitsHeaderItem { Key = newKeyName, Value = "0", Comment = "Aggiunta manuale", IsModified = true };
        
        // Logica di inserimento: cerchiamo di stare prima della chiave END
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

    // --- GESTIONE MEMORIA (IDisposable) ---
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