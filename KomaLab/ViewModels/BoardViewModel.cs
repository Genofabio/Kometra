using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Nodes;
using KomaLab.Models.Processing;
using KomaLab.Models.Processing.Enhancement;
using KomaLab.Models.Visualization;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.Services.UI;
using KomaLab.Services.Undo;
using KomaLab.ViewModels.Nodes;
using KomaLab.ViewModels.Visualization;
using SequenceNavigator = KomaLab.ViewModels.Shared.SequenceNavigator;

namespace KomaLab.ViewModels;

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

    // --- Stato Navigazione ---
    public BoardViewport Viewport { get; } = new();
    
    // --- Stato Selezione ---
    [ObservableProperty] private BaseNodeViewModel? _selectedNode;
    
    private ImageNodeViewModel? SelectedImageNode => SelectedNode as ImageNodeViewModel;

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
        ShowRadialEnhancementWindowCommand.NotifyCanExecuteChanged();
        ShowStructureExtractionWindowCommand.NotifyCanExecuteChanged();
        ShowLocalContrastWindowCommand.NotifyCanExecuteChanged();
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

    // [GRAFO: Add/Remove/Register]
    private void AddNodeToGraph(BaseNodeViewModel node, string undoLabel)
    {
        var action = new DelegateAction(undoLabel,
            execute: () => { if (!Nodes.Contains(node)) { Nodes.Add(node); RegisterNodeEvents(node); node.ZIndex = ++_maxZIndex; } SetSelectedNode(node); },
            undo: () => { if (Nodes.Contains(node)) { UnregisterNodeEvents(node); Nodes.Remove(node); DeselectAllNodes(); } }
        );
        action.Execute();
        _undoService.RecordAction(action);
    }

    private void RemoveNodeFromGraph(BaseNodeViewModel node)
    {
        var connectionsToRemove = Connections.Where(c => c.Source == node || c.Target == node).ToList();
        var action = new DelegateAction("Rimuovi Nodo",
            execute: () => {
                if (node is ImageNodeViewModel { Navigator: SequenceNavigator sn }) sn.Stop();
                if (SelectedNode == node) DeselectAllNodes();
                UnregisterNodeEvents(node);
                foreach (var conn in connectionsToRemove) Connections.Remove(conn);
                Nodes.Remove(node);
                OnPropertyChanged(nameof(IsGlobalAnimationRunning));
            },
            undo: () => {
                if (!Nodes.Contains(node)) { Nodes.Add(node); RegisterNodeEvents(node); foreach (var conn in connectionsToRemove) Connections.Add(conn); SetSelectedNode(node); OnPropertyChanged(nameof(IsGlobalAnimationRunning)); }
            }
        );
        action.Execute();
        _undoService.RecordAction(action);
    }

    private void RegisterProcessingResult(BaseNodeViewModel newNode, BaseNodeViewModel sourceNode, string tempFilePath, string undoLabel)
    {
        var action = new DelegateAction(undoLabel,
            execute: () => {
                if (!Nodes.Contains(newNode)) { Nodes.Add(newNode); RegisterNodeEvents(newNode); newNode.ZIndex = ++_maxZIndex; CreateConnection(sourceNode, newNode); }
                SetSelectedNode(newNode);
            },
            undo: () => {
                if (Nodes.Contains(newNode)) {
                    var link = Connections.FirstOrDefault(c => c.Source == sourceNode && c.Target == newNode);
                    if (link != null) Connections.Remove(link);
                    if (SelectedNode == newNode) DeselectAllNodes();
                    UnregisterNodeEvents(newNode); Nodes.Remove(newNode);
                }
            },
            onDispose: (wasExecuted) => { if (!wasExecuted && !string.IsNullOrEmpty(tempFilePath)) _dataManager.DeleteTemporaryData(tempFilePath); }
        );
        action.Execute();
        _undoService.RecordAction(action);
    }

    private void RegisterNodeEvents(BaseNodeViewModel node) { node.RequestRemove += OnNodeRequestRemove; node.RequestBringToFront += n => n.ZIndex = ++_maxZIndex; }
    private void UnregisterNodeEvents(BaseNodeViewModel node) => node.RequestRemove -= OnNodeRequestRemove;
    private void OnNodeRequestRemove(BaseNodeViewModel node) => RemoveNodeFromGraph(node);
    public void CreateConnection(BaseNodeViewModel source, BaseNodeViewModel target) {
        var connection = new ConnectionViewModel(new ConnectionModel { SourceNodeId = source.Id, TargetNodeId = target.Id }, source, target);
        Connections.Add(connection);
    }

    // ---------------------------------------------------------------------------
    // TOOL DI ELABORAZIONE
    // ---------------------------------------------------------------------------

    // 1. MODELLI RADIALI
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowRadialEnhancementWindow()
    {
        await RunGenericProcessing(async (files, mode) => 
        {
            var result = await _windowService.ShowRadialEnhancementWindowAsync(files, mode);
            if (result == null) return null; // Annullato dall'utente
            
            // Estraiamo il path e calcoliamo il suffisso dal Mode
            string suffix = GetEnhancementSuffix(result.Value.Mode);
            return (result.Value.Paths, suffix);
        }, "Miglioramento Radiale");
    }

    // 2. ESTRAZIONE STRUTTURE
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowStructureExtractionWindow()
    {
        await RunGenericProcessing(async (files, mode) => 
        {
            var result = await _windowService.ShowStructureExtractionWindowAsync(files, mode);
            if (result == null) return null;

            string suffix = GetEnhancementSuffix(result.Value.Mode);
            return (result.Value.Paths, suffix);
        }, "Estrazione Strutture");
    }

    // 3. CONTRASTO LOCALE
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowLocalContrastWindow()
    {
        await RunGenericProcessing(async (files, mode) => 
        {
            var result = await _windowService.ShowLocalContrastWindowAsync(files, mode);
            if (result == null) return null;

            string suffix = GetEnhancementSuffix(result.Value.Mode);
            return (result.Value.Paths, suffix);
        }, "Contrasto Locale");
    }

    // 4. POSTERIZZAZIONE
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowPosterizationWindow()
    {
        await RunGenericProcessing(async (files, mode) => 
        {
            var paths = await _windowService.ShowPosterizationWindowAsync(files, mode);
            return paths != null ? (paths, "(Posterizzata)") : null;
        }, "Posterizzazione");
    }
    
    // 5. ALLINEAMENTO
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowAlignmentWindow()
    {
        await RunGenericProcessing(async (files, mode) => 
        {
            var paths = await _windowService.ShowAlignmentWindowAsync(files, mode);
            return paths != null ? (paths, "(Allineata)") : null;
        }, "Allineamento");
    }
    
    // --- HELPER GENERICO (DRY) ---
    private async Task RunGenericProcessing(
        Func<List<FitsFileReference>, VisualizationMode, Task<(List<string> Paths, string Suffix)?>> windowAction, 
        string undoLabel)
    {
        if (SelectedNode is not ImageNodeViewModel imgNode) return;
        
        var inputFiles = imgNode.CurrentFiles.ToList();
        if (!inputFiles.Any()) return;
    
        var result = await windowAction(inputFiles, imgNode.VisualizationMode);
        
        if (result == null || !result.Value.Paths.Any()) return;

        var (resultPaths, titleSuffix) = result.Value;

        double centerX = imgNode.X + (1.5 * imgNode.EstimatedTotalSize.Width);
        double centerY = imgNode.Y + (imgNode.EstimatedTotalSize.Height / 2.0);

        BaseNodeViewModel newNode = resultPaths.Count == 1
            ? await _nodeFactory.CreateSingleImageNodeAsync(resultPaths[0], centerX + 400, centerY)
            : await _nodeFactory.CreateMultipleImagesNodeAsync(resultPaths, centerX + 400, centerY);

        newNode.Title = $"{imgNode.Title} {titleSuffix}";
        
        if (newNode is ImageNodeViewModel resNode) resNode.VisualizationMode = imgNode.VisualizationMode;
        
        RegisterProcessingResult(newNode, imgNode, resultPaths[0], undoLabel);
    }

    // --- HELPER NOMI FILTRI ---
    private string GetEnhancementSuffix(ImageEnhancementMode mode)
    {
        return mode switch
        {
            ImageEnhancementMode.LarsonSekaninaStandard => "(Larson-Sekanina)",
            ImageEnhancementMode.LarsonSekaninaSymmetric => "(Larson-Sek. Simm.)",
            ImageEnhancementMode.AdaptiveLaplacianRVSF => "(RVSF Adattivo)",
            ImageEnhancementMode.AdaptiveLaplacianMosaic => "(RVSF Mosaico)",
            ImageEnhancementMode.InverseRho => "(1/Rho)",
            ImageEnhancementMode.AzimuthalAverage => "(Media Azimutale)",
            ImageEnhancementMode.AzimuthalMedian => "(Mediana Azimutale)",
            ImageEnhancementMode.AzimuthalRenormalization => "(Rinormalizzazione)",
            ImageEnhancementMode.FrangiVesselnessFilter => "(Frangi Vesselness)",
            ImageEnhancementMode.StructureTensorCoherence => "(Tensore Struttura)",
            ImageEnhancementMode.WhiteTopHatExtraction => "(Top-Hat)",
            ImageEnhancementMode.UnsharpMaskingMedian => "(Unsharp Masking)",
            ImageEnhancementMode.ClaheLocalContrast => "(CLAHE)",
            ImageEnhancementMode.AdaptiveLocalNormalization => "(LSN)",
            _ => "(Filtrata)"
        };
    }

    // [ALTRI COMANDI]
    [RelayCommand]
    private async Task AddNodeAsync()
    {
        var resultPaths = await _windowService.ShowImportWindowAsync();
        if (resultPaths == null || !resultPaths.Any()) return;
        var tasks = resultPaths.Select(async path => { var header = await _dataManager.GetHeaderOnlyAsync(path); var date = _metadataService.GetObservationDate(header) ?? DateTime.MinValue; return (Path: path, Date: date); });
        var results = await Task.WhenAll(tasks);
        var sortedPaths = results.OrderBy(x => x.Date).Select(x => x.Path).ToList();
        Point centerScreen = new Point(Viewport.ViewportSize.Width / 2.0, Viewport.ViewportSize.Height / 2.0);
        Point pos = Viewport.ToWorldCoordinates(centerScreen);
        BaseNodeViewModel newNode = sortedPaths.Count == 1 ? await _nodeFactory.CreateSingleImageNodeAsync(sortedPaths[0], pos.X, pos.Y) : await _nodeFactory.CreateMultipleImagesNodeAsync(sortedPaths, pos.X, pos.Y);
        bool isCalibrated = sortedPaths.Any(p => p.Contains("Calibrated", StringComparison.OrdinalIgnoreCase));
        if (isCalibrated) newNode.Title += " (Calibrata)";
        AddNodeToGraph(newNode, sortedPaths.Count == 1 ? "Aggiungi Immagine" : "Aggiungi Sequenza");
    }

    [RelayCommand] private void PanBoard(string direction) { double s = 100; switch(direction.ToLower()){ case "left": Viewport.ApplyPan(s,0); break; case "right": Viewport.ApplyPan(-s,0); break; case "up": Viewport.ApplyPan(0,s); break; case "down": Viewport.ApplyPan(0,-s); break; } }
    [RelayCommand] private void ZoomBoard(string direction) { double f = direction.ToLower()=="in"?1.2:0.8; Viewport.ApplyZoomAtPoint(f, new Point(Viewport.ViewportSize.Width/2, Viewport.ViewportSize.Height/2)); }
    [RelayCommand(CanExecute = nameof(CanStackImages))] private async Task StackImages(StackingMode mode) { if(SelectedNode is not ImageNodeViewModel s) return; try { var r = await _stackingCoordinator.ExecuteStackingAsync(s.CurrentFiles, mode); double x=s.X+1.5*s.EstimatedTotalSize.Width; double y=s.Y+s.EstimatedTotalSize.Height/2.0; var n = await _nodeFactory.CreateSingleImageNodeAsync(r.FilePath, x+400, y); n.Title=$"{s.Title} ({mode})"; if(n is ImageNodeViewModel res) res.VisualizationMode=s.VisualizationMode; RegisterProcessingResult(n, s, r.FilePath, "Stacking"); } catch(Exception ex) { Debug.WriteLine($"Stacking failed: {ex.Message}"); } }
    
    [RelayCommand(CanExecute = nameof(CanSaveNode))] 
    private async Task SaveSelectedNode() 
    { 
        if(SelectedNode is not ImageNodeViewModel n || n.ActiveFile == null) return; 
        
        var p = await _dialogService.ShowSaveFitsFileDialogAsync(n.ActiveFile.FileName); 
        if(string.IsNullOrWhiteSpace(p)) return; 
        
        try 
        { 
            var fr = n.ActiveFile;
            var data = await _dataManager.GetDataAsync(fr.FilePath);
            
            // [MODIFICA MEF] Accesso sicuro all'immagine da salvare
            var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
            if (imageHdu == null) return; // Niente da salvare
            
            // Salviamo i pixel dell'HDU e l'header (con priorità alla RAM)
            await _dataManager.SaveDataAsync(p, imageHdu.PixelData, fr.ModifiedHeader ?? imageHdu.Header); 
        } 
        catch(Exception ex) 
        { 
            Debug.WriteLine($"Save failed: {ex.Message}"); 
        } 
    }
    
    [RelayCommand(CanExecute = nameof(CanSaveVideo))]
    private async Task SaveVideo()
    {
        var node = SelectedImageNode;
        if (node?.ActiveRenderer == null) return;

        var settings = await _windowService.ShowVideoExportDialogAsync(node, node.VisualizationMode);
        if (settings == null) return;

        try 
        {
            await _videoCoordinator.ExportVideoAsync(
                node.CurrentFiles, 
                settings, 
                settings.InitialProfile);
            
            System.Diagnostics.Debug.WriteLine("Esportazione completata con successo.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Export fallito: {ex.Message}");
        }
    }
    
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))] private async Task ShowPlateSolvingWindow() { if(SelectedNode is ImageNodeViewModel n) await _windowService.ShowPlateSolvingWindowAsync(n); }
    [RelayCommand(CanExecute = nameof(CanEditHeader))] private async Task EditSelectedNodeHeader() { if(SelectedNode is ImageNodeViewModel n) await _windowService.ShowHeaderEditorAsync(n.CurrentFiles, n.Navigator); }
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))] private async Task ResetNormalization() { if(SelectedNode is ImageNodeViewModel n) await n.ResetThresholdsAsync(); }
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))] private void ResetNodeView() { if(SelectedNode is ImageNodeViewModel n) n.ResetView(); }
    [RelayCommand] private void ResetBoard() { Viewport.ResetView(); OnPropertyChanged(nameof(Viewport)); }
    [RelayCommand(CanExecute = nameof(CanSetVisualizationMode))] private void SetVisualizationMode(VisualizationMode mode) { if(SelectedNode is ImageNodeViewModel n) n.VisualizationMode = mode; }
    [RelayCommand(CanExecute = nameof(CanToggleAnimation))] private void ToggleNodeAnimation() { if(SelectedNode is ImageNodeViewModel { Navigator: SequenceNavigator sn }) { sn.ToggleLoop(); OnPropertyChanged(nameof(IsGlobalAnimationRunning)); } }
    [RelayCommand] public void FitView() => Viewport.ZoomToFit(Nodes);
    [RelayCommand(CanExecute = nameof(CanUndo))] private void Undo() => _undoService.Undo();
    [RelayCommand(CanExecute = nameof(CanRedo))] private void Redo() => _undoService.Redo();
    private bool CanUndo() => _undoService.CanUndo;
    private bool CanRedo() => _undoService.CanRedo;
    public void Pan(Vector delta) => Viewport.ApplyPan(delta.X, delta.Y);
    public void Zoom(double deltaY, Point mousePosition) { double factor = deltaY > 0 ? 1.1 : 0.9; Viewport.ApplyZoomAtPoint(factor, mousePosition); }
    public void SetSelectedNode(BaseNodeViewModel? node) { if(SelectedNode != null) SelectedNode.IsSelected = false; SelectedNode = node; if(SelectedNode != null) SelectedNode.IsSelected = true; }
    public void DeselectAllNodes() => SetSelectedNode(null);
    private bool CanExecuteOnImageNode() => SelectedNode is ImageNodeViewModel;
    private bool CanEditHeader() => SelectedNode is ImageNodeViewModel n && n.ActiveFile != null;
    private bool CanSetVisualizationMode(VisualizationMode mode) => SelectedNode is ImageNodeViewModel n && n.VisualizationMode != mode;
    private bool CanSaveVideo() => SelectedImageNode?.Navigator.TotalCount > 1;
    private bool CanToggleAnimation() => SelectedNode is ImageNodeViewModel n && n.Navigator.CanMove;
    private bool CanStackImages(StackingMode mode) => SelectedNode is ImageNodeViewModel n && n.Navigator.TotalCount > 1;
    private bool CanSaveNode() => SelectedNode is ImageNodeViewModel vm && vm.ActiveFile != null;
}