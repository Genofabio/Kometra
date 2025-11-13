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

namespace KomaLab.ViewModels;

public partial class BoardViewModel : ObservableObject
{
    // --- Dipendenze ---
    private readonly INodeViewModelFactory _nodeFactory;
    private readonly IDialogService _dialogService; 
    private readonly IWindowService _windowService;

    // --- Proprietà ---
    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private double _scale = 0.46;
    [ObservableProperty] private Rect _viewBounds;
    [ObservableProperty] private BaseNodeViewModel? _selectedNode;
    public ObservableCollection<BaseNodeViewModel> Nodes { get; } = new();

    // --- Costruttore ---
    public BoardViewModel(INodeViewModelFactory nodeFactory, IDialogService dialogService, IWindowService windowService)
    {
        _nodeFactory = nodeFactory;
        _dialogService = dialogService; 
        _windowService = windowService;
        _ = LoadInitialNodesAsync();
    }

    // Caricamento iniziale
    private async Task LoadInitialNodesAsync()
    {
        await AddSingleNodeAsync("avares://KomaLab/Assets/summed_clean.fits", 200, 100);
        await AddSingleNodeAsync("avares://KomaLab/Assets/summed_clean.fits", 200, 300);
    }
    
    // --- Comandi ---

    [RelayCommand]
    private async Task AddNode()
    {
        var imagePaths = await _dialogService.ShowOpenFitsFileDialogAsync();
        if (imagePaths == null) return; 
        
        var pathList = imagePaths.ToList();
        if (pathList.Count == 0) return; 

        double screenCenterX = ViewBounds.Width / 2;
        double screenCenterY = ViewBounds.Height / 2;
        if (Scale == 0) return;
        double worldX = (screenCenterX - OffsetX) / Scale;
        double worldY = (screenCenterY - OffsetY) / Scale;
        
        if (pathList.Count == 1)
        {
            await AddSingleNodeAsync(pathList[0], worldX, worldY, centerOnPosition: true);
        }
        else
        {
            await AddMultipleNodesAsync(pathList, worldX, worldY);
        }
    }
    
    [RelayCommand(CanExecute = nameof(CanResetNormalization))]
    private async Task ResetNormalization() { await Task.CompletedTask; }
    private bool CanResetNormalization() => false;
    
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

        if (SelectedNode != null)
        {
            SelectedNode.IsSelected = false;
        }
        SelectedNode = nodeToSelect;
        if (SelectedNode != null)
        {
            SelectedNode.IsSelected = true;
        }
        
        ResetNormalizationCommand.NotifyCanExecuteChanged();
        ShowAlignmentWindowCommand.NotifyCanExecuteChanged(); 
    }
    
    public void DeselectAllNodes()
    {
        if (SelectedNode != null)
        {
            SelectedNode.IsSelected = false;
        }
        SelectedNode = null;
        
        ResetNormalizationCommand.NotifyCanExecuteChanged();
        ShowAlignmentWindowCommand.NotifyCanExecuteChanged(); 
    }
    
    [RelayCommand(CanExecute = nameof(CanShowAlignmentWindow))]
    private async Task ShowAlignmentWindow() // <-- 1. Rendi il metodo 'async Task'
    {
        if (SelectedNode == null) return;

        try
        {
            // 2. Chiama e ATTENDI (await) la versione async del servizio
            var newProcessedData = await _windowService.ShowAlignmentWindowAsync(SelectedNode);

            // 3. Controlla se l'utente ha premuto "Applica" (il risultato non è nullo)
            if (newProcessedData != null)
            {
                // 4. Passa i nuovi dati processati al nodo.
                //    Il nodo sa come gestirli (grazie al Passo 1).
                await SelectedNode.ApplyProcessedDataAsync(newProcessedData);
            }
            // Se 'newProcessedData' è nullo, l'utente ha premuto 'Annulla'
            // o 'Chiudi', quindi non facciamo nulla.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Processo di allineamento fallito: {ex.Message}");
            // Qui potresti mostrare un DialogService di errore all'utente
        }
    }
    
    // Il comando si abilita solo se un nodo è selezionato
    private bool CanShowAlignmentWindow() => SelectedNode != null;
    
    // --- Metodi Privati ---
    private async Task AddSingleNodeAsync(string imagePath, double x, double y, bool centerOnPosition = false)
    {
        try
        {
            var newNodeViewModel = await _nodeFactory.CreateSingleImageNodeAsync(this, imagePath, x, y, centerOnPosition);
            Nodes.Add(newNodeViewModel);
        }
        catch(Exception ex)
        {
            Debug.WriteLine($"ERRORE Aggiunta Nodo Singolo: {ex.Message}");
        }
    }
    
    private async Task AddMultipleNodesAsync(List<string> imagePaths, double x, double y)
    {
        try
        {
            var newNodeViewModel = await _nodeFactory.CreateMultipleImagesNodeAsync(this, imagePaths, x, y);
            Nodes.Add(newNodeViewModel);
        }
        catch(Exception ex)
        {
            Debug.WriteLine($"ERRORE Aggiunta Nodo Multiplo: {ex.Message}");
        }
    }
}