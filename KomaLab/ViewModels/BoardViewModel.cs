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
    private readonly IImageProcessingService _processingService;

    // --- Proprietà ---
    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private double _scale = 0.46;
    [ObservableProperty] private Rect _viewBounds;
    [ObservableProperty] private BaseNodeViewModel? _selectedNode;
    public ObservableCollection<BaseNodeViewModel> Nodes { get; } = new();

    // --- Costruttore ---
    public BoardViewModel(
        INodeViewModelFactory nodeFactory, 
        IDialogService dialogService, 
        IWindowService windowService,
        IImageProcessingService processingService)
    {
        _nodeFactory = nodeFactory;
        _dialogService = dialogService; 
        _windowService = windowService;
    
        // Assegna il servizio
        _processingService = processingService; 
    
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
    private async Task ResetNormalization()
    {
        if (SelectedNode != null)
        {
            // Deleghiamo al nodo la logica di reset
            await SelectedNode.ResetThresholdsAsync();
        }
    }
    private bool CanResetNormalization() => SelectedNode != null;
    
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
        StackImagesCommand.NotifyCanExecuteChanged();
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
        StackImagesCommand.NotifyCanExecuteChanged();
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
    
    [RelayCommand(CanExecute = nameof(CanStackImages))]
    private async Task StackImages(StackingMode mode)
    {
        // 1. Controllo di sicurezza (il CanExecute lo gestisce già per la UI, ma controlliamo comunque)
        if (SelectedNode is not MultipleImagesNodeViewModel multiNode) return;

        try
        {
            // 2. RECUPERO DATI POLIMORFICO
            var rawDataList = await SelectedNode.GetCurrentDataAsync();

            // 3. Filtriamo eventuali null (sicurezza)
            var sourceImages = rawDataList
                .Where(d => d != null)
                .Cast<FitsImageData>()
                .ToList();

            if (sourceImages.Count < 2)
            {
                Debug.WriteLine("Servono almeno 2 immagini valide per fare uno stack.");
                return;
            }

            // 4. Eseguiamo il calcolo (Service)
            var resultData = await _processingService.ComputeStackAsync(sourceImages, mode);

            // 5. Prepariamo i metadati per il nuovo nodo
            double newX = multiNode.X + 50;
            double newY = multiNode.Y + 50;
        
            // --- LOGICA DI PULIZIA DEL TITOLO AGGIUNTA QUI ---
            string baseTitle = multiNode.Title;
            
            // Trova l'ultima parentesi aperta e chiusa
            int lastOpenParen = baseTitle.LastIndexOf('(');
            int lastCloseParen = baseTitle.LastIndexOf(')');

            // Se l'ultima parentesi aperta esiste ed è prima di quella chiusa,
            // assumiamo che il contenuto sia un metadato da rimuovere.
            if (lastOpenParen != -1 && lastCloseParen > lastOpenParen)
            {
                // Taglia la stringa fino all'ultima parentesi aperta
                baseTitle = baseTitle.Substring(0, lastOpenParen).TrimEnd();
            }
            
            string opName = mode switch 
            {
                StackingMode.Sum => "Somma",
                StackingMode.Average => "Media",
                StackingMode.Median => "Mediana",
                _ => "Stack"
            };
            // Ricostruisce il titolo pulito con il nuovo suffisso dell'operazione
            string newTitle = $"{baseTitle} ({opName})"; 

            // 6. Creiamo il nuovo nodo (usando la Factory modificata nello step precedente)
            var newNode = await _nodeFactory.CreateSingleImageNodeFromDataAsync(
                this, 
                resultData, 
                newTitle, 
                newX, 
                newY
            );

            // 7. Aggiungiamo alla Board
            Nodes.Add(newNode);
            SetSelectedNode(newNode);
        
            Debug.WriteLine($"Stacking {mode} completato.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore durante lo stacking: {ex.Message}");
        }
    }

    private bool CanStackImages(StackingMode mode)
    {
        // Il comando è abilitato SOLO se il nodo selezionato è di tipo Multiplo
        return SelectedNode is MultipleImagesNodeViewModel;
    }
    
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