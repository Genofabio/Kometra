using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using KomaLab.Models; // <-- Ora importa la nuova gerarchIA di modelli
using KomaLab.ViewModels;
using KomaLab.ViewModels.Helpers;

namespace KomaLab.Services;

/// <summary>
/// Implementazione concreta di INodeViewModelFactory.
/// </summary>
public class NodeViewModelFactory : INodeViewModelFactory
{
    private readonly IFitsService _fitsService;
    private readonly IImageProcessingService _processingService;

    public NodeViewModelFactory(
        IFitsService fitsService, 
        IImageProcessingService processingService) // <-- Aggiungi qui
    {
        _fitsService = fitsService;
        _processingService = processingService; // <-- Salvalo
    }

    /// <summary>
    /// Crea, carica e posiziona un nuovo SingleImageNodeViewModel.
    /// </summary>
    public async Task<SingleImageNodeViewModel> CreateSingleImageNodeAsync(
        BoardViewModel parent, 
        string imagePath, 
        double x, double y, 
        bool centerOnPosition = false)
    {
        // 1. CREA IL NUOVO MODELLO SPECIFICO
        var newNodeModel = new SingleImageNodeModel
        {
            ImagePath = imagePath, // <-- Inserisce il percorso nel modello
            Title = Path.GetFileName(imagePath),
            X = x,
            Y = y
        };
    
        // 2. PASSA IL NUOVO MODELLO al costruttore
        var newNodeViewModel = new SingleImageNodeViewModel(
            parent, 
            newNodeModel,
            _fitsService,
            _processingService);
        
        await newNodeViewModel.LoadDataAsync();
        
        if (centerOnPosition)
        {
            var size = newNodeViewModel.EstimatedTotalSize;
            newNodeViewModel.X = x - (size.Width / 2);
            newNodeViewModel.Y = y - (size.Height / 2);
        }
        
        return newNodeViewModel;
    }
    
    /// <summary>
    /// Crea, pre-scansiona e pre-carica un nuovo MultipleImagesNodeViewModel.
    /// </summary>
    public async Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(
        BoardViewModel parent, 
        List<string> imagePaths, 
        double x, double y)
    {
        // --- 1. Pre-scansione (invariato) ---
        double maxWidth = 0;
        double maxHeight = 0;
        foreach (var path in imagePaths)
        {
            try
            {
                var imgSize = await _fitsService.GetFitsImageSizeAsync(path);
                if (imgSize.Width > maxWidth) maxWidth = imgSize.Width;
                if (imgSize.Height > maxHeight) maxHeight = imgSize.Height;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Impossibile leggere l'header di {path}: {ex.Message}");
            }
        }
        var maxSize = new Size(maxWidth, maxHeight);
        
        // --- 2. Caricamento primo file (invariato) ---
        string title;
        FitsImageData? firstImageData;
        try
        {
            firstImageData = await _fitsService.LoadFitsFromFileAsync(imagePaths[0]);
            if (firstImageData == null) { throw new InvalidOperationException("File FITS iniziale non valido."); }
            
            title = firstImageData.FitsHeader.GetStringValue("OBJECT");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = Path.GetFileName(imagePaths[0]);
            }
            title += $" ({imagePaths.Count} immagini)";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore critico durante il caricamento di {imagePaths[0]}: {ex.Message}");
            throw new InvalidOperationException("Impossibile creare la pila di immagini.", ex);
        }

        // --- 3. CREA IL NUOVO MODELLO SPECIFICO ---
        var newNodeModel = new MultipleImagesNodeModel
        {
            Title = title,
            X = x,
            Y = y,
            ImagePaths = imagePaths // <-- Inserisce la lista di percorsi nel modello
        };

        // --- 4. PASSA IL NUOVO MODELLO al costruttore ---
        var newNodeViewModel = new MultipleImagesNodeViewModel(
            parent, 
            newNodeModel, // <-- Passa il modello
            _fitsService, 
            _processingService,
            maxSize,
            firstImageData); // Passa i dati pre-caricati
        
        await newNodeViewModel.InitializeAsync();

        // --- 5. Centra (invariato) ---
        var size = newNodeViewModel.EstimatedTotalSize;
        newNodeViewModel.X = x - (size.Width / 2);
        newNodeViewModel.Y = y - (size.Height / 2);

        return newNodeViewModel;
    }
    
    public async Task<SingleImageNodeViewModel> CreateSingleImageNodeFromDataAsync(
        BoardViewModel parent, 
        FitsImageData data, 
        string title, 
        double x, 
        double y)
    {
        // 1. Crea il Modello (Dati persistenti di base)
        // Impostiamo ImagePath vuoto o speciale perché i dati non vengono letti da disco
        var model = new SingleImageNodeModel
        {
            Title = title,
            X = x,
            Y = y,
            ImagePath = string.Empty // Segnala che è un nodo "in-memory" o generato
        };

        // 2. Crea il ViewModel usando il costruttore standard che hai definito
        // Nota: La Factory deve avere _fitsService e _processingService iniettati nel suo costruttore
        var node = new SingleImageNodeViewModel(parent, model, _fitsService, _processingService);

        // 3. Inietta i dati calcolati (Stacking)
        // Questo metodo internamente creerà il FitsRenderer, calcolerà le soglie
        // e inizializzerà la visualizzazione, esattamente come se avesse caricato un file.
        await node.ApplyProcessedDataAsync(new List<FitsImageData> { data });

        return node;
    }
}