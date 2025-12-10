using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services.Data;
using KomaLab.Services.Factories;
using KomaLab.Services.Imaging;
using KomaLab.Services.UI;
using KomaLab.Services.Undo;

namespace KomaLab.ViewModels;

public partial class BoardViewModel : ObservableObject
{
    // --- Dipendenze ---
    private readonly INodeViewModelFactory _nodeFactory;
    private readonly IDialogService _dialogService;
    private readonly IWindowService _windowService;
    private readonly IFitsService _fitsService;
    private readonly IImageOperationService _opsService;
    private readonly IUndoService _undoService;

    // --- Proprietà ---
    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private double _scale = 0.46;
    [ObservableProperty] private Rect _viewBounds;
    [ObservableProperty] private BaseNodeViewModel? _selectedNode;
    
    public ObservableCollection<BaseNodeViewModel> Nodes { get; } = new();
    
    // Collezione per i collegamenti (Link) tra i nodi
    public ObservableCollection<ConnectionViewModel> Connections { get; } = new();
    
    private int _maxZIndex;

    // --- Costruttore ---
    public BoardViewModel(
        INodeViewModelFactory nodeFactory,
        IDialogService dialogService,
        IWindowService windowService,
        IFitsService fitsService,
        IImageOperationService opsService,
        IUndoService undoService)
    {
        _nodeFactory = nodeFactory;
        _dialogService = dialogService;
        _windowService = windowService;
        _fitsService = fitsService;
        _opsService = opsService;
        _undoService = undoService;
    }

