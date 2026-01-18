using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Nodes;
using KomaLab.Models.Processing;
using KomaLab.Models.Visualization;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.Services.UI;
using KomaLab.Services.Undo;
using KomaLab.ViewModels.Nodes;
using KomaLab.ViewModels.Components;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.ViewModels;

/// <summary>
/// ViewModel principale della Board. Gestisce il grafo dei nodi e coordina i tool di elaborazione.
/// Architettura centralizzata basata sul contratto IImageNavigator.
/// </summary>
public partial class BoardViewModel : ObservableObject
{
    // --- Dipendenze ---
    private readonly INodeViewModelFactory _nodeFactory;
    private readonly IDialogService _dialogService;
    private readonly IWindowService _windowService;
    private readonly IFitsMetadataService _metadataService;
    private readonly IUndoService _undoService;
    private readonly IFitsDataManager _dataManager; 
    private readonly IStackingCoordinator _stackingCoordinator;
    private readonly IVideoExportCoordinator _videoCoordinator;

    // --- Stato Navigazione (Delegato al Viewport) ---
    public BoardViewport Viewport { get; } = new();
    
    // --- Stato Selezione ---
    [ObservableProperty] private BaseNodeViewModel? _selectedNode;

    partial void OnSelectedNodeChanged(BaseNodeViewModel? value)
    {
        ResetNormalizationCommand.NotifyCanExecuteChanged();
        ResetNodeViewCommand.NotifyCanExecuteChanged();
        ShowAlignmentWindowCommand.NotifyCanExecuteChanged();
        StackImagesCommand.NotifyCanExecuteChanged();
        SaveSelectedNodeCommand.NotifyCanExecuteChanged();
        SaveVideoCommand.NotifyCanExecuteChanged();
        ToggleNodeAnimationCommand.NotifyCanExecuteChanged();
        EditSelectedNodeHeaderCommand.NotifyCanExecuteChanged();
        ShowPlateSolvingWindowCommand.NotifyCanExecuteChanged();
        SetVisualizationModeCommand.NotifyCanExecuteChanged();
        ShowPosterizationWindowCommand.NotifyCanExecuteChanged();
    }
    
    public bool IsGlobalAnimationRunning => 
        Nodes.OfType<ImageNodeViewModel>().Any(n => n.Navigator is SequenceNavigator { IsLooping: true });
    
    // --- Collezioni ---
    public ObservableCollection<BaseNodeViewModel> Nodes { get; } = new();
    public ObservableCollection<ConnectionViewModel> Connections { get; } = new();
    
    private int _maxZIndex;

    public BoardViewModel(
        INodeViewModelFactory nodeFactory,
        IDialogService dialogService,
        IWindowService windowService,
        IFitsMetadataService metadataService, 
        IUndoService undoService,
        IFitsDataManager dataManager,
        IStackingCoordinator stackingCoordinator,
        IVideoExportCoordinator videoCoordinator)
    {
        _nodeFactory = nodeFactory ?? throw new ArgumentNullException(nameof(nodeFactory));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _undoService = undoService ?? throw new ArgumentNullException(nameof(undoService));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _stackingCoordinator = stackingCoordinator ?? throw new ArgumentNullException(nameof(stackingCoordinator));
        _videoCoordinator = videoCoordinator ?? throw new ArgumentNullException(nameof(videoCoordinator));
    }

    // ---------------------------------------------------------------------------
    // GESTIONE GRAFO (ADD / REMOVE) CON UNDO
    // ---------------------------------------------------------------------------

    private void AddNodeToGraph(BaseNodeViewModel node, string undoLabel)
    {
        var action = new DelegateAction(undoLabel,
            execute: () => 
            {
                if (!Nodes.Contains(node))
                {
                    Nodes.Add(node);
                    RegisterNodeEvents(node);
                    node.ZIndex = ++_maxZIndex;
                }
                SetSelectedNode(node);
            },
            undo: () => 
            {
                if (Nodes.Contains(node))
                {
                    UnregisterNodeEvents(node);
                    Nodes.Remove(node);
                    DeselectAllNodes();
                }
            }
        );
        action.Execute();
        _undoService.RecordAction(action);
    }

