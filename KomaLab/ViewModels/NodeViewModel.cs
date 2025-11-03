using System;
using System.Runtime.InteropServices;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services;
using nom.tam.fits;

namespace KomaLab.ViewModels;

public partial class NodeViewModel : ObservableObject
{
    // --- Attributi (Campi e Proprietà) ---

    // Dipendenze e Dati Strutturali
    private readonly NodeModel _model;
    private readonly IFitsService _fitsService;
    public BoardViewModel ParentBoard { get; }

    // Gestione Posizione
    public double X
    {
        get => _model.X;
        set => SetProperty(_model.X, value, _model, (m, v) => m.X = v);
    }
    public double Y
    {
        get => _model.Y;
        set => SetProperty(_model.Y, value, _model, (m, v) => m.Y = v);
    }

    // Stato Generale del Nodo
    [ObservableProperty]
    private string _title = "";
    [ObservableProperty]
    private bool _isLoading = true;
    [ObservableProperty]
    private bool _isSelected;

    // Gestione Immagine e Dati FITS
    private object? _rawFitsData;
    [ObservableProperty]
    private Header? _fitsHeader;
    [ObservableProperty]
    private Size _imageSize;
    [ObservableProperty]
    private Bitmap? _nodeImage;
    [ObservableProperty]
    private double _blackPoint;
    [ObservableProperty]
    private double _whitePoint;

    // --- Costruttore ---

    public NodeViewModel(BoardViewModel parentBoard, NodeModel model, IFitsService fitsService)
    {
        ParentBoard = parentBoard ?? throw new ArgumentNullException(nameof(parentBoard));
        _model = model;
        _fitsService = fitsService;
        Title = _model.Title;
    }

    // --- Metodi ---

    // Comandi (RelayCommand)
    [RelayCommand]
    private void RemoveSelf()
    {
        ParentBoard.Nodes.Remove(this);
    }

    // Gestione Input (chiamati dal Code-Behind)
    
    /// <summary>
    /// Sposta il nodo in base a un delta (in pixel) proveniente dalla View.
    /// </summary>
    public void MoveNode(Vector screenDelta)
    {
        double currentScale = ParentBoard.Scale;
        if (currentScale == 0) return;

        X += screenDelta.X / currentScale;
        Y += screenDelta.Y / currentScale;
    }

    /// <summary>
    /// Regola le soglie dell'immagine in base all'input della rotellina del mouse.
    /// </summary>
    public void AdjustThresholds(double deltaY, bool isShiftPressed)
    {
        double currentRange = WhitePoint - BlackPoint;
        if (currentRange <= 0) currentRange = 1000; 

        double stepPercentage = 0.10; // 10%
        double deltaAmount = (currentRange * stepPercentage) * deltaY;

        if (isShiftPressed)
        {
            BlackPoint += deltaAmount;
        }
        else
        {
            WhitePoint += deltaAmount;
        }
    }

    // Gestione Dati e Caricamento
    
    /// <summary>
    /// Carica i dati FITS iniziali usando il servizio.
    /// </summary>
    public async Task LoadDataAsync()
    {
        IsLoading = true;
        try
        {
            var imageData = await _fitsService.LoadFitsFromFileAsync(_model.ImagePath);

            _rawFitsData = imageData.RawData;
            FitsHeader = imageData.FitsHeader;
            ImageSize = imageData.ImageSize;
            
            // L'impostazione di queste proprietà innesca On...Changed e rigenera l'immagine
            BlackPoint = imageData.InitialBlackPoint;
            WhitePoint = imageData.InitialWhitePoint;
        }
        catch (Exception ex)
        {
            Title = $"ERRORE: {ex.Message.Split('\n')[0]}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Resetta le soglie ai valori originali calcolati.
    /// </summary>
    public Task ResetThresholds()
    {
        if (IsLoading || _rawFitsData == null || FitsHeader == null)
            return Task.CompletedTask;

        try
        {
            // Ricalcola le soglie dai dati in cache (veloce, senza I/O)
            var (black, white) = _fitsService.CalculateClippedThresholds(_rawFitsData, FitsHeader);
            BlackPoint = black;
            WhitePoint = white;
        }
        catch (Exception ex)
        {
            Title = $"ERRORE Reset: {ex.Message.Split('\n')[0]}";
        }
        return Task.CompletedTask;
    }

    // Metodi Privati Helper (Rigenerazione Immagine)
    
    /// <summary>
    /// Rigenera il Bitmap usando i dati grezzi e le soglie correnti.
    /// </summary>
    private async Task RegeneratePreviewImageAsync()
    {
        if (_rawFitsData == null || FitsHeader == null || ImageSize == default(Size))
            return;

        // Copia i valori per il thread in background
        object rawData = _rawFitsData;
        Header header = FitsHeader;
        int width = (int)ImageSize.Width;
        int height = (int)ImageSize.Height;
        double black = BlackPoint;
        double white = WhitePoint;

        // Delega la normalizzazione (lavoro pesante) al servizio
        byte[] normalizedData = await Task.Run(() =>
            _fitsService.NormalizeData(rawData, header, width, height, black, white)
        );

        // Aggiorna il Bitmap sulla UI
        var writeableBmp = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormats.Gray8, AlphaFormat.Opaque);

        using (var lockedBuffer = writeableBmp.Lock())
        {
            Marshal.Copy(normalizedData, 0, lockedBuffer.Address, normalizedData.Length);
        }

        NodeImage = writeableBmp;
    }

    // Metodi Parziali (Reazioni ai Cambiamenti di Proprietà)
    
    partial void OnBlackPointChanged(double value)
    {
        _ = RegeneratePreviewImageAsync();
    }

    partial void OnWhitePointChanged(double value)
    {
        _ = RegeneratePreviewImageAsync();
    }
}