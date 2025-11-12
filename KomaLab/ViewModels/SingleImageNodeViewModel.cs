using System;
using System.Threading.Tasks;
using Avalonia;
using KomaLab.Models;
using KomaLab.Services;
using nom.tam.fits;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.ViewModels;

/// <summary>
/// ViewModel per un nodo che visualizza una singola immagine FITS.
/// Eredita il comportamento di base (posizione, selezione) da BaseNodeViewModel.
/// </summary>
public partial class SingleImageNodeViewModel : BaseNodeViewModel 
{
    // --- Campi ---
    private readonly IFitsService _fitsService;
    
    // Riferimento al modello specifico (che contiene ImagePath)
    private readonly SingleImageNodeModel _imageModel;

    // "Motore" di visualizzazione (contiene Bitmap, Black/White)
    [ObservableProperty]
    private Helpers.FitsDisplayViewModel _fitsImage;
    
    public string ImagePath => _imageModel.ImagePath;

    // --- Implementazione Proprietà Astratte ---

    /// <summary>
    /// Restituisce la dimensione del contenuto (l'immagine)
    /// per il calcolo della dimensione totale nella classe base.
    /// </summary>
    protected override Size NodeContentSize
    {
        get
        {
            // Legge la dimensione dal "motore" immagine
            if (FitsImage.ImageSize == default(Size))
                return new Size(200, 150); // Dimensione di fallback
            
            return FitsImage.ImageSize;
        }
    }

    // --- Costruttore ---
    
    public SingleImageNodeViewModel(
        BoardViewModel parentBoard, 
        SingleImageNodeModel model, // Accetta il modello specifico
        IFitsService fitsService) 
        : base(parentBoard, model) // Passa il modello alla classe base
    {
        _fitsService = fitsService;
        _imageModel = model; // Salva il riferimento al modello specifico
        
        // Crea un "motore" segnaposto vuoto
        var placeholderModel = new FitsImageData
        {
            RawData = Array.Empty<byte[]>(),
            FitsHeader = new Header(),
            ImageSize = default
        };
        _fitsImage = new Helpers.FitsDisplayViewModel(placeholderModel, _fitsService);
    }
    
    // --- Metodi ---
    
    // Implementazione del metodo parziale
    // Questo viene chiamato automaticamente dopo che _fitsImage è stato impostato
    partial void OnFitsImageChanged(Helpers.FitsDisplayViewModel? value)
    {
        // 1. Notifico che la MIA proprietà (astratta) è cambiata
        OnPropertyChanged(nameof(NodeContentSize));
        
        // 2. Se vuoi, puoi già inizializzare qui
        value?.Initialize();
    }

    /// <summary>
    /// Carica i dati FITS in modo asincrono.
    /// </summary>
    public async Task LoadDataAsync()
    {
        try
        {
            // Legge il percorso dal modello specifico
            var imageData = await _fitsService.LoadFitsFromFileAsync(_imageModel.ImagePath); 

            if (imageData == null)
            {
                throw new Exception("I dati FITS caricati sono nulli o non validi.");
            }
            
            FitsImage = new Helpers.FitsDisplayViewModel(imageData, _fitsService);
        }
        catch (Exception ex)
        {
            Title = $"ERRE: {ex.Message.Split('\n')[0]}";
        }
    }
}