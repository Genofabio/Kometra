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
using KomaLab.Services.Data;
using KomaLab.ViewModels.Nodes;
using nom.tam.fits;
// Importiamo il Service

namespace KomaLab.ViewModels.Tools;

// ---------------------------------------------------------------------------
// FILE: HeaderEditorToolViewModel.cs
// DESCRIZIONE:
// ViewModel per l'editor degli Header FITS.
// Si occupa esclusivamente della logica di presentazione (filtro, selezione, status UI).
// Delega tutta la logica di business (Parsing, Validazione, Ricostruzione FITS)
// al FitsMetadataService, garantendo coerenza e sicurezza dei dati.
// ---------------------------------------------------------------------------

public partial class HeaderEditorToolViewModel : ObservableObject
{
    private readonly ImageNodeViewModel _sourceNode;
    private readonly FitsMetadataService _metadataService; // Il nostro "Brain"
    private readonly List<FitsHeaderItem> _allItems = new();
    private CancellationTokenSource? _healthCheckCts;

    public event Action? RequestScrollToSelection;

    // --- COLORI STATUS ---
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#03A077"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E6606A"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#808080"));

    // --- PROPRIETÀ UI ---

    [ObservableProperty] 
    private ObservableCollection<FitsHeaderItem> _filteredItems = new();

    [ObservableProperty] private FitsHeaderItem? _selectedItem;

    [ObservableProperty] private string _currentFileName = "N/A";
    [ObservableProperty] private string _imageCounterText = "";
    [ObservableProperty] private bool _isMultipleImages;
    
    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    // --- SEMAFORI ---
    [ObservableProperty] private bool _isChecking = false;

    // --- HEALTH CHECK UI ---
    [ObservableProperty] private IBrush _dateStatusColor = PendingBrush;
    [ObservableProperty] private string _dateStatusText = "In attesa...";
    
    [ObservableProperty] private IBrush _locationStatusColor = PendingBrush;
    [ObservableProperty] private string _locationStatusText = "In attesa...";
    
    [ObservableProperty] private IBrush _wcsStatusColor = PendingBrush;
    [ObservableProperty] private string _wcsStatusText = "In attesa...";

    public HeaderEditorToolViewModel(ImageNodeViewModel sourceNode)
    {
        _sourceNode = sourceNode ?? throw new ArgumentNullException(nameof(sourceNode));
        
        // Istanziamo il service (o lo iniettiamo via DI se il progetto lo supporta)
        _metadataService = new FitsMetadataService();

        IsMultipleImages = _sourceNode is MultipleImagesNodeViewModel;
        
        _sourceNode.PropertyChanged += OnSourceNodePropertyChanged;
        LoadCurrentHeader();
    }

