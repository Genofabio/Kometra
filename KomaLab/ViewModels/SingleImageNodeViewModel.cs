using System;
using System.Threading.Tasks;
using Avalonia;
using KomaLab.Models;
using KomaLab.Services;
using nom.tam.fits;
using CommunityToolkit.Mvvm.ComponentModel; // <-- 1. Aggiungi questo using

namespace KomaLab.ViewModels;

// Assicurati che la classe sia 'partial'
public partial class SingleImageNodeViewModel : BaseNodeViewModel 
{
    private readonly IFitsService _fitsService;
    private const double ESTIMATED_UI_HEIGHT = 60.0; // Per calcolare la dimensione

    // --- 2. ERRORE LOGICO CORRETTO ---
    // Sostituito: public FitsDisplayViewModel FitsImage { get; private set; }
    // Con:
    [ObservableProperty]
    private FitsDisplayViewModel _fitsImage;
    // Questo notificherà la UI quando 'FitsImage' viene sostituito.

    // --- 3. ERRORE DI COMPILAZIONE CORRETTO ---
    // Aggiunta l'implementazione della proprietà astratta
    public Size EstimatedTotalSize
    {
        get
        {
            // Usa il campo privato _fitsImage
            if (FitsImage.ImageSize == default(Size))
                return new Size(200, 150); // Dimensione di fallback

            return new Size(FitsImage.ImageSize.Width, FitsImage.ImageSize.Height + ESTIMATED_UI_HEIGHT);
        }
    }

    // --- Costruttore ---
    public SingleImageNodeViewModel(
        BoardViewModel parentBoard, 
        NodeModel model, 
        IFitsService fitsService) 
        : base(parentBoard, model)
    {
        _fitsService = fitsService;
        
        var placeholderModel = new FitsImageData
        {
            RawData = Array.Empty<byte[]>(),
            FitsHeader = new Header(),
            ImageSize = default
        };
        // Assegna al campo privato _fitsImage
        _fitsImage = new FitsDisplayViewModel(placeholderModel, _fitsService);
    }
    
    // --- Metodi ---

    public async Task LoadDataAsync()
    {
        try
        {
            var imageData = await _fitsService.LoadFitsFromFileAsync(Model.ImagePath);

            if (imageData == null)
            {
                throw new Exception("I dati FITS caricati sono nulli o non validi.");
            }

            // Questo ora assegna al campo e [ObservableProperty]
            // notificherà la UI, aggiornando {Binding FitsImage.Image}
            FitsImage = new FitsDisplayViewModel(imageData, _fitsService);
            FitsImage.Initialize();
            
            // Questo ora funziona perché la proprietà EstimatedTotalSize esiste
            OnPropertyChanged(nameof(EstimatedTotalSize)); 
        }
        catch (Exception ex)
        {
            Title = $"ERRE: {ex.Message.Split('\n')[0]}";
        }
    }
}