    private void RemoveNodeFromGraph(BaseNodeViewModel node)
    {
        var connectionsToRemove = Connections.Where(c => c.Source == node || c.Target == node).ToList();

        var action = new DelegateAction("Rimuovi Nodo",
            execute: () =>
            {
                if (node is ImageNodeViewModel { Navigator: SequenceNavigator sn }) sn.Stop();
                if (SelectedNode == node) DeselectAllNodes();
                UnregisterNodeEvents(node);
                foreach (var conn in connectionsToRemove) Connections.Remove(conn);
                Nodes.Remove(node);
                OnPropertyChanged(nameof(IsGlobalAnimationRunning));
            },
            undo: () =>
            {
                if (!Nodes.Contains(node))
                {
                    Nodes.Add(node);
                    RegisterNodeEvents(node);
                    foreach (var conn in connectionsToRemove) Connections.Add(conn);
                    SetSelectedNode(node);
                    OnPropertyChanged(nameof(IsGlobalAnimationRunning));
                }
            }
        );
        action.Execute();
        _undoService.RecordAction(action);
    }

    private void RegisterProcessingResult(BaseNodeViewModel newNode, BaseNodeViewModel sourceNode, string tempFilePath, string undoLabel)
    {
        var action = new DelegateAction(undoLabel,
            execute: () => {
                if (!Nodes.Contains(newNode))
                {
                    Nodes.Add(newNode);
                    RegisterNodeEvents(newNode);
                    newNode.ZIndex = ++_maxZIndex;
                    CreateConnection(sourceNode, newNode);
                }
                SetSelectedNode(newNode);
            },
            undo: () => {
                if (Nodes.Contains(newNode))
                {
                    var link = Connections.FirstOrDefault(c => c.Source == sourceNode && c.Target == newNode);
                    if (link != null) Connections.Remove(link);
                    if (SelectedNode == newNode) DeselectAllNodes();
                    UnregisterNodeEvents(newNode);
                    Nodes.Remove(newNode);
                }
            },
            onDispose: (wasExecuted) => {
                if (!wasExecuted && !string.IsNullOrEmpty(tempFilePath))
                    _dataManager.DeleteTemporaryData(tempFilePath);
            }
        );
        action.Execute();
        _undoService.RecordAction(action);
    }

    // ---------------------------------------------------------------------------
    // EVENTI E CONNESSIONI
    // ---------------------------------------------------------------------------

    private void RegisterNodeEvents(BaseNodeViewModel node)
    {
        node.RequestRemove += OnNodeRequestRemove;
        node.RequestBringToFront += n => n.ZIndex = ++_maxZIndex;
    }

    private void UnregisterNodeEvents(BaseNodeViewModel node) => node.RequestRemove -= OnNodeRequestRemove;
    private void OnNodeRequestRemove(BaseNodeViewModel node) => RemoveNodeFromGraph(node);

    public void CreateConnection(BaseNodeViewModel source, BaseNodeViewModel target)
    {
        var connection = new ConnectionViewModel(
            new ConnectionModel { SourceNodeId = source.Id, TargetNodeId = target.Id }, 
            source, target);
        Connections.Add(connection);
    }

    // ---------------------------------------------------------------------------
    // COMANDI PRINCIPALI
    // ---------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddNodeAsync()
    {
        var paths = await _dialogService.ShowOpenFitsFileDialogAsync();
        if (paths == null || !paths.Any()) return;

        var sortedPaths = _metadataService.SortByDate(paths, p => _dataManager.GetHeaderOnlyAsync(p).Result).ToList();
        
        Point centerScreen = new Point(Viewport.ViewportSize.Width / 2.0, Viewport.ViewportSize.Height / 2.0);
        Point pos = Viewport.ToWorldCoordinates(centerScreen);

        BaseNodeViewModel newNode = sortedPaths.Count == 1 
            ? await _nodeFactory.CreateSingleImageNodeAsync(sortedPaths[0], pos.X, pos.Y)
            : await _nodeFactory.CreateMultipleImagesNodeAsync(sortedPaths, pos.X, pos.Y);

        AddNodeToGraph(newNode, sortedPaths.Count == 1 ? "Aggiungi Immagine" : "Aggiungi Sequenza");
    }

