using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;       // Necessario per CancellationToken
using System.Threading.Tasks; // Necessario per Task.Run
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using nom.tam.fits;
using KomaLab.Models;
using KomaLab.Services.Astrometry; // FONDAMENTALE: Usa i servizi esistenti

namespace KomaLab.ViewModels;

public partial class HeaderEditorViewModel : ObservableObject
{
    private readonly ImageNodeViewModel _sourceNode;
    
    // Lista master di tutti gli elementi (non filtrata)
    private readonly List<FitsHeaderItem> _allItems = new();

    // Token per gestire l'annullamento dei calcoli semaforici (Race Condition Fix)
    private CancellationTokenSource? _healthCheckCts;

    // Collezione visualizzata nella DataGrid
    public ObservableCollection<FitsHeaderItem> FilteredItems { get; } = new();

    // Selezione corrente (per scroll automatico ed eliminazione)
    [ObservableProperty] private FitsHeaderItem? _selectedItem;

    // --- INFO FILE E NAVIGAZIONE ---
    [ObservableProperty] private string _currentFileName = "N/A";
    [ObservableProperty] private string _imageCounterText = "";
    [ObservableProperty] private bool _isMultipleImages;
    
    // --- RICERCA ---
    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    // --- SEMAFORI (Stato Metadati) ---
    [ObservableProperty] private IBrush _dateStatusColor = Brushes.Gray;
    [ObservableProperty] private string _dateStatusText = "Verifica in corso...";
    
    [ObservableProperty] private IBrush _locationStatusColor = Brushes.Gray;
    [ObservableProperty] private string _locationStatusText = "Verifica in corso...";
    
    [ObservableProperty] private IBrush _wcsStatusColor = Brushes.Gray;
    [ObservableProperty] private string _wcsStatusText = "Verifica in corso...";

    public HeaderEditorViewModel(ImageNodeViewModel sourceNode)
    {
        _sourceNode = sourceNode;
        IsMultipleImages = _sourceNode is MultipleImagesNodeViewModel;
        
        // Attiva supporto per chiavi lunghe (es. HIERARCH CAHA TEL GEOLAT)
        FitsFactory.UseHierarch = true; 

        // Sottoscrizione eventi per cambio immagine
        _sourceNode.PropertyChanged += OnSourceNodePropertyChanged;
        
        LoadCurrentHeader();
    }

