using System;
using System.Collections.Generic; 
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq; 
using System.Threading.Tasks;
using Avalonia;
using KomaLab.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;

namespace KomaLab.ViewModels;

public partial class BoardViewModel : ObservableObject
{
    // --- Dipendenze ---
    private readonly INodeViewModelFactory _nodeFactory;
    private readonly IDialogService _dialogService; 
    private readonly IWindowService _windowService;
    private readonly IFitsService _fitsService;           // Per salvare
    private readonly IImageOperationService _opsService;  // Per lo stacking (NUOVO)

    // --- Proprietà ---
    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private double _scale = 0.46;
    [ObservableProperty] private Rect _viewBounds;
    [ObservableProperty] private BaseNodeViewModel? _selectedNode;
    
    public ObservableCollection<BaseNodeViewModel> Nodes { get; } = new();
    private int _maxZIndex;

    // --- Costruttore ---
    public BoardViewModel(
        INodeViewModelFactory nodeFactory, 
        IDialogService dialogService, 
        IWindowService windowService,
        IFitsService fitsService,
        IImageOperationService opsService) // Inietto il servizio operazioni
    {
        _nodeFactory = nodeFactory;
        _dialogService = dialogService; 
        _windowService = windowService;
        _fitsService = fitsService;
        _opsService = opsService;
    }
    
    // --- Comandi ---

    [RelayCommand]
    private async Task AddNode()
    {
        var imagePaths = await _dialogService.ShowOpenFitsFileDialogAsync();
        if (imagePaths != null)
        {
            var enumerable = imagePaths as string[] ?? imagePaths.ToArray();
            if (!enumerable.Any()) return; 
        
            var pathList = enumerable.ToList();
            double screenCenterX = ViewBounds.Width / 2;
            double screenCenterY = ViewBounds.Height / 2;
            if (Scale == 0) return;
        
            double worldX = (screenCenterX - OffsetX) / Scale;
            double worldY = (screenCenterY - OffsetY) / Scale;
        
            if (pathList.Count == 1)
            {
                await AddSingleNodeAsync(pathList[0], worldX, worldY);
            }
            else
            {
                await AddMultipleNodesAsync(pathList, worldX, worldY);
            }
        }
    }

    // --- Gestione Eventi Nodi (Disaccoppiamento) ---

    private void RegisterNodeEvents(BaseNodeViewModel node)
    {
        // Ci iscriviamo agli eventi del nodo
        node.RequestRemove += OnNodeRequestRemove;
        node.RequestBringToFront += OnNodeRequestBringToFront;
    }

    private void UnregisterNodeEvents(BaseNodeViewModel node)
    {
        node.RequestRemove -= OnNodeRequestRemove;
        node.RequestBringToFront -= OnNodeRequestBringToFront;
    }

    private void OnNodeRequestRemove(BaseNodeViewModel node)
    {
        if (SelectedNode == node) DeselectAllNodes();
        UnregisterNodeEvents(node);
        Nodes.Remove(node);
    }

    private void OnNodeRequestBringToFront(BaseNodeViewModel node)
    {
        _maxZIndex++;
        node.ZIndex = _maxZIndex;
    }

    // --- Helpers Aggiunta Nodi ---

    private async Task AddSingleNodeAsync(string imagePath, double x, double y)
    {
        try
        {
            // La factory ora non richiede 'this'
            var newNode = await _nodeFactory.CreateSingleImageNodeAsync(imagePath, x, y);
            
            // Registriamo gli eventi
            RegisterNodeEvents(newNode);
            
            Nodes.Add(newNode);
            OnNodeRequestBringToFront(newNode);
        }
        catch(Exception ex)
        {
            Debug.WriteLine($"Error adding single node: {ex.Message}");
        }
    }
    
    private async Task AddMultipleNodesAsync(List<string> imagePaths, double x, double y)
    {
        try
        {
            var newNode = await _nodeFactory.CreateMultipleImagesNodeAsync(imagePaths, x, y);
            RegisterNodeEvents(newNode);
            Nodes.Add(newNode);
            OnNodeRequestBringToFront(newNode);
        }
        catch(Exception ex)
        {
            Debug.WriteLine($"Error adding multiple node: {ex.Message}");
        }
    }

    // --- Comandi Operativi ---

    [RelayCommand(CanExecute = nameof(CanResetNormalization))]
    private async Task ResetNormalization()
    {
        if (SelectedNode is ImageNodeViewModel imgNode) // Controllo il tipo corretto
        {
            await imgNode.ResetThresholdsAsync();
        }
    }
    private bool CanResetNormalization() => SelectedNode is ImageNodeViewModel;
    
