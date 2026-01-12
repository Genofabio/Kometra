using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using KomaLab.Models.Visualization;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using KomaLab.Services.UI;
using KomaLab.Services.Undo;
using KomaLab.ViewModels.Nodes; 

namespace KomaLab.ViewModels;

public partial class BoardViewModel : ObservableObject
{
    // --- Dipendenze ---
    private readonly INodeViewModelFactory _nodeFactory;
    private readonly IDialogService _dialogService;
    private readonly IWindowService _windowService;
    private readonly IFitsIoService _ioService;          
    private readonly IFitsMetadataService _metadataService; 
    private readonly IImageOperationService _opsService;
    private readonly IUndoService _undoService;
    private readonly IMediaExportService _mediaService; 

    // --- Proprietà Visuali ---
    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private double _scale = 0.46;
    [ObservableProperty] private Rect _viewBounds;
    
    // --- Stato Selezione ---
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ResetNormalizationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetNodeViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowAlignmentWindowCommand))]
    [NotifyCanExecuteChangedFor(nameof(StackImagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedNodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveVideoCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleNodeAnimationCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedNodeHeaderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowPlateSolvingWindowCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetVisualizationModeCommand))] 
    [NotifyCanExecuteChangedFor(nameof(ShowPosterizationWindowCommand))]
    private BaseNodeViewModel? _selectedNode;
    
    public bool IsGlobalAnimationRunning => 
        Nodes.OfType<MultipleImagesNodeViewModel>().Any(n => n.IsAnimating);
    
    // --- Collezioni ---
    public ObservableCollection<BaseNodeViewModel> Nodes { get; } = new();
    public ObservableCollection<ConnectionViewModel> Connections { get; } = new();
    
    private int _maxZIndex;

    // --- Costruttore ---
    public BoardViewModel(
        INodeViewModelFactory nodeFactory,
        IDialogService dialogService,
        IWindowService windowService,
        IFitsIoService ioService,           
        IFitsMetadataService metadataService, 
        IImageOperationService opsService,
        IUndoService undoService,
        IMediaExportService mediaService) // Iniettato correttamente
    {
        _nodeFactory = nodeFactory ?? throw new ArgumentNullException(nameof(nodeFactory));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _opsService = opsService ?? throw new ArgumentNullException(nameof(opsService));
        _undoService = undoService ?? throw new ArgumentNullException(nameof(undoService));
        _mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
    }

    // --- Comandi Aggiunta Nodi ---

    [RelayCommand]
    private async Task AddNode()
    {
        var imagePaths = await _dialogService.ShowOpenFitsFileDialogAsync();
        if (imagePaths == null || !imagePaths.Any()) return;

        // Utilizzo del BatchService per la preparazione (ordinamento cronologico)
        var preparedPaths = await _ioService.PrepareBatchAsync(imagePaths);

        Point center = GetCenterOfView();

        if (preparedPaths.Count == 1)
        {
            await AddSingleNodeAsync(preparedPaths[0], center.X, center.Y, centerOnPosition: true);
        }
        else
        {
            await AddMultipleNodesAsync(preparedPaths, center.X, center.Y);
        }
    }

    // --- UNDO / REDO ---
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _undoService.Undo();
    private bool CanUndo() => _undoService.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _undoService.Redo();
    private bool CanRedo() => _undoService.CanRedo;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(UndoCommand) && e.PropertyName != nameof(RedoCommand))
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
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
        var connectionsToRemove = Connections
            .Where(c => c.Source == node || c.Target == node)
            .ToList();

        var action = new DelegateAction(
            "Rimuovi Nodo",
            execute: () =>
            {
                if (node is MultipleImagesNodeViewModel multiNode && multiNode.IsAnimating)
                {
                    multiNode.ToggleAnimation(); 
                }

                if (SelectedNode == node) DeselectAllNodes();
                UnregisterNodeEvents(node);
                
                foreach (var conn in connectionsToRemove)
                {
                    Connections.Remove(conn);
                    conn.Dispose();
                }
                
                Nodes.Remove(node);

                OnPropertyChanged(nameof(IsGlobalAnimationRunning));
                ToggleNodeAnimationCommand.NotifyCanExecuteChanged();
            },
            undo: () =>
            {
                if (!Nodes.Contains(node))
                {
                    Nodes.Add(node);
                    RegisterNodeEvents(node);
                    
                    foreach (var conn in connectionsToRemove)
                    {
                        if (!Connections.Contains(conn)) Connections.Add(conn);
                    }
                    SetSelectedNode(node);
                    
                    OnPropertyChanged(nameof(IsGlobalAnimationRunning));
                    ToggleNodeAnimationCommand.NotifyCanExecuteChanged();
                }
            }
        );

        action.Execute();
        _undoService.RecordAction(action);
    }

    private void OnNodeRequestBringToFront(BaseNodeViewModel node)
    {
        _maxZIndex++;
        node.ZIndex = _maxZIndex;
    }

    // --- Helpers Aggiunta Nodi ---

    private void RegisterNodeWithUndo(BaseNodeViewModel newNode, string actionName)
    {
        var action = new DelegateAction(
            actionName,
            execute: () =>
            {
                if (!Nodes.Contains(newNode))
                {
                    Nodes.Add(newNode);
                    RegisterNodeEvents(newNode);
                    OnNodeRequestBringToFront(newNode);
                }
            },
            undo: () =>
            {
                if (Nodes.Contains(newNode))
                {
                    if (SelectedNode == newNode) DeselectAllNodes();
                    UnregisterNodeEvents(newNode);
                    Nodes.Remove(newNode);
                }
            }
        );

        action.Execute();
        _undoService.RecordAction(action);
    }

    private async Task AddSingleNodeAsync(string imagePath, double x, double y, bool centerOnPosition = false)
    {
        try
        {
            BaseNodeViewModel newNode = await _nodeFactory.CreateSingleImageNodeAsync(imagePath, x, y, centerOnPosition);
            RegisterNodeWithUndo(newNode, "Aggiungi Immagine");
        }
        catch(Exception ex) { Debug.WriteLine($"Error adding single node: {ex.Message}"); }
    }
    
    private async Task AddMultipleNodesAsync(List<string> imagePaths, double x, double y)
    {
        try
        {
            BaseNodeViewModel newNode = await _nodeFactory.CreateMultipleImagesNodeAsync(imagePaths, x, y, centerOnPosition: true);
            RegisterNodeWithUndo(newNode, "Aggiungi Multi-Immagine");
        }
        catch(Exception ex) { Debug.WriteLine($"Error adding multiple node: {ex.Message}"); }
    }
    
    public void CreateConnection(BaseNodeViewModel source, BaseNodeViewModel target)
    {
        var model = new Models.Nodes.ConnectionModel { SourceNodeId = source.Id, TargetNodeId = target.Id };
        var connection = new ConnectionViewModel(model, source, target);
        Connections.Add(connection);
    }
    
    // --- Comandi Operativi ---

    [RelayCommand(CanExecute = nameof(CanResetNormalization))]
    private async Task ResetNormalization()
    {
        if (SelectedNode is ImageNodeViewModel imgNode) await imgNode.ResetThresholdsAsync();
    }
    private bool CanResetNormalization() => SelectedNode is ImageNodeViewModel;
    
    [RelayCommand(CanExecute = nameof(CanResetNodeView))]
    private void ResetNodeView()
    {
        if (SelectedNode is ImageNodeViewModel imgNode) imgNode.ResetView();
    }
    private bool CanResetNodeView() => SelectedNode is ImageNodeViewModel;

    // --- EDITOR HEADER ---
    [RelayCommand(CanExecute = nameof(CanEditHeader))]
    private async Task EditSelectedNodeHeader()
    {
        if (SelectedNode is ImageNodeViewModel imgNode)
        {
            var updatedHeader = await _windowService.ShowHeaderEditorAsync(imgNode);
            if (updatedHeader != null && imgNode.ActiveRenderer != null)
            {
                imgNode.ActiveRenderer.Data.FitsHeader = updatedHeader;
            }
        }
    }
    private bool CanEditHeader() => SelectedNode is ImageNodeViewModel || (SelectedNode is MultipleImagesNodeViewModel m && m.ImagePaths.Count > 0);
    
    // --- PLATE SOLVING ---
    [RelayCommand(CanExecute = nameof(CanShowPlateSolving))]
    private async Task ShowPlateSolvingWindow()
    {
        if (SelectedNode is ImageNodeViewModel imgNode)
        {
            await _windowService.ShowPlateSolvingWindowAsync(imgNode);
        }
    }
    private bool CanShowPlateSolving() => SelectedNode is ImageNodeViewModel;
    
    // --- VISUALIZATION MODE ---
    [RelayCommand(CanExecute = nameof(CanSetVisualizationMode))]
    private void SetVisualizationMode(VisualizationMode mode)
    {
        if (SelectedNode is ImageNodeViewModel imgNode)
        {
            imgNode.VisualizationMode = mode;
            SetVisualizationModeCommand.NotifyCanExecuteChanged();
        }
    }
    private bool CanSetVisualizationMode(VisualizationMode mode) => 
        SelectedNode is ImageNodeViewModel imgNode && imgNode.VisualizationMode != mode;
    
    // --- POSTERIZATION ---
    [RelayCommand(CanExecute = nameof(CanShowPosterization))]
    private async Task ShowPosterizationWindow()
    {
        if (SelectedNode is not ImageNodeViewModel imgNode) return;
        try
        {
            var inputPaths = await imgNode.PrepareInputPathsAsync(_ioService);
            if (inputPaths.Count == 0) return;

            var newPaths = await _windowService.ShowPosterizationWindowAsync(inputPaths, imgNode.VisualizationMode);
            if (newPaths != null && newPaths.Count > 0)
            {
                await HandleProcessingResultAsync(imgNode, newPaths, "Posterizzazione", "(Posterizzata)");
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Posterization failed: {ex}"); }
    }
    private bool CanShowPosterization() => SelectedNode is ImageNodeViewModel;

    // --- ALIGNMENT ---
    [RelayCommand(CanExecute = nameof(CanShowAlignmentWindow))]
    private async Task ShowAlignmentWindow()
    {
        if (SelectedNode is not ImageNodeViewModel imgNode) return;
        try
        {
            var inputPaths = await imgNode.PrepareInputPathsAsync(_ioService);
            if (inputPaths.Count == 0) return;

            var newPaths = await _windowService.ShowAlignmentWindowAsync(inputPaths, imgNode.VisualizationMode);
            if (newPaths != null && newPaths.Count > 0)
            {
                await HandleProcessingResultAsync(imgNode, newPaths, "Allineamento Immagini", "(Allineata)");
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Alignment failed: {ex}"); }
    }
    private bool CanShowAlignmentWindow() => SelectedNode is ImageNodeViewModel;

    // --- STACKING ---
    [RelayCommand(CanExecute = nameof(CanStackImages))]
    private async Task StackImages(StackingMode mode)
    {
        if (SelectedNode is not MultipleImagesNodeViewModel multiNode) return;

        try
        {
            // Validazione compatibilità batch prima dello stacking
            var (isCompatible, error) = await _ioService.ValidateCompatibilityAsync(multiNode.ImagePaths);
            if (!isCompatible)
            {
                Debug.WriteLine($"Stacking abortito: {error}");
                return;
            }

            var rawDataList = await multiNode.GetCurrentDataAsync();
            var sourceImages = rawDataList.Where(d => d != null).Cast<FitsImageData>().ToList();
            if (sourceImages.Count < 2) return;

            var resultData = await _opsService.ComputeStackAsync(sourceImages, mode);
            
            double gap = 300;
            double newX = multiNode.X + multiNode.EstimatedTotalSize.Width + gap;
            double newY = multiNode.Y;
            
            string currentTitle = multiNode.Title;
            string cleanTitle = Regex.Replace(currentTitle, @"\s*\(\d+\s*immagini\)", "", RegexOptions.IgnoreCase);
            string modeString = mode switch {
                StackingMode.Average => "Media",
                StackingMode.Median => "Mediana",
                StackingMode.Sum => "Somma",
                _ => mode.ToString()
            };
            string newTitle = $"{cleanTitle.Trim()} ({modeString})";

            BaseNodeViewModel newNode = await _nodeFactory.CreateSingleImageNodeFromDataAsync(
                resultData, newTitle, newX, newY
            );

            if (newNode is ImageNodeViewModel imgNode)
            {
                imgNode.VisualizationMode = multiNode.VisualizationMode;
            }

            var action = new DelegateAction(
                "Stacking Immagini",
                execute: () =>
                {
                    if (!Nodes.Contains(newNode))
                    {
                        Nodes.Add(newNode);
                        RegisterNodeEvents(newNode);
                        OnNodeRequestBringToFront(newNode);
                        CreateConnection(multiNode, newNode);
                    }
                },
                undo: () =>
                {
                    if (Nodes.Contains(newNode))
                    {
                        var link = Connections.FirstOrDefault(c => c.Source == multiNode && c.Target == newNode);
                        if (link != null) { Connections.Remove(link); link.Dispose(); }

                        if (SelectedNode == newNode) DeselectAllNodes();
                        UnregisterNodeEvents(newNode);
                        Nodes.Remove(newNode);
                    }
                }
            );

            action.Execute();
            _undoService.RecordAction(action);
            SetSelectedNode(newNode);
        }
        catch (Exception ex) { Debug.WriteLine($"Stacking error: {ex.Message}"); }
    }
    private bool CanStackImages(StackingMode mode) => SelectedNode is MultipleImagesNodeViewModel;
    
    // --- ANIMAZIONE ---
    [RelayCommand(CanExecute = nameof(CanToggleAnimation))]
    private void ToggleNodeAnimation()
    {
        var runningNode = Nodes.OfType<MultipleImagesNodeViewModel>().FirstOrDefault(n => n.IsAnimating);

        if (runningNode != null) runningNode.ToggleAnimation(); 
        else if (SelectedNode is MultipleImagesNodeViewModel selectedMulti && selectedMulti.ImagePaths.Count > 1)
        {
            selectedMulti.ToggleAnimation();
        }
        
        OnPropertyChanged(nameof(IsGlobalAnimationRunning)); 
        ToggleNodeAnimationCommand.NotifyCanExecuteChanged();
    }
    private bool CanToggleAnimation()
    {
        bool isAnyAnimating = Nodes.OfType<MultipleImagesNodeViewModel>().Any(n => n.IsAnimating);
        bool isSelectionValid = SelectedNode is MultipleImagesNodeViewModel multi && multi.ImagePaths.Count > 1;
        return isAnyAnimating || isSelectionValid;
    }

    // --- ZOOM / PAN ---
    [RelayCommand] private void IncrementOffset() { OffsetX += 20; }
    [RelayCommand] private void ResetBoard() { OffsetX = 0.0; OffsetY = 0.0; Scale = 0.5; }
    
    public void Pan(Vector delta) { OffsetX += delta.X; OffsetY += delta.Y; }
    
    public void Zoom(double deltaY, Point mousePosition)
    {
        double oldScale = Scale;
        double zoomFactor = deltaY > 0 ? 1.1 : 1 / 1.1;
        double newScale = Math.Clamp(oldScale * zoomFactor, 0.05, 10);
        OffsetX = mousePosition.X - (mousePosition.X - OffsetX) * (newScale / oldScale);
        OffsetY = mousePosition.Y - (mousePosition.Y - OffsetY) * (newScale / oldScale);
        Scale = newScale;
    }
    
    // --- SELEZIONE ---
    public void SetSelectedNode(BaseNodeViewModel nodeToSelect)
    {
        if (SelectedNode == nodeToSelect) return;
        if (SelectedNode != null) SelectedNode.IsSelected = false;
        SelectedNode = nodeToSelect;
        if (SelectedNode != null) SelectedNode.IsSelected = true;
        RefreshCommands();
    }
    
    public void DeselectAllNodes()
    {
        if (SelectedNode != null) SelectedNode.IsSelected = false;
        SelectedNode = null;
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ResetNormalizationCommand.NotifyCanExecuteChanged();
        ShowAlignmentWindowCommand.NotifyCanExecuteChanged();
        StackImagesCommand.NotifyCanExecuteChanged();
        SaveSelectedNodeCommand.NotifyCanExecuteChanged();
        ResetNodeViewCommand.NotifyCanExecuteChanged();
        ToggleNodeAnimationCommand.NotifyCanExecuteChanged();
        SaveVideoCommand.NotifyCanExecuteChanged();
        SetVisualizationModeCommand.NotifyCanExecuteChanged(); 
    }

    // --- SAVE / EXPORT ---
    [RelayCommand(CanExecute = nameof(CanSaveNode))]
    private async Task SaveSelectedNode()
    {
        if (SelectedNode is not ImageNodeViewModel imgNode) return;
        FitsImageData? dataToSave = imgNode.GetActiveImageData();
        if (dataToSave == null) return;

        string defaultName = $"{imgNode.Title}.fits";
        var savePath = await _dialogService.ShowSaveFitsFileDialogAsync(defaultName);
        if (!string.IsNullOrWhiteSpace(savePath)) await _ioService.SaveAsync(dataToSave, savePath);
    }
    private bool CanSaveNode() => SelectedNode is ImageNodeViewModel;
    
    [RelayCommand(CanExecute = nameof(CanSaveVideo))]
    private async Task SaveVideo()
    {
        if (SelectedNode is not MultipleImagesNodeViewModel multiNode) return;
        if (multiNode.ActiveRenderer == null) return;

        string cleanTitle = string.Join("_", multiNode.Title.Split(Path.GetInvalidFileNameChars()));
        string defaultName = $"{cleanTitle}.avi";
        var savePath = await _dialogService.ShowSaveFileDialogAsync(defaultName, "Video AVI", "*.avi");
        if (string.IsNullOrWhiteSpace(savePath)) return;

        try
        {
            var contrastProfile = multiNode.ActiveRenderer.CaptureContrastProfile();
            await _mediaService.ExportVideoAsync(
                multiNode.ImagePaths, 
                savePath, 
                fps: 5.0, 
                profile: contrastProfile,
                mode: multiNode.VisualizationMode
            );
        }
        catch (Exception ex) { Debug.WriteLine($"Video export error: {ex.Message}"); }
    }
    private bool CanSaveVideo() => SelectedNode is MultipleImagesNodeViewModel vm && vm.ImagePaths.Count > 1;
    
    // --- HELPER DI POSIZIONAMENTO ---
    private Point GetCenterOfView()
    {
        if (ViewBounds.Width == 0 || ViewBounds.Height == 0) return new Point(0, 0);
        double screenCenterX = ViewBounds.Width / 2.0;
        double screenCenterY = ViewBounds.Height / 2.0;
        double worldX = (screenCenterX - OffsetX) / Scale;
        double worldY = (screenCenterY - OffsetY) / Scale;
        return new Point(worldX, worldY);
    }
    
    // --- NAVIGAZIONE TASTIERA ---
    [RelayCommand]
    private void PanBoard(string direction)
    {
        double step = 50.0 / Scale;
        switch (direction.ToLower())
        {
            case "left":  Pan(new Vector(step, 0)); break;
            case "right": Pan(new Vector(-step, 0)); break;
            case "up":    Pan(new Vector(0, step)); break;
            case "down":  Pan(new Vector(0, -step)); break;
        }
    }

    [RelayCommand]
    private void ZoomBoard(string mode)
    {
        var center = new Point(ViewBounds.Width / 2.0, ViewBounds.Height / 2.0);
        double delta = mode.ToLower() == "in" ? 120 : -120;
        Zoom(delta, center);
    }
    
    // --- HELPER PROCESSING RESULT ---
    private async Task HandleProcessingResultAsync(ImageNodeViewModel imgNode, List<string> newPaths, string undoName, string titleSuffix)
    {
        double gap = 300;
        double newX = imgNode.X + imgNode.EstimatedTotalSize.Width + gap;
        double newY = imgNode.Y;
        string newTitle = $"{imgNode.Title} {titleSuffix}";

        BaseNodeViewModel newNode;
        if (newPaths.Count == 1)
        {
            newNode = await _nodeFactory.CreateSingleImageNodeAsync(newPaths[0], newX, newY, false);
        }
        else
        {
            var multiNode = await _nodeFactory.CreateMultipleImagesNodeAsync(newPaths, newX, newY, false);
            string? dirPath = Path.GetDirectoryName(newPaths[0]);
            if (dirPath != null && dirPath.Contains("Komalab", StringComparison.OrdinalIgnoreCase))
                multiNode.TemporaryFolderPath = dirPath;
            newNode = multiNode;
        }

        newNode.Title = newTitle;
        if (newNode is ImageNodeViewModel resNode) 
            resNode.VisualizationMode = imgNode.VisualizationMode;

        var action = new DelegateAction(
            undoName,
            execute: () =>
            {
                if (!Nodes.Contains(newNode))
                {
                    Nodes.Add(newNode);
                    RegisterNodeEvents(newNode);
                    OnNodeRequestBringToFront(newNode);
                    CreateConnection(imgNode, newNode);
                }
            },
            undo: () =>
            {
                if (Nodes.Contains(newNode))
                {
                    var link = Connections.FirstOrDefault(c => c.Source == imgNode && c.Target == newNode);
                    if (link != null) { Connections.Remove(link); link.Dispose(); }
                    if (SelectedNode == newNode) DeselectAllNodes();
                    UnregisterNodeEvents(newNode);
                    Nodes.Remove(newNode);
                }
            }
        );

        action.Execute();
        _undoService.RecordAction(action);
        SetSelectedNode(newNode);
    }
}