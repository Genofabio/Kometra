using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models;
using KomaLab.Services;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace KomaLab.ViewModels;

/// <summary>
/// Questo ViewModel non è un "Nodo".
/// È un "motore" riutilizzabile che gestisce lo stato di visualizzazione
/// (Black/White, Bitmap) di un singolo modello di dati FITS (FitsImageData).
/// </summary>
public partial class FitsDisplayViewModel : ObservableObject
{
    // --- Campi ---
    private readonly FitsImageData _model; // Il Model con i dati grezzi
    private readonly IFitsService _fitsService; // Il servizio per la normalizzazione
    
    public Size ImageSize => _model.ImageSize;

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

        // Imposta i valori iniziali dal Model
        _blackPoint = model.InitialBlackPoint;
        _whitePoint = model.InitialWhitePoint;
    }
    
    /// <summary>
    /// Avvia la prima generazione dell'immagine.
    /// Chiamato dal ViewModel genitore DOPO l'impostazione.
    /// </summary>
    public void Initialize()
    {
        _ = RegeneratePreviewImageAsync();
    }
    
    // --- Logica di Rigenerazione ---

    // I metodi parziali vengono chiamati automaticamente quando 
    // BlackPoint o WhitePoint cambiano, grazie a [ObservableProperty]
    
    partial void OnBlackPointChanged(double value) => _ = RegeneratePreviewImageAsync();
    partial void OnWhitePointChanged(double value) => _ = RegeneratePreviewImageAsync();

    /// <summary>
    /// Rigenera il Bitmap usando i dati grezzi e le soglie correnti.
    /// (Questa è la logica copiata dal tuo SingleImageNodeViewModel originale).
    /// </summary>
    private async Task RegeneratePreviewImageAsync()
    {
        // 1. Chiedi al servizio di normalizzare i dati
        byte[] normalizedData = await Task.Run(() =>
            _fitsService.NormalizeData(
                _model.RawData, _model.FitsHeader,
                (int)_model.ImageSize.Width, (int)_model.ImageSize.Height,
                BlackPoint, WhitePoint)
        );

        // 2. Crea il Bitmap
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
    /// Pulisce il Bitmap per liberare RAM.
    /// </summary>
    public void UnloadData()
    {
        Image?.Dispose();
        Image = null;
        // In futuro, potremmo anche decidere di liberare _model.RawData qui
        // se la gestione della RAM diventasse un problema critico.
    }
}