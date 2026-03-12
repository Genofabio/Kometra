using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;
using System.IO; 
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Models.Fits;
using Kometra.Models.Nodes;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Arithmetic;
using Kometra.Models.Processing.Enhancement;
using Kometra.Models.Processing.Stacking;
using Kometra.Models.Visualization;
using Kometra.Services;
using Kometra.Services.Factories;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.ImportExport;
using Kometra.Services.Processing.Coordinators;
using Kometra.Services.Settings;
using Kometra.Services.UI;
using Kometra.Services.Undo;
using Kometra.ViewModels.Nodes;
using Kometra.ViewModels.Visualization;

namespace Kometra.ViewModels;

using Shared_SequenceNavigator = Shared.SequenceNavigator;

public partial class BoardViewModel : ObservableObject
{
    // --- Costanti per Layout Grafico ---
    private const double DefaultNodeMarginX = 150.0;
    private const double CascadeOffsetY = 100.0;

    // --- Dipendenze ---
    private readonly INodeViewModelFactory _nodeFactory;
    private readonly IWindowService _windowService;
    private readonly IFitsMetadataService _metadataService;
    private readonly IUndoService _undoService;
    private readonly IFitsDataManager _dataManager; 
    private readonly IStackingCoordinator _stackingCoordinator;
    private readonly IVideoExportCoordinator _videoCoordinator;
    private readonly IConfigurationService _configService;
    private readonly IArithmeticCoordinator _arithmeticCoordinator;
    private readonly INodeStructureCoordinator _nodeStructureCoordinator;

    // --- Stato Navigazione ---
    public BoardViewport Viewport { get; } = new();
    
