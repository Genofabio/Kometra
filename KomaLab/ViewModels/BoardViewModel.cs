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
    private readonly IFitsService _fitsService;           
    private readonly IImageOperationService _opsService;  

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
        IImageOperationService opsService) 
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

    // --- Gestione Eventi Nodi ---

    private void RegisterNodeEvents(BaseNodeViewModel node)
    {
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
        
        // FONDAMENTALE: Pulizia Memoria
        node.Dispose();
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
            var newNode = await _nodeFactory.CreateSingleImageNodeAsync(imagePath, x, y);
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
        if (SelectedNode is ImageNodeViewModel imgNode) 
        {
            await imgNode.ResetThresholdsAsync();
        }
    }
    private bool CanResetNormalization() => SelectedNode is ImageNodeViewModel;
    
    // --- LOGICA ALLINEAMENTO (CORRETTA) ---
    [RelayCommand(CanExecute = nameof(CanShowAlignmentWindow))]
    private async Task ShowAlignmentWindow()
    {
        // 1. Controllo generico (va bene sia Single che Multi)
        if (SelectedNode is not ImageNodeViewModel imgNode) return;

        try
        {
            // 2. POLIMORFISMO: Chiediamo al nodo di darci i file di input.
            //    Se è un SingleNode in memoria, si salverà da solo in Temp.
            var inputPaths = await imgNode.PrepareInputPathsAsync(_fitsService);

            if (inputPaths.Count == 0) return; // Nessun dato valido

            // 3. Chiamiamo il servizio (che lavora con stringhe, Low RAM)
            var newPaths = await _windowService.ShowAlignmentWindowAsync(inputPaths);

            if (newPaths != null && newPaths.Count > 0)
            {
                double newX = imgNode.X + 60;
                double newY = imgNode.Y + 60;
                
                // Pulizia titolo
                string cleanTitle = imgNode.Title.Replace(" (Aligned)", "").Trim();
                string newTitle = $"{cleanTitle} (Aligned)";
                
                // Recupera cartella temp per il Dispose
                string? dirPath = System.IO.Path.GetDirectoryName(newPaths[0]);
                bool isTemp = dirPath != null && dirPath.Contains("KomaLab_Aligned");

                BaseNodeViewModel newNode;

                // 4. DECISIONE INTELLIGENTE: Creare Single o Multi?
                if (newPaths.Count == 1)
                {
                    // Se l'allineamento restituisce 1 sola immagine (es. crop su singola)
                    newNode = await _nodeFactory.CreateSingleImageNodeAsync(newPaths[0], newX, newY);
                    // Non impostiamo TemporaryFolderPath su SingleNode (perché non lo supporta ancora),
                    // ma essendo un solo file non è gravissimo. 
                    // (Se vuoi supportarlo, aggiungi la prop TemporaryFolderPath anche a SingleNodeViewModel).
                }
                else
                {
                    // Caso standard: Stack allineato
                    var multiNode = await _nodeFactory.CreateMultipleImagesNodeAsync(newPaths, newX, newY);
                    if (isTemp) multiNode.TemporaryFolderPath = dirPath;
                    newNode = multiNode;
                }

                newNode.Title = newTitle;

                // 5. Aggiunta alla Board
                RegisterNodeEvents(newNode);
                Nodes.Add(newNode);
                SetSelectedNode(newNode);
                OnNodeRequestBringToFront(newNode);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Alignment failed: {ex}");
        }
    }
    
    private bool CanShowAlignmentWindow() => SelectedNode is ImageNodeViewModel;
    
    // --- LOGICA STACKING ---
    [RelayCommand(CanExecute = nameof(CanStackImages))]
    private async Task StackImages(StackingMode mode)
    {
        if (SelectedNode is not MultipleImagesNodeViewModel multiNode) return;

        try
        {
            var rawDataList = await multiNode.GetCurrentDataAsync();
            var sourceImages = rawDataList.Where(d => d != null).Cast<FitsImageData>().ToList();

            if (sourceImages.Count < 2) return;

            var resultData = await _opsService.ComputeStackAsync(sourceImages, mode);
            
            double newX = multiNode.X + 50;
            double newY = multiNode.Y + 50;
        
            string baseTitle = multiNode.Title;
            int idx = baseTitle.LastIndexOf('(');
            if (idx != -1) baseTitle = baseTitle.Substring(0, idx).TrimEnd();
            
            string newTitle = $"{baseTitle} ({mode})"; 

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

    // --- Altri Comandi ---

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