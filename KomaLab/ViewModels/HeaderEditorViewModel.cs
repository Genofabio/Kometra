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
using nom.tam.fits;
using KomaLab.Models;
using KomaLab.Services.Astrometry;

namespace KomaLab.ViewModels;

public partial class HeaderEditorViewModel : ObservableObject
{
    private readonly ImageNodeViewModel _sourceNode;
    private readonly List<FitsHeaderItem> _allItems = new();
    private CancellationTokenSource? _healthCheckCts;

    // --- COLORI SPECIFICI RICHIESTI ---
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#03A077")); // Verde
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E6606A"));   // Rosso
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#808080")); // Grigio

    [ObservableProperty] 
    private ObservableCollection<FitsHeaderItem> _filteredItems = new();

    [ObservableProperty] private FitsHeaderItem? _selectedItem;

    [ObservableProperty] private string _currentFileName = "N/A";
    [ObservableProperty] private string _imageCounterText = "";
    [ObservableProperty] private bool _isMultipleImages;
    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    // --- STATO SEMAFORI ---
    [ObservableProperty] private bool _isChecking = false; // Gestisce la visibilità dello stato "in corso"

    [ObservableProperty] private IBrush _dateStatusColor = PendingBrush;
    [ObservableProperty] private string _dateStatusText = "In attesa...";
    
    [ObservableProperty] private IBrush _locationStatusColor = PendingBrush;
    [ObservableProperty] private string _locationStatusText = "In attesa...";
    
    [ObservableProperty] private IBrush _wcsStatusColor = PendingBrush;
    [ObservableProperty] private string _wcsStatusText = "In attesa...";

