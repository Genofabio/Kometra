using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models;
using KomaLab.Services;

namespace KomaLab.ViewModels.Helpers;

/// <summary>
/// Questo ViewModel non è un "Nodo".
/// È un "motore" riutilizzabile che gestisce lo stato di visualizzazione
/// (Black/White, Bitmap) di un singolo modello di dati FITS (FitsImageData).
/// </summary>
public partial class FitsDisplayViewModel : ObservableObject
{
    // --- Campi ---
    private readonly FitsImageData _model; 
    private readonly IFitsService _fitsService; 
    
    public Size ImageSize => _model.ImageSize;

    // --- Campo per l'Ottimizzazione ---
    private CancellationTokenSource? _regenerationCts;

    // --- Proprietà (Stato dell'Immagine) ---
    
    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private double _blackPoint;

    [ObservableProperty]
    private double _whitePoint;

    // --- Costruttore ---
    
    public FitsDisplayViewModel(FitsImageData model, IFitsService fitsService)
    {
        _model = model;
        _fitsService = fitsService;
        
        _blackPoint = model.InitialBlackPoint;
        _whitePoint = model.InitialWhitePoint;
    }
    
    /// <summary>
    /// Avvia la prima generazione dell'immagine.
    /// </summary>
    public void Initialize()
    {
        TriggerRegeneration();
    }
    
    // --- Logica di Rigenerazione ---
    partial void OnBlackPointChanged(double value) => TriggerRegeneration();
    partial void OnWhitePointChanged(double value) => TriggerRegeneration();

    /// <summary>
    /// Avvia una rigenerazione "debounced" (anti-sfarfallio).
    /// Annulla qualsiasi task di rigenerazione precedente.
    /// </summary>
    private void TriggerRegeneration()
    {
        // 1. Cancella il task precedente, se esiste
        _regenerationCts?.Cancel();
        
        // 2. Crea un nuovo token per questo task
        _regenerationCts = new CancellationTokenSource();
        var token = _regenerationCts.Token;

        // 3. Avvia il task con il token
        _ = RegeneratePreviewImageAsync(token);
    }

    /// <summary>
    /// Rigenera il Bitmap usando i dati grezzi e le soglie correnti.
    /// (Versione annullabile per evitare lag).
    /// </summary>
    private async Task RegeneratePreviewImageAsync(CancellationToken token)
    {
        byte[]? normalizedData;
        try
        {
            // Chiedi al servizio di normalizzare i dati (in background)
            normalizedData = await Task.Run(() =>
            {
                // Controlla se siamo stati annullati *prima* di iniziare il lavoro pesante
                token.ThrowIfCancellationRequested();
                
                return _fitsService.NormalizeData(
                    _model.RawData, _model.FitsHeader,
                    (int)_model.ImageSize.Width, (int)_model.ImageSize.Height,
                    BlackPoint, WhitePoint);
            }, token); // Passa il token anche a Task.Run
        }
        catch (OperationCanceledException)
        {
            // Significa che l'utente ha mosso di nuovo lo slider.
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore durante la rigenerazione dell'immagine: {ex.Message}");
            return;
        }
        
        if (token.IsCancellationRequested)
        {
            return;
        }

        // Crea il Bitmap (siamo tornati sul thread UI)
        var writeableBmp = new WriteableBitmap(
            new PixelSize((int)_model.ImageSize.Width, (int)_model.ImageSize.Height),
            new Vector(96, 96),
            PixelFormats.Gray8, AlphaFormat.Opaque);

        using (var lockedBuffer = writeableBmp.Lock())
        {
            Marshal.Copy(normalizedData, 0, lockedBuffer.Address, normalizedData.Length);
        }
        
        Image = writeableBmp;
    }
    
    /// <summary>
    /// Ricalcola le soglie ottimali dai dati grezzi
    /// e aggiorna questo ViewModel.
    /// </summary>
    /// <returns>Le nuove soglie calcolate.</returns>
    public async Task<(double Black, double White)> ResetThresholdsAsync()
    {
        // 1. Chiama il servizio usando i DATI PRIVATI
        var (newBlack, newWhite) = await Task.Run(() => 
            _fitsService.CalculateClippedThresholds(
                _model.RawData, 
                _model.FitsHeader)
        );
        
        // 2. Aggiorna le proprietà di questo ViewModel
        //    (questo attiverà automaticamente la rigenerazione
        //    tramite OnBlackPointChanged/OnWhitePointChanged)
        BlackPoint = newBlack;
        WhitePoint = newWhite;

        // 3. Restituisce i nuovi valori al chiamante
        return (newBlack, newWhite);
    }
    
    /// <summary>
    /// Pulisce il Bitmap per liberare RAM.
    /// </summary>
    public void UnloadData()
    {
        _regenerationCts?.Cancel(); 
        Image?.Dispose();
        Image = null;
    }
}