    // --- COMANDI PONTE PER KEYBINDINGS (Risolvono gli errori di compilazione) ---

    [RelayCommand]
    private void PanBoard(string direction)
    {
        double step = 100; // Pixel di spostamento per ogni tasto freccia
        switch (direction.ToLower())
        {
            case "left": Viewport.ApplyPan(step, 0); break;
            case "right": Viewport.ApplyPan(-step, 0); break;
            case "up": Viewport.ApplyPan(0, step); break;
            case "down": Viewport.ApplyPan(0, -step); break;
        }
    }

    [RelayCommand]
    private void ZoomBoard(string direction)
    {
        double factor = direction.ToLower() == "in" ? 1.2 : 0.8;
        // Calcola il centro del viewport per uno zoom bilanciato
        var center = new Point(Viewport.ViewportSize.Width / 2, Viewport.ViewportSize.Height / 2);
        Viewport.ApplyZoomAtPoint(factor, center);
    }

    // ---------------------------------------------------------------------------
    // TOOL E ELABORAZIONE
    // ---------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanStackImages))]
    private async Task StackImages(StackingMode mode)
    {
        if (SelectedNode is not ImageNodeViewModel source) return;
        try
        {
            var resultRef = await _stackingCoordinator.ExecuteStackingAsync(source.CurrentFiles, mode);
            var newNode = await _nodeFactory.CreateSingleImageNodeAsync(resultRef.FilePath, source.X + 400, source.Y);
            newNode.Title = $"{source.Title} ({mode})";
            if (newNode is ImageNodeViewModel resNode) resNode.VisualizationMode = source.VisualizationMode;
            RegisterProcessingResult(newNode, source, resultRef.FilePath, "Stacking");
        }
        catch (Exception ex) { Debug.WriteLine($"Stacking failed: {ex.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(CanSaveNode))]
    private async Task SaveSelectedNode()
    {
        if (SelectedNode is not ImageNodeViewModel imgNode || imgNode.ActiveFile == null) return;
        var savePath = await _dialogService.ShowSaveFitsFileDialogAsync(imgNode.ActiveFile.FileName);
        if (string.IsNullOrWhiteSpace(savePath)) return;
        try {
            var fileRef = imgNode.ActiveFile;
            var data = await _dataManager.GetDataAsync(fileRef.FilePath);
            await _dataManager.SaveDataAsync(savePath, data.PixelData, fileRef.ModifiedHeader ?? data.Header);
        } catch (Exception ex) { Debug.WriteLine($"Save failed: {ex.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(CanSaveVideo))]
    private async Task SaveVideo()
    {
        if (SelectedNode is not ImageNodeViewModel imgNode || imgNode.ActiveRenderer == null) return;
        var path = await _dialogService.ShowSaveFileDialogAsync($"{imgNode.Title}.avi", "Video", "*.avi");
        if (string.IsNullOrWhiteSpace(path)) return;
        await _videoCoordinator.ExportVideoAsync(imgNode.CurrentFiles, path, 5.0, imgNode.ActiveRenderer.CaptureContrastProfile(), imgNode.VisualizationMode);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowPosterizationWindow()
    {
        await RunGenericProcessing((paths, mode) => _windowService.ShowPosterizationWindowAsync(paths, mode), "Posterizzazione", "(Posterizzata)");
    }

    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowAlignmentWindow()
    {
        await RunGenericProcessing((paths, mode) => _windowService.ShowAlignmentWindowAsync(paths, mode), "Allineamento", "(Allineata)");
    }
    
    private async Task RunGenericProcessing(Func<List<string>, VisualizationMode, Task<List<string>?>> windowAction, string undoLabel, string titleSuffix)
    {
        if (SelectedNode is not ImageNodeViewModel imgNode) return;
        var inputs = imgNode.GetManagedFilePaths();
        if (!inputs.Any()) return;
        var resultPaths = await windowAction(inputs, imgNode.VisualizationMode);
        if (resultPaths == null || !resultPaths.Any()) return;
        BaseNodeViewModel newNode = resultPaths.Count == 1
            ? await _nodeFactory.CreateSingleImageNodeAsync(resultPaths[0], imgNode.X + 350, imgNode.Y)
            : await _nodeFactory.CreateMultipleImagesNodeAsync(resultPaths, imgNode.X + 350, imgNode.Y);
        newNode.Title = $"{imgNode.Title} {titleSuffix}";
        if (newNode is ImageNodeViewModel resNode) resNode.VisualizationMode = imgNode.VisualizationMode;
        RegisterProcessingResult(newNode, imgNode, resultPaths[0], undoLabel);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowPlateSolvingWindow() 
    { if (SelectedNode is ImageNodeViewModel n) await _windowService.ShowPlateSolvingWindowAsync(n); }

    [RelayCommand(CanExecute = nameof(CanEditHeader))]
    private async Task EditSelectedNodeHeader()
    {
        if (SelectedNode is ImageNodeViewModel imgNode)
        {
            var newHeader = await _windowService.ShowHeaderEditorAsync(imgNode.CurrentFiles, imgNode.Navigator);
            if (newHeader != null && imgNode.ActiveFile != null) imgNode.ActiveFile.ModifiedHeader = newHeader;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ResetNormalization() { if (SelectedNode is ImageNodeViewModel n) await n.ResetThresholdsAsync(); }

    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private void ResetNodeView() { if (SelectedNode is ImageNodeViewModel n) n.ResetView(); }

    [RelayCommand]
    private void ResetBoard()
    {
        Viewport.ResetView();
        OnPropertyChanged(nameof(Viewport));
    }

    [RelayCommand(CanExecute = nameof(CanSetVisualizationMode))]
    private void SetVisualizationMode(VisualizationMode mode) { if (SelectedNode is ImageNodeViewModel n) n.VisualizationMode = mode; }

    [RelayCommand(CanExecute = nameof(CanToggleAnimation))]
    private void ToggleNodeAnimation()
    {
        if (SelectedNode is ImageNodeViewModel { Navigator: SequenceNavigator sn })
        {
            sn.ToggleLoop();
            OnPropertyChanged(nameof(IsGlobalAnimationRunning));
        }
    }

    [RelayCommand] public void FitView() => Viewport.ZoomToFit(Nodes);
    [RelayCommand(CanExecute = nameof(CanUndo))] private void Undo() => _undoService.Undo();
    [RelayCommand(CanExecute = nameof(CanRedo))] private void Redo() => _undoService.Redo();
    private bool CanUndo() => _undoService.CanUndo;
    private bool CanRedo() => _undoService.CanRedo;

    public void Pan(Vector delta) => Viewport.ApplyPan(delta.X, delta.Y);
    public void Zoom(double deltaY, Point mousePosition)
    {
        double factor = deltaY > 0 ? 1.1 : 0.9;
        Viewport.ApplyZoomAtPoint(factor, mousePosition);
    }

    public void SetSelectedNode(BaseNodeViewModel? node)
    {
        if (SelectedNode != null) SelectedNode.IsSelected = false;
        SelectedNode = node;
        if (SelectedNode != null) SelectedNode.IsSelected = true;
    }
    public void DeselectAllNodes() => SetSelectedNode(null);

    private bool CanExecuteOnImageNode() => SelectedNode is ImageNodeViewModel;
    private bool CanEditHeader() => SelectedNode is ImageNodeViewModel n && n.ActiveFile != null;
    private bool CanSetVisualizationMode(VisualizationMode mode) => SelectedNode is ImageNodeViewModel n && n.VisualizationMode != mode;
    private bool CanSaveVideo() => SelectedNode is ImageNodeViewModel n && n.Navigator.TotalCount > 1;
    private bool CanToggleAnimation() => SelectedNode is ImageNodeViewModel n && n.Navigator.CanMove;
    private bool CanStackImages(StackingMode mode) => SelectedNode is ImageNodeViewModel n && n.Navigator.TotalCount > 1;
    private bool CanSaveNode() => SelectedNode is ImageNodeViewModel vm && vm.ActiveFile != null;
}