    // --- Stato Selezione ---
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(SelectedImageNode))]
    private BaseNodeViewModel? _selectedNode;

    // Collezione per la gestione della multi-selezione
    public ObservableCollection<BaseNodeViewModel> SelectedNodes { get; } = new();

    // Proprietà per monitorare il numero di nodi selezionati
    public int SelectedNodesCount => SelectedNodes.Count;
    
    // Restituisce il nodo immagine solo se la selezione è univoca (1 solo nodo selezionato)
    private ImageNodeViewModel? SelectedImageNode => SelectedNodesCount == 1 ? SelectedNodes[0] as ImageNodeViewModel : null;

    // --- PROPRIETÀ DINAMICHE PER L'INTERFACCIA ---
    public string BoardBackgroundColor => _configService.Current.BoardBackgroundColor;
    public string PrimarySelectionColor => _configService.Current.PrimarySelectionColor;

    partial void OnSelectedNodeChanged(BaseNodeViewModel? value)
    {
        NotifySelectionCommands();
    }
    
    public bool IsGlobalAnimationRunning => 
        Nodes.OfType<ImageNodeViewModel>().Any(n => n.Navigator is Shared_SequenceNavigator { IsLooping: true });
    
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
        IVideoExportCoordinator videoCoordinator,
        IConfigurationService configService,
        IArithmeticCoordinator arithmeticCoordinator,
        INodeStructureCoordinator nodeStructureCoordinator)
    {
        _nodeFactory = nodeFactory ?? throw new ArgumentNullException(nameof(nodeFactory));
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _undoService = undoService ?? throw new ArgumentNullException(nameof(undoService));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _stackingCoordinator = stackingCoordinator ?? throw new ArgumentNullException(nameof(stackingCoordinator));
        _videoCoordinator = videoCoordinator ?? throw new ArgumentNullException(nameof(videoCoordinator));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _arithmeticCoordinator = arithmeticCoordinator ?? throw new ArgumentNullException(nameof(arithmeticCoordinator));
        _nodeStructureCoordinator = nodeStructureCoordinator ?? throw new ArgumentNullException(nameof(nodeStructureCoordinator));
        
        if (_configService is ConfigurationService cs)
        {
            cs.PropertyChanged += OnSettingsChanged;
        }

        SelectedNodes.CollectionChanged += (s, e) => NotifySelectionCommands();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IConfigurationService.Current))
        {
            OnPropertyChanged(nameof(BoardBackgroundColor));
            OnPropertyChanged(nameof(PrimarySelectionColor));
        }
    }

    // ---------------------------------------------------------------------------
    // LOGICA SELEZIONE
    // ---------------------------------------------------------------------------

    public void SetSelectedNode(BaseNodeViewModel? node) => SetSelectedNode(node, false);

    public void SetSelectedNode(BaseNodeViewModel? node, bool isModifierPressed)
    {
        if (node == null)
        {
            DeselectAllNodes();
            return;
        }

        if (!isModifierPressed)
        {
            // Click pulito: azzera tutto e seleziona solo questo
            DeselectAllNodes();
            node.IsSelected = true;
            SelectedNodes.Add(node);
            SelectedNode = node;
        }
        else
        {
            // Click con Modificatore (Multi-Selezione Illimitata)
            if (SelectedNodes.Contains(node))
            {
                // Comportamento standard OS: Ctrl+Click su un elemento selezionato lo DESELEZIONA
                node.IsSelected = false;
                node.SelectionLetter = string.Empty;
                SelectedNodes.Remove(node);
            }
            else
            {
                // Aggiunge alla selezione senza alcun limite di numero
                node.IsSelected = true;
                SelectedNodes.Add(node);
            }

            SelectedNode = SelectedNodes.Count == 1 ? SelectedNodes[0] : null;
        }
    }

    public void DeselectAllNodes()
    {
        foreach (var n in SelectedNodes)
        {
            n.IsSelected = false;
            n.SelectionLetter = string.Empty;
        }
        SelectedNodes.Clear();
        SelectedNode = null;
    }

    private void NotifySelectionCommands()
    {
        // Pulisce tutte le lettere
        foreach (var n in Nodes) n.SelectionLetter = string.Empty;

        // Assegna A e B SOLO se ci sono esattamente 2 nodi
        if (SelectedNodes.Count == 2)
        {
            SelectedNodes[0].SelectionLetter = "A";
            SelectedNodes[1].SelectionLetter = "B";
        }

        OnPropertyChanged(nameof(SelectedNodesCount));
        
        ResetNormalizationCommand.NotifyCanExecuteChanged();
        ResetNodeViewCommand.NotifyCanExecuteChanged();
        ShowAlignmentWindowCommand.NotifyCanExecuteChanged();
        StackImagesCommand.NotifyCanExecuteChanged();
        ExportSelectedNodeCommand.NotifyCanExecuteChanged(); 
        SaveVideoCommand.NotifyCanExecuteChanged();
        ToggleNodeAnimationCommand.NotifyCanExecuteChanged();
        EditSelectedNodeHeaderCommand.NotifyCanExecuteChanged();
        ShowPlateSolvingWindowCommand.NotifyCanExecuteChanged();
        SetVisualizationModeCommand.NotifyCanExecuteChanged();
        ShowPosterizationWindowCommand.NotifyCanExecuteChanged();
        ShowRadialEnhancementWindowCommand.NotifyCanExecuteChanged();
        ShowStructureExtractionWindowCommand.NotifyCanExecuteChanged();
        ShowLocalContrastWindowCommand.NotifyCanExecuteChanged();
        ShowStarMaskingWindowCommand.NotifyCanExecuteChanged();
        ShowCropWindowCommand.NotifyCanExecuteChanged();

        AddNodesCommand.NotifyCanExecuteChanged();
        SubtractNodesCommand.NotifyCanExecuteChanged();
        MultiplyNodesCommand.NotifyCanExecuteChanged();
        DivideNodesCommand.NotifyCanExecuteChanged();
        
        JoinCommand.NotifyCanExecuteChanged();
        SplitCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------------------
    // HELPER POSIZIONAMENTO GRAFO
    // ---------------------------------------------------------------------------

    private void RepositionNewNode(BaseNodeViewModel sourceNode, BaseNodeViewModel newNode, int index = 0)
    {
        double sourceHalfWidth = sourceNode.EstimatedTotalSize.Width / 2.0;
        double newHalfWidth = newNode.EstimatedTotalSize.Width / 2.0;
        
        double deltaX = sourceHalfWidth + DefaultNodeMarginX + newHalfWidth;
        
        double sourceCenterY = sourceNode.Y + (sourceNode.EstimatedTotalSize.Height / 2.0);
        double newHalfHeight = newNode.EstimatedTotalSize.Height / 2.0;
        
        double offset = index * CascadeOffsetY;

        newNode.X = sourceNode.X + deltaX;
        newNode.Y = sourceCenterY - newHalfHeight + offset;
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
                if (node is ImageNodeViewModel { Navigator: Shared_SequenceNavigator sn }) sn.Stop();
                if (SelectedNodes.Contains(node)) DeselectAllNodes();
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

    // --- GESTIONE RISULTATI ELABORAZIONE (COLLEGAMENTI) ---
    
    private void RegisterProcessingResult(BaseNodeViewModel newNode, BaseNodeViewModel sourceNode, string tempFilePath, string undoLabel)
    {
        RegisterProcessingResult(newNode, new List<BaseNodeViewModel> { sourceNode }, tempFilePath, undoLabel);
    }

    private void RegisterProcessingResult(BaseNodeViewModel newNode, IEnumerable<BaseNodeViewModel> sourceNodes, string tempFilePath, string undoLabel)
    {
        var sources = sourceNodes.ToList();
        var action = new DelegateAction(undoLabel,
            execute: () => {
                if (!Nodes.Contains(newNode)) 
                { 
                    Nodes.Add(newNode); 
                    RegisterNodeEvents(newNode); 
                    newNode.ZIndex = ++_maxZIndex; 
                    
                    foreach (var source in sources)
                    {
                        CreateConnection(source, newNode);
                    }
                }
                SetSelectedNode(newNode);
            },
            undo: () => {
                if (Nodes.Contains(newNode)) {
                    var links = Connections.Where(c => c.Target == newNode).ToList();
                    foreach (var link in links) Connections.Remove(link);
                    
                    if (SelectedNodes.Contains(newNode)) DeselectAllNodes();
                    UnregisterNodeEvents(newNode); 
                    Nodes.Remove(newNode);
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
    // TOOL DI ELABORAZIONE (NODO SINGOLO)
    // ---------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowCropWindow()
    {
        await RunGenericProcessing(async (files, mode) => 
        {
            var paths = await _windowService.ShowCropToolWindowAsync(files, mode);
            return paths != null ? (paths, "(Cropped)") : null;
        }, "Ritaglio Immagine");
    }

    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowRadialEnhancementWindow()
    {
        await RunGenericProcessing(async (files, mode) => 
        {
            var result = await _windowService.ShowRadialEnhancementWindowAsync(files, mode);
            if (result == null) return null;
            return (result.Value.Paths, GetEnhancementSuffix(result.Value.Mode));
        }, "Miglioramento Radiale");
    }

    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowStructureExtractionWindow()
    {
        await RunGenericProcessing(async (files, mode) => 
        {
            var result = await _windowService.ShowStructureExtractionWindowAsync(files, mode);
            if (result == null) return null;
            return (result.Value.Paths, GetEnhancementSuffix(result.Value.Mode));
        }, "Estrazione Strutture");
    }

    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowLocalContrastWindow()
    {
        await RunGenericProcessing(async (files, mode) => 
        {
            var result = await _windowService.ShowLocalContrastWindowAsync(files, mode);
            if (result == null) return null;
            return (result.Value.Paths, GetEnhancementSuffix(result.Value.Mode));
        }, "Contrasto Locale");
    }

    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowPosterizationWindow()
    {
        await RunGenericProcessing(async (files, mode) => 
        {
            var paths = await _windowService.ShowPosterizationWindowAsync(files, mode);
            return paths != null ? (paths, "(Posterizzata)") : null;
        }, "Posterizzazione");
    }
    
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowAlignmentWindow()
    {
        await RunGenericProcessing(async (files, mode) => 
        {
            var paths = await _windowService.ShowAlignmentWindowAsync(files, mode);
            return paths != null ? (paths, "(Allineata)") : null;
        }, "Allineamento");
    }
    
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ShowStarMaskingWindow()
    {
        await RunGenericProcessing(async (files, mode) => 
        {
            var paths = await _windowService.ShowStarMaskingWindowAsync(files);
            return (paths != null && paths.Any()) ? (paths, "(Starless)") : null;
        }, "Rimozione Stelle");
    }
    
    private async Task RunGenericProcessing(
        Func<List<FitsFileReference>, VisualizationMode, Task<(List<string> Paths, string Suffix)?>> windowAction, 
        string undoLabel)
    {
        var imgNode = SelectedImageNode;
        if (imgNode == null) return;
        var inputFiles = imgNode.CurrentFiles.ToList();
        if (!inputFiles.Any()) return;
        try 
        {
            var result = await windowAction(inputFiles, imgNode.VisualizationMode);
            if (result == null || result.Value.Paths == null || !result.Value.Paths.Any()) return;

            var (resultPaths, titleSuffix) = result.Value;
            
            GC.Collect(); GC.WaitForPendingFinalizers();

            BaseNodeViewModel newNode = resultPaths.Count == 1
                ? await _nodeFactory.CreateSingleImageNodeAsync(resultPaths[0], 0, 0)
                : await _nodeFactory.CreateMultipleImagesNodeAsync(resultPaths, 0, 0);

            RepositionNewNode(imgNode, newNode);

            newNode.Title = $"{imgNode.Title} {titleSuffix}";
            if (newNode is ImageNodeViewModel resNode) resNode.VisualizationMode = imgNode.VisualizationMode;
            
            RegisterProcessingResult(newNode, imgNode, resultPaths[0], undoLabel);
        }
        catch (Exception ex) { Debug.WriteLine($"Errore: {ex.Message}"); }
    }

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

    // ---------------------------------------------------------------------------
    // OPERAZIONI MATEMATICHE (DOPPIO NODO A/B)
    // ---------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanExecuteMath))]
    private async Task AddNodes() => await RunArithmetic(ArithmeticOperation.Add, "Somma Immagini");

    [RelayCommand(CanExecute = nameof(CanExecuteMath))]
    private async Task SubtractNodes() => await RunArithmetic(ArithmeticOperation.Subtract, "Sottrazione Immagini");

    [RelayCommand(CanExecute = nameof(CanExecuteMath))]
    private async Task MultiplyNodes() => await RunArithmetic(ArithmeticOperation.Multiply, "Moltiplicazione Immagini");

    [RelayCommand(CanExecute = nameof(CanExecuteMath))]
    private async Task DivideNodes() => await RunArithmetic(ArithmeticOperation.Divide, "Divisione Immagini");

    private async Task RunArithmetic(ArithmeticOperation op, string undoLabel)
    {
        var nodeA = SelectedNodes[0] as ImageNodeViewModel;
        var nodeB = SelectedNodes[1] as ImageNodeViewModel;
        if (nodeA == null || nodeB == null) return;

        try
        {
            var resultPaths = await _arithmeticCoordinator.ProcessAsync(
                nodeA.CurrentFiles.ToList(), 
                nodeB.CurrentFiles.ToList(), 
                op);

            if (resultPaths == null || !resultPaths.Any()) return;

            BaseNodeViewModel newNode = resultPaths.Count == 1
                ? await _nodeFactory.CreateSingleImageNodeAsync(resultPaths[0], 0, 0)
                : await _nodeFactory.CreateMultipleImagesNodeAsync(resultPaths, 0, 0);

            RepositionNewNode(nodeA, newNode);

            string opSymbol = op switch { 
                ArithmeticOperation.Add => "+", 
                ArithmeticOperation.Subtract => "-", 
                ArithmeticOperation.Multiply => "*", 
                ArithmeticOperation.Divide => "/", 
                _ => "?" 
            };

            newNode.Title = $"{nodeA.Title} {opSymbol} {nodeB.Title}";
            if (newNode is ImageNodeViewModel resNode) resNode.VisualizationMode = nodeA.VisualizationMode;

            RegisterProcessingResult(newNode, new List<BaseNodeViewModel> { nodeA, nodeB }, resultPaths[0], undoLabel);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore aritmetico: {ex.Message}");
        }
    }
    
    // ===========================================================================
    // OPERAZIONI DI STRUTTURA NODI (JOIN / SPLIT)
    // ===========================================================================

    [RelayCommand(CanExecute = nameof(CanJoin))]
    private async Task Join()
    {
        var allFiles = SelectedNodes.OfType<ImageNodeViewModel>()
                                    .SelectMany(n => n.CurrentFiles)
                                    .ToList();

        if (!allFiles.Any()) return;

        try 
        {
            var paths = await _nodeStructureCoordinator.JoinNodesAsync(allFiles);
        
            var rightmostNode = SelectedNodes.OrderByDescending(n => n.X).First();
            
            var newNode = await _nodeFactory.CreateMultipleImagesNodeAsync(paths, 0, 0);
            
            RepositionNewNode(rightmostNode, newNode);
            
            newNode.Title = "Joined Sequence";
            
            if (SelectedNodes[0] is ImageNodeViewModel firstNode && newNode is ImageNodeViewModel resNode)
                resNode.VisualizationMode = firstNode.VisualizationMode;

            RegisterProcessingResult(newNode, SelectedNodes.ToList(), paths[0], "Unione Nodi");
        } 
        catch (Exception ex) { Debug.WriteLine($"Errore Join: {ex.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(CanSplit))]
    private async Task Split()
    {
        var node = SelectedImageNode;
        if (node == null || node.Navigator.TotalCount <= 1) return;

        try 
        {
            var paths = _nodeStructureCoordinator.SplitNode(node.CurrentFiles.ToList());
            
            for (int i = 0; i < paths.Count; i++)
            {
                var newNode = await _nodeFactory.CreateSingleImageNodeAsync(paths[i], 0, 0);
                
                RepositionNewNode(node, newNode, i);

                newNode.Title = System.IO.Path.GetFileNameWithoutExtension(paths[i]);
                
                if (newNode is ImageNodeViewModel resNode)
                    resNode.VisualizationMode = node.VisualizationMode;

                RegisterProcessingResult(newNode, node, paths[i], "Divisione Sequenza");
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Errore Split: {ex.Message}"); }
    }

    // ---------------------------------------------------------------------------
    // COMANDI BOARD / IMPORT / ALTRE OPERAZIONI
    // ---------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddNodeAsync()
    {
        var result = await _windowService.ShowImportWindowAsync();
        if (result == null || result.Value.Paths == null || !result.Value.Paths.Any()) return;

        var (paths, separateNodes) = result.Value;
        var tasks = paths.Select(async path => { 
            var header = await _dataManager.GetHeaderOnlyAsync(path); 
            var date = _metadataService.GetObservationDate(header) ?? DateTime.MinValue; 
            return (Path: path, Date: date); 
        });
        var results = await Task.WhenAll(tasks);
        var sortedPaths = results.OrderBy(x => x.Date).Select(x => x.Path).ToList();

        Point centerScreen = new Point(Viewport.ViewportSize.Width / 2.0, Viewport.ViewportSize.Height / 2.0);
        Point pos = Viewport.ToWorldCoordinates(centerScreen);

        if (separateNodes)
        {
            double offsetX = 0, offsetY = 0;
            foreach (var path in sortedPaths)
            {
                var newNode = await _nodeFactory.CreateSingleImageNodeAsync(path, pos.X + offsetX, pos.Y + offsetY);
                AddNodeToGraph(newNode, "Importa Immagine Singola");
                offsetX += 30; offsetY += 30;
            }
        }
        else
        {
            BaseNodeViewModel newNode = sortedPaths.Count == 1 
                ? await _nodeFactory.CreateSingleImageNodeAsync(sortedPaths[0], pos.X, pos.Y)
                : await _nodeFactory.CreateMultipleImagesNodeAsync(sortedPaths, pos.X, pos.Y);
            AddNodeToGraph(newNode, sortedPaths.Count == 1 ? "Aggiungi Immagine" : "Aggiungi Sequenza");
        }
    }

    [RelayCommand] private void PanBoard(string direction) { double s = 100; switch(direction.ToLower()){ case "left": Viewport.ApplyPan(s,0); break; case "right": Viewport.ApplyPan(-s,0); break; case "up": Viewport.ApplyPan(0,s); break; case "down": Viewport.ApplyPan(0,-s); break; } }
    [RelayCommand] private void ZoomBoard(string direction) { double f = direction.ToLower()=="in"?1.2:0.8; Viewport.ApplyZoomAtPoint(f, new Point(Viewport.ViewportSize.Width/2, Viewport.ViewportSize.Height/2)); }
    
    [RelayCommand(CanExecute = nameof(CanStackImages))] 
    private async Task StackImages(StackingMode mode) 
    { 
        var s = SelectedImageNode;
        if(s == null) return; 
        try { 
            var r = await _stackingCoordinator.ExecuteStackingAsync(s.CurrentFiles, mode); 
            
            var n = await _nodeFactory.CreateSingleImageNodeAsync(r.FilePath, 0, 0); 
            
            RepositionNewNode(s, n);

            n.Title=$"{s.Title} ({mode})"; 
            if(n is ImageNodeViewModel res) res.VisualizationMode=s.VisualizationMode; 
            RegisterProcessingResult(n, s, r.FilePath, "Stacking"); 
        } catch(Exception ex) { Debug.WriteLine($"Stacking failed: {ex.Message}"); } 
    }
    
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))]
    private async Task ExportSelectedNode()
    {
        var n = SelectedImageNode;
        if (n == null) return;
        var filePaths = n.CurrentFiles.Select(f => f.FilePath).ToList();
        await _windowService.ShowExportWindowAsync(filePaths);
    }

    [RelayCommand(CanExecute = nameof(CanSaveVideo))]
    private async Task SaveVideo()
    {
        var node = SelectedImageNode;
        if (node?.ActiveRenderer == null) return;
        var settings = await _windowService.ShowVideoExportDialogAsync(node, node.VisualizationMode);
        if (settings == null) return;
        try { await _videoCoordinator.ExportVideoAsync(node.CurrentFiles, settings, settings.InitialProfile); }
        catch (Exception ex) { Debug.WriteLine($"Export fallito: {ex.Message}"); }
    }
    
    [RelayCommand] private async Task ShowSettingsWindow() => await _windowService.ShowSettingsWindowAsync();
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))] private async Task ShowPlateSolvingWindow() { if(SelectedImageNode is ImageNodeViewModel n) await _windowService.ShowPlateSolvingWindowAsync(n); }
    [RelayCommand(CanExecute = nameof(CanEditHeader))] private async Task EditSelectedNodeHeader() { if(SelectedImageNode is ImageNodeViewModel n) await _windowService.ShowHeaderEditorAsync(n.CurrentFiles, n.Navigator); }
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))] private async Task ResetNormalization() { if(SelectedImageNode is ImageNodeViewModel n) await n.ResetThresholdsAsync(); }
    [RelayCommand(CanExecute = nameof(CanExecuteOnImageNode))] private void ResetNodeView() { if(SelectedImageNode is ImageNodeViewModel n) n.ResetView(); }
    [RelayCommand] private void ResetBoard() { Viewport.ResetView(); OnPropertyChanged(nameof(Viewport)); }
    [RelayCommand(CanExecute = nameof(CanSetVisualizationMode))] private void SetVisualizationMode(VisualizationMode mode) { if(SelectedImageNode is ImageNodeViewModel n) n.VisualizationMode = mode; }
    
    [RelayCommand(CanExecute = nameof(CanToggleAnimation))] 
    private void ToggleNodeAnimation() 
    { 
        if(SelectedImageNode is ImageNodeViewModel { Navigator: Shared_SequenceNavigator sn }) 
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
    public void Zoom(double deltaY, Point mousePosition) { double factor = deltaY > 0 ? 1.1 : (1.0 / 1.1); Viewport.ApplyZoomAtPoint(factor, mousePosition); }
    public void Pan(double deltaX, double deltaY) => Viewport.ApplyPan(deltaX, deltaY);

    // --- PREDICATI DI ESECUZIONE ---

    private bool CanExecuteMath()
    {
        if (SelectedNodesCount != 2) return false;
        var nodeA = SelectedNodes[0] as ImageNodeViewModel;
        var nodeB = SelectedNodes[1] as ImageNodeViewModel;
        if (nodeA == null || nodeB == null) return false;
        return _arithmeticCoordinator.CanProcess(nodeA.CurrentFiles.ToList(), nodeB.CurrentFiles.ToList());
    }
    
    private bool CanJoin() => SelectedNodesCount >= 2;
    private bool CanSplit() => SelectedNodesCount == 1 && SelectedImageNode?.Navigator.TotalCount > 1;

    private bool CanExecuteOnImageNode() => SelectedNodesCount == 1 && SelectedNodes[0] is ImageNodeViewModel;
    private bool CanEditHeader() => SelectedNodesCount == 1 && SelectedNodes[0] is ImageNodeViewModel n && n.ActiveFile != null;
    private bool CanSetVisualizationMode(VisualizationMode mode) => SelectedNodesCount == 1 && SelectedNodes[0] is ImageNodeViewModel n && n.VisualizationMode != mode;
    private bool CanSaveVideo() => SelectedNodesCount == 1 && SelectedNodes[0] is ImageNodeViewModel n && n.Navigator.TotalCount > 1;
    private bool CanToggleAnimation() => SelectedNodesCount == 1 && SelectedNodes[0] is ImageNodeViewModel n && n.Navigator.CanMove;
    private bool CanStackImages(StackingMode mode) => SelectedNodesCount == 1 && SelectedNodes[0] is ImageNodeViewModel n && n.Navigator.TotalCount > 1;
}