using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services;

namespace KomaLab.ViewModels;

public partial class MultipleImagesNodeViewModel : BaseNodeViewModel
{
    // --- Campi ---
    private readonly IFitsService _fitsService;
    private readonly MultipleImagesNodeModel _stackModel;
    private readonly Size _maxImageSize;
    private readonly int _imageCount;

    // --- Proprietà per la Pila ---
    
    [ObservableProperty]
    private FitsDisplayViewModel? _activeFitsImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentImageText))]
    [NotifyPropertyChangedFor(nameof(CanShowPrevious))]
    [NotifyPropertyChangedFor(nameof(CanShowNext))]
    private int _currentIndex;
    
    [ObservableProperty]
    private double _blackPoint;

    [ObservableProperty]
    private double _whitePoint;

    // --- Proprietà Calcolate ---
    public string CurrentImageText => $"{CurrentIndex + 1} / {_imageCount}";
    public Size MaxImageSize => _maxImageSize;
    protected override Size NodeContentSize => MaxImageSize;
    public bool CanShowPrevious => CurrentIndex > 0;
    public bool CanShowNext => CurrentIndex < _imageCount - 1;

    // --- Costruttore ---
    
    public MultipleImagesNodeViewModel(
        BoardViewModel parentBoard, 
        MultipleImagesNodeModel model,
        IFitsService fitsService,
        Size maxSize,
        FitsImageData initialData) 
        : base(parentBoard, model)
    {
        _fitsService = fitsService;
        _stackModel = model;
        _maxImageSize = maxSize;
        _imageCount = model.ImagePaths.Count; 
        
        _currentIndex = 0;
        
        ActiveFitsImage = new FitsDisplayViewModel(initialData, _fitsService);
        ActiveFitsImage.Initialize();
        
        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;
    }

    // --- Comandi di Navigazione ---

    // CORREZIONE: CanExecute ora punta alla proprietà pubblica 'CanShowPrevious'
    [RelayCommand(CanExecute = nameof(CanShowPrevious))]
    private async Task PreviousImage()
    {
        if (!IsSelected)
        {
            ParentBoard.SetSelectedNode(this);
        }
        
        await LoadImageAtIndexAsync(CurrentIndex - 1);
    }
    
    // CORREZIONE: CanExecute ora punta alla proprietà pubblica 'CanShowNext'
    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task NextImage()
    {
        if (!IsSelected)
        {
            ParentBoard.SetSelectedNode(this);
        }
        
        await LoadImageAtIndexAsync(CurrentIndex + 1);
    }

    // --- Logica di Caricamento Lazy ---

    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _imageCount || index == CurrentIndex)
            return;
        
        ActiveFitsImage?.UnloadData();
        CurrentIndex = index;

        FitsImageData? newModel;
        try
        {
            newModel = await _fitsService.LoadFitsFromFileAsync(_stackModel.ImagePaths[index]);
        }
        catch (System.Exception ex)
        {
            Title = $"Errore: {ex.Message}";
            return; 
        }
        
        if (newModel == null)
        {
            Title = "Errore: Dati FITS non validi";
            return; 
        }
    
        ActiveFitsImage = new FitsDisplayViewModel(newModel, _fitsService);
    
        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;

        ActiveFitsImage.Initialize();

        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
    }
    
    // --- Metodi Parziali ---
    
    partial void OnBlackPointChanged(double value)
    {
        if (ActiveFitsImage != null)
            ActiveFitsImage.BlackPoint = value;
    }
    
    partial void OnWhitePointChanged(double value)
    {
        if (ActiveFitsImage != null)
            ActiveFitsImage.WhitePoint = value;
    }

}