    // --- Comandi ---
    [RelayCommand]
    private async Task AddNode()
    {
        var imagePaths = await _dialogService.ShowOpenFitsFileDialogAsync();

        if (imagePaths == null) return;
        var pathList = imagePaths.ToList();
        if (pathList.Count == 0) return;

        Point center = GetCenterOfView();

        if (pathList.Count == 1)
        {
            await AddSingleNodeAsync(pathList[0], center.X, center.Y, centerOnPosition: true);
        }
        else
        {
            await AddMultipleNodesAsync(pathList, center.X, center.Y);
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
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
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
                if (SelectedNode == node) DeselectAllNodes();
                UnregisterNodeEvents(node);
                foreach (var conn in connectionsToRemove)
                {
                    Connections.Remove(conn);
                }
                Nodes.Remove(node);
            },
            undo: () =>
            {
                if (!Nodes.Contains(node))
                {
                    Nodes.Add(node);
                    RegisterNodeEvents(node);
                    foreach (var conn in connectionsToRemove)
                    {
                        if (!Connections.Contains(conn))
                        {
                            Connections.Add(conn);
                        }
                    }
                    SetSelectedNode(node);
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
            var newNode = await _nodeFactory.CreateSingleImageNodeAsync(imagePath, x, y, centerOnPosition);
            RegisterNodeWithUndo(newNode, "Aggiungi Immagine");
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
            var newNode = await _nodeFactory.CreateMultipleImagesNodeAsync(imagePaths, x, y, centerOnPosition: true);
            RegisterNodeWithUndo(newNode, "Aggiungi Multi-Immagine");
        }
        catch(Exception ex)
        {
            Debug.WriteLine($"Error adding multiple node: {ex.Message}");
        }
    }
    
    public void CreateConnection(BaseNodeViewModel source, BaseNodeViewModel target)
    {
        var connection = new ConnectionViewModel(source, target);
        Connections.Add(connection);
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
    
    [RelayCommand(CanExecute = nameof(CanResetNodeView))]
    private void ResetNodeView()
    {
        if (SelectedNode is ImageNodeViewModel imgNode)
        {
            imgNode.ResetView();
        }
    }
    private bool CanResetNodeView() => SelectedNode is ImageNodeViewModel;

    // --- LOGICA ALLINEAMENTO ---
    [RelayCommand(CanExecute = nameof(CanShowAlignmentWindow))]
    private async Task ShowAlignmentWindow()
    {
        if (SelectedNode is not ImageNodeViewModel imgNode) return;

        try
        {
            var inputPaths = await imgNode.PrepareInputPathsAsync(_fitsService);
            if (inputPaths.Count == 0) return;

            var newPaths = await _windowService.ShowAlignmentWindowAsync(inputPaths);

            if (newPaths != null && newPaths.Count > 0)
            {
                double gap = 300;
                double newX = imgNode.X + imgNode.EstimatedTotalSize.Width + gap;
                double newY = imgNode.Y;

                string newTitle = $"{imgNode.Title} (Allineata)";
                string? dirPath = System.IO.Path.GetDirectoryName(newPaths[0]);
                bool isTemp = dirPath != null && dirPath.Contains("Komalab", StringComparison.OrdinalIgnoreCase);

                BaseNodeViewModel newNode;

                if (newPaths.Count == 1)
                {
                    newNode = await _nodeFactory.CreateSingleImageNodeAsync(newPaths[0], newX, newY, centerOnPosition: false);
                }
                else
                {
                    var multiNode = await _nodeFactory.CreateMultipleImagesNodeAsync(newPaths, newX, newY, centerOnPosition: false);
                    if (isTemp) multiNode.TemporaryFolderPath = dirPath;
                    newNode = multiNode;
                }

                newNode.Title = newTitle;

                var action = new DelegateAction(
                    "Allineamento Immagini",
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
                            if (link != null) Connections.Remove(link);

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
            double gap = 300;
            double newX = multiNode.X + multiNode.EstimatedTotalSize.Width + gap;
            double newY = multiNode.Y;
            string currentTitle = multiNode.Title;
            string cleanTitle = Regex.Replace(currentTitle, @"\s*\(\d+\s*immagini\)", "", RegexOptions.IgnoreCase);
            string modeString = mode switch
            {
                StackingMode.Average => "Media",
                StackingMode.Median => "Mediana",
                StackingMode.Sum => "Somma",
                _ => mode.ToString()
            };

            string newTitle = $"{cleanTitle.Trim()} ({modeString})";

            var newNode = await _nodeFactory.CreateSingleImageNodeFromDataAsync(
                resultData, newTitle, newX, newY
            );

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
                        if (link != null) Connections.Remove(link);

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
        ResetNodeViewCommand.NotifyCanExecuteChanged();
    }
    
    public void DeselectAllNodes()
    {
        if (SelectedNode != null) SelectedNode.IsSelected = false;
        SelectedNode = null;
        
        ResetNormalizationCommand.NotifyCanExecuteChanged();
        ShowAlignmentWindowCommand.NotifyCanExecuteChanged();
        StackImagesCommand.NotifyCanExecuteChanged();
        SaveSelectedNodeCommand.NotifyCanExecuteChanged();
        ResetNodeViewCommand.NotifyCanExecuteChanged();
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
        // Definiamo di quanto spostarci (es. 50 pixel)
        double step = 50.0;
        
        // NOTA: Se voglio vedere a Destra, devo spostare il contenuto a Sinistra (negativo)
        switch (direction.ToLower())
        {
            case "left":
                Pan(new Vector(step, 0)); 
                break;
            case "right":
                Pan(new Vector(-step, 0));
                break;
            case "up":
                Pan(new Vector(0, step));
                break;
            case "down":
                Pan(new Vector(0, -step));
                break;
        }
    }

    [RelayCommand]
    private void ZoomBoard(string mode)
    {
        // Per lo zoom da tastiera, usiamo il CENTRO della vista corrente come pivot
        var center = new Point(ViewBounds.Width / 2.0, ViewBounds.Height / 2.0);
        
        // Simuliamo un delta simile alla rotella del mouse (120 è standard)
        double delta = mode.ToLower() == "in" ? 120 : -120;
        
        Zoom(delta, center);
    }
}