    public HeaderEditorViewModel(ImageNodeViewModel sourceNode)
    {
        _sourceNode = sourceNode;
        IsMultipleImages = _sourceNode is MultipleImagesNodeViewModel;
        FitsFactory.UseHierarch = true; 
        
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
        // 1. Reset visuale immediato
        IsChecking = true;
        ResetStatusToWaiting(); 

        FitsFactory.UseHierarch = true;
        var renderer = _sourceNode.ActiveRenderer;
        var header = renderer?.Data?.FitsHeader;

        // Gestione Info File
        if (_sourceNode is SingleImageNodeViewModel single) { CurrentFileName = single.Title ?? "N/A"; ImageCounterText = "1 / 1"; }
        else if (_sourceNode is MultipleImagesNodeViewModel multi) 
        { 
            int idx = multi.CurrentIndex;
            if (idx >= 0 && idx < multi.ImagePaths.Count) CurrentFileName = System.IO.Path.GetFileName(multi.ImagePaths[idx]);
            ImageCounterText = multi.CurrentImageText; 
        }

        if (header == null)
        {
            _allItems.Clear();
            ApplyFilter();
            IsChecking = false;
            return; 
        }

        // 2. Caricamento e pulizia dati in Background
        var parsedItems = await Task.Run(() => 
        {
            var tempList = new List<FitsHeaderItem>();
            var cursor = header.GetCursor();
            
            while (cursor.MoveNext())
            {
                nom.tam.fits.HeaderCard? card = null;
                if (cursor.Current is nom.tam.fits.HeaderCard hc) card = hc;
                else if (cursor.Current is System.Collections.DictionaryEntry de && de.Value is nom.tam.fits.HeaderCard hcd) card = hcd;

                if (card != null && !string.IsNullOrWhiteSpace(card.Key))
                {
                    // FIX HIERARCH: Gestisce chiavi malformate o senza spazi
                    if (card.Key.Trim().ToUpper() == "HIERARCH")
                    {
                        string raw = card.ToString();
                        int eqIndex = raw.IndexOf('=');
                        if (eqIndex > 8)
                        {
                            string realKey = raw.Substring(0, eqIndex).Trim();
                            // Inserisce spazio se manca (HIERARCHCAHA -> HIERARCH CAHA)
                            if (realKey.Length > 8 && !char.IsWhiteSpace(realKey[8])) 
                                realKey = realKey.Insert(8, " ");

                            string valPart = raw.Substring(eqIndex + 1).Trim();
                            string realValue = valPart;
                            string realComment = "";

                            int slashIndex = valPart.IndexOf('/');
                            if (slashIndex >= 0)
                            {
                                realValue = valPart.Substring(0, slashIndex).Trim();
                                realComment = valPart.Substring(slashIndex + 1).Trim();
                            }
                            realValue = realValue.Replace("'", "");

                            tempList.Add(new FitsHeaderItem { Key = realKey, Value = realValue, Comment = realComment, IsModified = false });
                            continue;
                        }
                    }

                    // FIX PUNTI: Sostituisce i punti con spazi nelle chiavi HIERARCH
                    string displayKey = card.Key;
                    if (displayKey.Contains(".")) displayKey = displayKey.Replace(".", " ");

                    tempList.Add(new FitsHeaderItem { Key = displayKey, Value = card.Value ?? "", Comment = card.Comment ?? "", IsReadOnly = IsSystemKey(displayKey), IsModified = false });
                }
            }
            return tempList;
        });

        // 3. Aggiornamento UI
        _allItems.Clear();
        _allItems.AddRange(parsedItems);
        ApplyFilter();

        // 4. Analisi semafori
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

            if (_allItems.Count == 0) 
            {
                IsChecking = false;
                ResetStatusToWaiting();
                return;
            }

            // Snapshot sicuro per thread secondario
            var itemsSnapshot = _allItems.Select(x => new { x.Key, x.Value, x.Comment }).ToList();

            var result = await Task.Run(() => 
            {
                token.ThrowIfCancellationRequested();
                var status = new HealthCheckResult();

                // Helper locale robusto che usa la stessa logica "Fuzzy" del Service
                // ma applicata alla lista visuale (per vedere le modifiche in tempo reale)
                string? FindValue(string[] tokens)
                {
                    foreach (var item in itemsSnapshot)
                    {
                        string k = item.Key.ToUpper().Trim();
                        // Ignora chiavi commento per evitare falsi positivi
                        if (k == "COMMENT" || k == "HISTORY" || k == "HIERARCH") continue;

                        foreach (var t in tokens)
                        {
                            // "HIERARCH CAHA TEL GEOLAT" contiene "GEOLAT" -> TROVATO
                            if (k.Contains(t)) 
                            {
                                if (!string.IsNullOrWhiteSpace(item.Value)) return item.Value;
                            }
                        }
                    }
                    return null;
                }

                // 1. DATA
                string? dateVal = FindValue(new[] { "DATE-OBS", "DATE" });
                if (DateTime.TryParse(dateVal, out DateTime dt)) status.DateMsg = $"Acquisizione: {dt:yyyy-MM-dd HH:mm:ss}";
                else status.DateError = "Timestamp mancante (DATE-OBS/DATE).";

                token.ThrowIfCancellationRequested();

                // 2. LUOGO
                string? latStr = FindValue(new[] { "SITELAT", "LATITUDE", "LAT-OBS", "GEOLAT", "GEO_LAT", "OBSGEO-B" });
                string? lonStr = FindValue(new[] { "SITELONG", "LONGITUD", "LONG-OBS", "GEOLON", "GEO_LON", "OBSGEO-L" });
                
                // Usiamo il parser robusto del servizio
                double? lat = FitsMetadataReader.ParseCoordinateString(latStr);
                double? lon = FitsMetadataReader.ParseCoordinateString(lonStr);

                if (lat.HasValue && lon.HasValue) status.LocMsg = $"Lat: {lat:F4}, Lon: {lon:F4}";
                else if (latStr != null && lonStr != null) status.LocMsg = $"Trovati (Raw): {latStr} / {lonStr}";
                else status.LocError = "Coordinate geografiche non trovate.";

                token.ThrowIfCancellationRequested();

                // 3. WCS
                bool hasCrval = FindValue(new[] { "CRVAL1" }) != null;
                bool hasCtype = FindValue(new[] { "CTYPE1" }) != null;
                if (hasCrval && hasCtype) status.WcsMsg = "Soluzione valida (WCS rilevato).";
                else status.WcsError = "Soluzione astrometrica incompleta.";

                return status;

            }, token);

            if (token.IsCancellationRequested) return;

            IsChecking = false;

            // --- APPLICAZIONE COLORI ---
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
        IEnumerable<FitsHeaderItem> results;

        if (string.IsNullOrWhiteSpace(query)) results = _allItems;
        else
        {
            results = _allItems
                .Where(item => 
                    (item.Key?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (item.Value?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (item.Comment?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(item => 
                {
                    if (item.Key?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) return 0;
                    return 2;
                });
        }
        FilteredItems = new ObservableCollection<FitsHeaderItem>(results);
    }

    public Header GetUpdatedHeader()
    {
        FitsFactory.UseHierarch = true;
        var newHeader = new Header();
        
        var activeHeader = _sourceNode.ActiveRenderer?.Data?.FitsHeader;
        if (activeHeader != null)
        {
            var cursor = activeHeader.GetCursor();
            while (cursor.MoveNext())
            {
                if (cursor.Current is HeaderCard hc && IsSystemKey(hc.Key))
                    newHeader.AddCard(hc);
            }
        }

        foreach (var item in _allItems)
        {
            if (IsSystemKey(item.Key) || string.IsNullOrWhiteSpace(item.Key)) continue;
            if (item.Key.Trim().ToUpper() == "END") continue;

            try
            {
                string keyUpper = item.Key.Trim().ToUpper();

                // COMMENT/HISTORY
                if (keyUpper == "COMMENT" || keyUpper == "HISTORY")
                {
                    string text = $"{item.Value} {item.Comment}".Trim();
                    newHeader.AddCard(new HeaderCard(keyUpper, null, text));
                    continue;
                }

                // HIERARCH
                string effectiveKey = item.Key;
                if (keyUpper.StartsWith("HIERARCH ")) effectiveKey = item.Key.Substring(9).Trim();

                // Inserimento Valore
                if (double.TryParse(item.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double dVal))
                    newHeader.AddValue(effectiveKey, dVal, item.Comment);
                else if (bool.TryParse(item.Value, out bool bVal))
                    newHeader.AddValue(effectiveKey, bVal, item.Comment);
                else
                    newHeader.AddValue(effectiveKey, item.Value ?? "", item.Comment);
            }
            catch (Exception ex) { Debug.WriteLine($"[Save] Skip: {ex.Message}"); }
        }
        return newHeader;
    }

    private bool IsSystemKey(string key)
    {
        var sysKeys = new[] { "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "EXTEND", "BSCALE", "BZERO" };
        return sysKeys.Contains(key.ToUpper());
    }

    [RelayCommand]
    private void AddNewKey()
    {
        string newKeyName = !string.IsNullOrWhiteSpace(SearchText) ? SearchText.Trim().ToUpper() : "NEW_KEY";
        var newItem = new FitsHeaderItem { Key = newKeyName, Value = "0", Comment = "Manuale", IsModified = true };
        SearchText = ""; 
        _allItems.Add(newItem);
        ApplyFilter(); 
        SelectedItem = newItem;
        RefreshHealthCheck();
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

    private class HealthCheckResult
    {
        public string? DateMsg; public string? DateError;
        public string? LocMsg; public string? LocError;
        public string? WcsMsg; public string? WcsError;
    }
}