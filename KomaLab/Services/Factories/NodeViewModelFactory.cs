using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using KomaLab.Models;
using KomaLab.Services.Data;
using KomaLab.Services.Imaging;
using KomaLab.ViewModels;

namespace KomaLab.Services.Factories;

public class NodeViewModelFactory : INodeViewModelFactory
{
    // Servizi necessari ai Nodi
    private readonly IFitsService _fitsService;
    private readonly IFitsDataConverter _converter;    // <--- NUOVO
    private readonly IImageAnalysisService _analysis;  // <--- NUOVO

    public NodeViewModelFactory(
        IFitsService fitsService,
        IFitsDataConverter converter,      // Iniezione dei nuovi servizi separati
        IImageAnalysisService analysis)
    {
        _fitsService = fitsService;
        _converter = converter;
        _analysis = analysis;
    }

    /// <summary>
    /// Crea, carica e posiziona un nuovo SingleImageNodeViewModel.
    /// </summary>
    public async Task<SingleImageNodeViewModel> CreateSingleImageNodeAsync(
        string imagePath, // NOTA: Rimossa BoardViewModel parent
        double x, double y, 
        bool centerOnPosition = false)
    {
        // 1. CREA IL NUOVO MODELLO SPECIFICO
        var newNodeModel = new SingleImageNodeModel
        {
            ImagePath = imagePath,
            Title = Path.GetFileName(imagePath),
            X = x,
            Y = y
        };
    
        // 2. ISTANZIA IL VIEWMODEL (Con i nuovi servizi)
        var newNodeViewModel = new SingleImageNodeViewModel(
            newNodeModel,
            _fitsService,
            _converter, // <--- Passiamo il converter
            _analysis   // <--- Passiamo l'analysis
        );
        
        await newNodeViewModel.LoadDataAsync();
        
        if (centerOnPosition)
        {
            // Usiamo EstimatedTotalSize che viene calcolato dal VM dopo il load
            var size = newNodeViewModel.EstimatedTotalSize;
            newNodeViewModel.X = x - (size.Width / 2);
            newNodeViewModel.Y = y - (size.Height / 2);
        }
        
        return newNodeViewModel;
    }

    public async Task<SingleImageNodeViewModel> CreateSingleImageNodeFromDataAsync(
        FitsImageData data, 
        string title, 
        double x, 
        double y)
    {
        // 1. Crea il Modello per dati in memoria
        var model = new SingleImageNodeModel
        {
            Title = title,
            X = x,
            Y = y,
            ImagePath = string.Empty 
        };

        // 2. Crea il ViewModel
        var node = new SingleImageNodeViewModel(
            model, 
            _fitsService, 
            _converter, 
            _analysis);

        // 3. Inietta i dati calcolati (es. risultato di uno stack)
        await node.ApplyProcessedDataAsync(new List<FitsImageData> { data });

        return node;
    }

    /// <summary>
    /// Crea, pre-scansiona e pre-carica un nuovo MultipleImagesNodeViewModel.
    /// </summary>
    public async Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(
        List<string> imagePaths, 
        double x, double y,
        bool centerOnPosition = false) // <--- Parametro Aggiunto
    {
        // --- 1. Pre-scansione (Invariato) ---
        double maxWidth = 0;
        double maxHeight = 0;

        foreach (var path in imagePaths)
        {
            try
            {
                var (w, h) = await _fitsService.GetFitsImageSizeAsync(path);
                if (w > maxWidth) maxWidth = w;
                if (h > maxHeight) maxHeight = h;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Impossibile leggere l'header di {path}: {ex.Message}");
            }
        }
        var maxSize = new Size(maxWidth, maxHeight);
        
        // --- 2. Caricamento primo file (Invariato) ---
        string title;
        FitsImageData? firstImageData;
        try
        {
            firstImageData = await _fitsService.LoadFitsFromFileAsync(imagePaths[0]);
            if (firstImageData == null) throw new InvalidOperationException("File FITS iniziale non valido.");
            
            title = firstImageData.FitsHeader.GetStringValue("OBJECT");
            if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileName(imagePaths[0]);
            title += $" ({imagePaths.Count} immagini)";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore critico durante il caricamento di {imagePaths[0]}: {ex.Message}");
            throw new InvalidOperationException("Impossibile creare la pila di immagini.", ex);
        }

        // --- 3. CREA IL NUOVO MODELLO ---
        var newNodeModel = new MultipleImagesNodeModel
        {
            Title = title,
            X = x, // Se centerOnPosition=false, questa è già la coordinata Top-Left corretta
            Y = y,
            ImagePaths = imagePaths
        };

        // --- 4. ISTANZIA IL VIEWMODEL (Invariato) ---
        var newNodeViewModel = new MultipleImagesNodeViewModel(
            newNodeModel, 
            _fitsService, 
            _converter,
            _analysis, 
            maxSize,
            firstImageData); 
        
        await newNodeViewModel.InitializeAsync();

        // --- 5. Centra (SOLO SE RICHIESTO) ---
        if (centerOnPosition) // <--- Condizione aggiunta
        {
            var size = newNodeViewModel.EstimatedTotalSize;
            newNodeViewModel.X = x - (size.Width / 2);
            newNodeViewModel.Y = y - (size.Height / 2);
        }

        return newNodeViewModel;
    }
}