    [RelayCommand(CanExecute = nameof(CanShowAlignmentWindow))]
    private async Task ShowAlignmentWindow()
    {
        // Controllo cast sicuro a ImageNodeViewModel (o specifico se serve)
        if (SelectedNode is not ImageNodeViewModel imgNode) return;

        try
        {
            // Il WindowService dovrà essere aggiornato per accettare ImageNodeViewModel
            var newProcessedData = await _windowService.ShowAlignmentWindowAsync(imgNode);

            if (newProcessedData != null)
            {
                await imgNode.ApplyProcessedDataAsync(newProcessedData);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Alignment failed: {ex.Message}");
        }
    }
    private bool CanShowAlignmentWindow() => SelectedNode is ImageNodeViewModel;
    
    [RelayCommand(CanExecute = nameof(CanStackImages))]
    private async Task StackImages(StackingMode mode)
    {
        if (SelectedNode is not MultipleImagesNodeViewModel multiNode) return;

        try
        {
            var rawDataList = await multiNode.GetCurrentDataAsync();
            var sourceImages = rawDataList.Where(d => d != null).Cast<FitsImageData>().ToList();

            if (sourceImages.Count < 2) return;

            // Usa il NUOVO servizio di operazioni
            var resultData = await _opsService.ComputeStackAsync(sourceImages, mode);

            double newX = multiNode.X + 50;
            double newY = multiNode.Y + 50;
        
            string baseTitle = multiNode.Title;
            int idx = baseTitle.LastIndexOf('(');
            if (idx != -1) baseTitle = baseTitle.Substring(0, idx).TrimEnd();
            
            string newTitle = $"{baseTitle} ({mode})"; 

            // Factory per nodo da dati in memoria
            var newNode = await _nodeFactory.CreateSingleImageNodeFromDataAsync(
                resultData, newTitle, newX, newY
            );

            RegisterNodeEvents(newNode);
            Nodes.Add(newNode);
            SetSelectedNode(newNode);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Stacking error: {ex.Message}");
        }
    }
    private bool CanStackImages(StackingMode mode) => SelectedNode is MultipleImagesNodeViewModel;

    // --- Altri Comandi (Pan/Zoom/Selection) ---

    [RelayCommand] private void IncrementOffset() { OffsetX += 20; }
    [RelayCommand] private void ResetBoard() { OffsetX = 0.0; OffsetY = 0.0; Scale = 0.5; }
    
    public void Pan(Vector delta) { OffsetX += delta.X; OffsetY += delta.Y; }
    
    public void Zoom(double deltaY, Point mousePosition)
    {
        double oldScale = Scale;
        double zoomFactor = deltaY > 0 ? 1.1 : 1 / 1.1;
        double newScale = Math.Clamp(oldScale * zoomFactor, 0.1, 10);

        OffsetX = mousePosition.X - (mousePosition.X - OffsetX) * (newScale / oldScale);
        OffsetY = mousePosition.Y - (mousePosition.Y - OffsetY) * (newScale / oldScale);
        Scale = newScale;
    }
    
    public void SetSelectedNode(BaseNodeViewModel nodeToSelect)
    {
        if (SelectedNode == nodeToSelect) return;

        if (SelectedNode != null) SelectedNode.IsSelected = false;
        
        SelectedNode = nodeToSelect;
        
        if (SelectedNode != null) SelectedNode.IsSelected = true;
        
        // Notifica cambio stato comandi
        ResetNormalizationCommand.NotifyCanExecuteChanged();
        ShowAlignmentWindowCommand.NotifyCanExecuteChanged(); 
        StackImagesCommand.NotifyCanExecuteChanged();
        SaveSelectedNodeCommand.NotifyCanExecuteChanged();
    }
    
    public void DeselectAllNodes()
    {
        if (SelectedNode != null) SelectedNode.IsSelected = false;
        SelectedNode = null;
        
        ResetNormalizationCommand.NotifyCanExecuteChanged();
        ShowAlignmentWindowCommand.NotifyCanExecuteChanged(); 
        StackImagesCommand.NotifyCanExecuteChanged();
        SaveSelectedNodeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSaveNode))]
    private async Task SaveSelectedNode()
    {
        // Cast a ImageNodeViewModel perché BaseNode non ha GetActiveImageData
        if (SelectedNode is not ImageNodeViewModel imgNode) return;

        FitsImageData? dataToSave = imgNode.GetActiveImageData();
        if (dataToSave == null) return;

        string defaultName = $"{imgNode.Title}.fits";
        var savePath = await _dialogService.ShowSaveFitsFileDialogAsync(defaultName);
        
        if (!string.IsNullOrWhiteSpace(savePath))
        {
            await _fitsService.SaveFitsFileAsync(dataToSave, savePath);
        }
    }
    private bool CanSaveNode() => SelectedNode is ImageNodeViewModel;
}