    private void OnSourceNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageNodeViewModel.ActiveRenderer) || 
            e.PropertyName == "CurrentIndex" || 
            e.PropertyName == "CurrentImageText")
        {
            LoadCurrentHeader();
        }
    }

    private async void LoadCurrentHeader()
    {
        IsChecking = true;
        ResetStatusToWaiting(); 

        var header = _sourceNode.ActiveRenderer?.Data?.FitsHeader;

        // Gestione Titolo Finestra
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

        // --- REFACTORING: Delega al Service ---
        // Il VM non deve sapere come parsare HIERARCH o iterare cursori.
        var parsedItems = await Task.Run(() => _metadataService.ParseForEditor(header));

        _allItems.Clear();
        _allItems.AddRange(parsedItems);
        
        ApplyFilter();
        RefreshHealthCheck();
    }

    public async void RefreshHealthCheck()
    {
        _healthCheckCts?.Cancel();
        _healthCheckCts = new CancellationTokenSource();
        var token = _healthCheckCts.Token;
        IsChecking = true;

        try
        {
            await Task.Delay(100, token); // Debounce
            
            var currentHeader = _sourceNode.ActiveRenderer?.Data?.FitsHeader;
            
            if (currentHeader == null || _allItems.Count == 0) 
            { 
                IsChecking = false; 
                ResetStatusToWaiting(); 
                return; 
            }

            // Eseguiamo i controlli in background usando il Service
            var result = await Task.Run(() => 
            {
                token.ThrowIfCancellationRequested();
                var status = new HealthCheckResult();
                
                // 1. DATA (Usa logica centralizzata Service)
                var dt = _metadataService.GetObservationDate(currentHeader);
                if (dt.HasValue) 
                    status.DateMsg = $"Acquisizione: {dt.Value:yyyy-MM-dd HH:mm:ss}";
                else 
                    status.DateError = "Timestamp mancante o formato non valido.";
                
                token.ThrowIfCancellationRequested();

                // 2. LOCATION (Usa logica centralizzata Service)
                var loc = _metadataService.GetObservatoryLocation(currentHeader);
                if (loc != null)
                    status.LocMsg = $"Lat: {loc.Latitude:F4}, Lon: {loc.Longitude:F4}";
                else
                    status.LocError = "Coordinate geografiche non trovate.";

                token.ThrowIfCancellationRequested();

                // 3. WCS (Usa logica centralizzata Service)
                var wcs = _metadataService.ExtractWcs(currentHeader);
                if (wcs != null && wcs.IsValid)
                    status.WcsMsg = $"Soluzione valida (Scale: {wcs.PixelScaleArcsec:F2}\" / px)";
                else
                    status.WcsError = "Soluzione astrometrica incompleta o assente.";

                return status;
            }, token);

            if (token.IsCancellationRequested) return;
            IsChecking = false;
            
            // Aggiornamento UI
            if (result.DateError == null) { DateStatusColor = SuccessBrush; DateStatusText = result.DateMsg!; }
            else { DateStatusColor = ErrorBrush; DateStatusText = result.DateError; }
            
            if (result.LocError == null) { LocationStatusColor = SuccessBrush; LocationStatusText = result.LocMsg!; }
            else { LocationStatusColor = ErrorBrush; LocationStatusText = result.LocError; }
            
            if (result.WcsError == null) { WcsStatusColor = SuccessBrush; WcsStatusText = result.WcsMsg!; }
            else { WcsStatusColor = ErrorBrush; WcsStatusText = result.WcsError; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            IsChecking = false; 
            Debug.WriteLine($"[HealthCheck] Error: {ex.Message}"); 
        }
    }

    private void ResetStatusToWaiting()
    {
        DateStatusColor = PendingBrush; DateStatusText = "Analisi in corso...";
        LocationStatusColor = PendingBrush; LocationStatusText = "Analisi in corso...";
        WcsStatusColor = PendingBrush; WcsStatusText = "Analisi in corso...";
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
                    (item.Key?.Contains((string)query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (item.Value?.Contains((string)query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (item.Comment?.Contains((string)query, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(item => 
                {
                    if (item.Key?.Contains((string)query, StringComparison.OrdinalIgnoreCase) == true) return 0;
                    return 2;
                });
            FilteredItems = new ObservableCollection<FitsHeaderItem>(results);
        }
    }

    public Header GetUpdatedHeader()
    {
        // --- REFACTORING: Delega al Service ---
        // Il VM non deve sapere quali chiavi sono "di sistema" o come ricostruire HIERARCH.
        var activeHeader = _sourceNode.ActiveRenderer?.Data?.FitsHeader;
        
        if (activeHeader == null) return new Header();

        return _metadataService.ReconstructHeader(activeHeader, _allItems);
    }

    [RelayCommand]
    private void AddNewKey()
    {
        string newKeyName = !string.IsNullOrWhiteSpace(SearchText) ? SearchText.Trim().ToUpper() : "NEW_KEY";
        var newItem = new FitsHeaderItem { Key = newKeyName, Value = "0", Comment = "Manuale", IsModified = true };
        SearchText = ""; 
        
        // Logica UI per inserire prima di END (se esiste visualmente)
        var endIndex = _allItems.FindIndex(x => x.Key.Trim().ToUpper() == "END");
        if (endIndex >= 0) _allItems.Insert(endIndex, newItem);
        else _allItems.Add(newItem);

        ApplyFilter(); 
        SelectedItem = newItem;
        RefreshHealthCheck();
        
        RequestScrollToSelection?.Invoke();
    }
    
    [RelayCommand]
    private void DeleteRow()
    {
        var item = SelectedItem;
        if (item == null || item.IsReadOnly) return;
        _allItems.Remove(item);
        ApplyFilter();
        RefreshHealthCheck();
    }

    [RelayCommand] private void NextImage() { if (_sourceNode is MultipleImagesNodeViewModel m && m.NextImageCommand.CanExecute(null)) m.NextImageCommand.Execute(null); }
    [RelayCommand] private void PreviousImage() { if (_sourceNode is MultipleImagesNodeViewModel m && m.PreviousImageCommand.CanExecute(null)) m.PreviousImageCommand.Execute(null); }

    // DTO locale per passare i dati dal Task background alla UI
    private class HealthCheckResult
    {
        public string? DateMsg; public string? DateError;
        public string? LocMsg; public string? LocError;
        public string? WcsMsg; public string? WcsError;
    }
}