    private void OnSourceNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Se cambia l'immagine attiva o l'indice dello stack, ricarichiamo tutto
        if (e.PropertyName == nameof(ImageNodeViewModel.ActiveRenderer) || 
            e.PropertyName == "CurrentIndex" || 
            e.PropertyName == "CurrentImageText")
        {
            LoadCurrentHeader();
        }
    }

    /// <summary>
    /// Legge l'header FITS dall'immagine corrente e popola la lista.
    /// </summary>
    private void LoadCurrentHeader()
    {
        var renderer = _sourceNode.ActiveRenderer;
        var header = renderer?.Data?.FitsHeader;

        // 1. Aggiorna Info File UI
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

        // 2. Caricamento Dati in Lista
        _allItems.Clear();
        if (header != null)
        {
            var cursor = header.GetCursor();
            while (cursor.MoveNext())
            {
                nom.tam.fits.HeaderCard? card = null;
                
                // Gestione robusta del cursore (può essere HeaderCard o DictionaryEntry)
                if (cursor.Current is nom.tam.fits.HeaderCard hc) card = hc;
                else if (cursor.Current is System.Collections.DictionaryEntry de && de.Value is nom.tam.fits.HeaderCard hcd) card = hcd;

                if (card != null && !string.IsNullOrWhiteSpace(card.Key))
                {
                    _allItems.Add(new FitsHeaderItem
                    {
                        Key = card.Key,
                        Value = card.Value ?? "",
                        Comment = card.Comment ?? "",
                        IsReadOnly = IsSystemKey(card.Key),
                        IsModified = false
                    });
                }
            }
        }

        // 3. Aggiorna Vista e Stato
        ApplyFilter();
        
        // Avvia il check asincrono dei semafori
        RefreshHealthCheck();
    }

    /// <summary>
    /// Analizza l'header per verificare la presenza di dati critici (Data, Luogo, WCS).
    /// Eseguito in ASINCRONO per non bloccare la UI e prevenire Race Conditions.
    /// </summary>
    /// <summary>
    /// Analizza l'header per verificare la presenza di dati critici.
    /// Resetta lo stato visivo PRIMA di iniziare il calcolo per evitare confusione.
    /// </summary>
    public async void RefreshHealthCheck()
    {
        // 1. STOP AI CALCOLI VECCHI
        _healthCheckCts?.Cancel();
        _healthCheckCts = new CancellationTokenSource();
        var token = _healthCheckCts.Token;

        // 2. RESET VISIVO IMMEDIATO (Fix del problema "Rosso che diventa Verde")
        // Appena inizia il check, impostiamo tutto a "Grigio/Verifica..."
        // Così l'utente non vede mai i risultati dell'immagine precedente su quella nuova.
        DateStatusColor = Brushes.Gray;
        DateStatusText = "Verifica in corso...";
        
        LocationStatusColor = Brushes.Gray;
        LocationStatusText = "Verifica in corso...";
        
        WcsStatusColor = Brushes.Gray;
        WcsStatusText = "Verifica in corso...";

        try
        {
            // 3. SNAPSHOT DATI (Veloce, sul thread UI)
            var headerSnapshot = GetUpdatedHeader();

            // 4. CALCOLO BACKGROUND (Pesante, non blocca la UI)
            var result = await Task.Run(() => 
            {
                token.ThrowIfCancellationRequested();
                var status = new HealthCheckResult();

                // --- A. DATA ---
                string? dateVal = headerSnapshot.GetStringValue("DATE-OBS");
                if (string.IsNullOrWhiteSpace(dateVal)) dateVal = headerSnapshot.GetStringValue("DATE");
                
                if (DateTime.TryParse(dateVal, out DateTime dt))
                    status.DateMsg = $"Acquisizione: {dt:yyyy-MM-dd HH:mm:ss}";
                else
                    status.DateError = "Timestamp mancante (DATE-OBS/DATE).";

                token.ThrowIfCancellationRequested();

                // --- B. LUOGO ---
                var loc = FitsMetadataReader.ReadObservatoryLocation(headerSnapshot);
                if (loc != null)
                    status.LocMsg = $"Lat: {loc.Latitude:F4}, Lon: {loc.Longitude:F4}";
                else
                    status.LocError = "Coordinate geografiche non trovate.";

                token.ThrowIfCancellationRequested();

                // --- C. WCS ---
                var wcs = WcsHeaderParser.Parse(headerSnapshot);
                if (wcs.IsValid)
                    status.WcsMsg = $"Soluzione valida ({wcs.ProjectionType}).";
                else
                    status.WcsError = "Soluzione astrometrica incompleta.";

                return status;

            }, token);

            // 5. APPLICAZIONE RISULTATI
            // Se nel frattempo l'utente ha cambiato ancora immagine, 'token' sarà cancellato
            // e noi non aggiorneremo la UI con dati vecchi.
            if (token.IsCancellationRequested) return;

            // Applica Data
            if (result.DateError == null) 
            { 
                DateStatusColor = Brushes.LightGreen; 
                DateStatusText = result.DateMsg!; 
            }
            else 
            { 
                DateStatusColor = Brushes.IndianRed; 
                DateStatusText = result.DateError; 
            }

            // Applica Luogo
            if (result.LocError == null) 
            { 
                LocationStatusColor = Brushes.LightGreen; 
                LocationStatusText = result.LocMsg!; 
            }
            else 
            { 
                LocationStatusColor = Brushes.IndianRed; 
                LocationStatusText = result.LocError; 
            }

            // Applica WCS
            if (result.WcsError == null) 
            { 
                WcsStatusColor = Brushes.LightGreen; 
                WcsStatusText = result.WcsMsg!; 
            }
            else 
            { 
                WcsStatusColor = Brushes.IndianRed; 
                WcsStatusText = result.WcsError; 
            }

        }
        catch (OperationCanceledException)
        {
            // Normale: un nuovo calcolo ha interrotto questo.
            // L'interfaccia rimarrà "Grigia" finché il nuovo calcolo non finisce.
        }
        catch (Exception ex)
        {
            // Errore imprevisto
            System.Diagnostics.Debug.WriteLine($"[HealthCheck] Error: {ex.Message}");
            DateStatusColor = Brushes.IndianRed;
            DateStatusText = "Errore durante la verifica.";
        }
    }

    /// <summary>
    /// Rigenera un oggetto Header FITS valido a partire dai dati nella griglia.
    /// Include protezione contro crash per dati malformati.
    /// </summary>
    public Header GetUpdatedHeader()
    {
        FitsFactory.UseHierarch = true;
        var newHeader = new Header();
        
        // Copia le chiavi di sistema originali (SIMPLE, BITPIX, NAXIS...)
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

        // Aggiungi le righe utente
        foreach (var item in _allItems)
        {
            // Salta chiavi sistema o vuote
            if (IsSystemKey(item.Key) || string.IsNullOrWhiteSpace(item.Key)) continue;

            try
            {
                // Tenta di salvare come numero (per precisione)
                if (double.TryParse(item.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double dVal))
                {
                    newHeader.AddValue(item.Key, dVal, item.Comment);
                }
                else
                {
                    // Altrimenti salva come stringa
                    newHeader.AddValue(item.Key, item.Value ?? "", item.Comment);
                }
            }
            catch (Exception)
            {
                // Fallback di emergenza: se la riga è corrotta, salvala come testo semplice o saltala
                // Questo previene il crash dell'intera applicazione
                try { newHeader.AddValue(item.Key, item.Value ?? "ERR", "Error saving row"); } catch { }
            }
        }
        return newHeader;
    }

    // --- COMANDI UI ---

    [RelayCommand]
    private void AddNewKey()
    {
        // Usa il testo di ricerca come nome chiave, se presente
        string newKeyName = !string.IsNullOrWhiteSpace(SearchText) ? SearchText.Trim().ToUpper() : "NEW_KEY";

        var newItem = new FitsHeaderItem 
        { 
            Key = newKeyName, 
            Value = "0", 
            Comment = "Manuale", 
            IsModified = true 
        };
        
        SearchText = ""; // Pulisce la ricerca per mostrare tutto

        // Inserimento "Intelligente": Prima della chiave END
        var endItem = _allItems.FirstOrDefault(x => x.Key.Trim().ToUpper() == "END");
        if (endItem != null) 
            _allItems.Insert(_allItems.IndexOf(endItem), newItem);
        else 
            _allItems.Add(newItem);
        
        ApplyFilter(); 
        SelectedItem = newItem; // Scroll automatico
        RefreshHealthCheck();   // Aggiorna semafori
    }
    
    [RelayCommand]
    private void DeleteRow()
    {
        var item = SelectedItem;
        if (item == null || item.IsReadOnly) return;

        if (_allItems.Contains(item)) _allItems.Remove(item);
        if (FilteredItems.Contains(item)) FilteredItems.Remove(item);
        
        RefreshHealthCheck(); // Aggiorna semafori
    }

    [RelayCommand]
    private void NextImage()
    {
        if (_sourceNode is MultipleImagesNodeViewModel m && m.NextImageCommand.CanExecute(null)) 
            m.NextImageCommand.Execute(null);
    }

    [RelayCommand]
    private void PreviousImage()
    {
        if (_sourceNode is MultipleImagesNodeViewModel m && m.PreviousImageCommand.CanExecute(null)) 
            m.PreviousImageCommand.Execute(null);
    }

    // --- LOGICA FILTRO E UTILS ---

    private void ApplyFilter()
    {
        FilteredItems.Clear();
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
                    // Ordinamento rilevanza: Chiave > Valore > Commento
                    if (item.Key?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) return 0;
                    if (item.Value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) return 1;
                    return 2;
                });
        }
        foreach (var item in results) FilteredItems.Add(item);
    }

    private bool IsSystemKey(string key)
    {
        var sysKeys = new[] { "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "EXTEND", "BSCALE", "BZERO" };
        return sysKeys.Contains(key.ToUpper());
    }
    
    // DTO privato per passaggio dati thread-safe
    private class HealthCheckResult
    {
        public string? DateMsg;
        public string? DateError;
        public string? LocMsg;
        public string? LocError;
        public string? WcsMsg;
        public string? WcsError;
    }
}