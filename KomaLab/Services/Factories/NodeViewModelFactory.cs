using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia; // Necessario per la struttura Size (UI)
using KomaLab.Models.Fits;
using KomaLab.Models.Nodes;
using KomaLab.Services.Data;
using KomaLab.Services.Imaging;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.Services.Factories;

// ---------------------------------------------------------------------------
// FILE: NodeViewModelFactory.cs
// RUOLO: Factory di ViewModel
// DESCRIZIONE:
// Crea istanze complesse dei nodi (Single/Multiple Image) iniettando le dipendenze
// corrette (I/O, Converter, Analysis) e gestendo il caricamento iniziale.
// ---------------------------------------------------------------------------

public class NodeViewModelFactory : INodeViewModelFactory
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;

    public NodeViewModelFactory(
        IFitsIoService ioService,
        IFitsImageDataConverter converter,
        IImageAnalysisService analysis)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
    }

    public async Task<SingleImageNodeViewModel> CreateSingleImageNodeAsync(
        string imagePath, 
        double x, double y, 
        bool centerOnPosition = false)
    {
        // 1. Modello Dati
        var newNodeModel = new SingleImageNodeModel
        {
            ImagePath = imagePath,
            Title = Path.GetFileName(imagePath),
            X = x,
            Y = y
        };
    
        // 2. ViewModel
        var newNodeViewModel = new SingleImageNodeViewModel(
            newNodeModel,
            _ioService,
            _converter,
            _analysis
        );
        
        // 3. Caricamento Dati
        await newNodeViewModel.LoadDataAsync();
        
        // 4. Centratura
        if (centerOnPosition)
        {
            Size size = newNodeViewModel.EstimatedTotalSize;
            newNodeViewModel.X = x - (size.Width / 2.0);
            newNodeViewModel.Y = y - (size.Height / 2.0);
        }
        
        return newNodeViewModel;
    }

    public async Task<SingleImageNodeViewModel> CreateSingleImageNodeFromDataAsync(
        FitsImageData data, 
        string title, 
        double x, 
        double y)
    {
        var model = new SingleImageNodeModel
        {
            ImagePath = string.Empty,
            Title = title,
            X = x,
            Y = y
        };

        var node = new SingleImageNodeViewModel(
            model, 
            _ioService, 
            _converter, 
            _analysis);

        // Iniezione diretta dati in memoria
        await node.ApplyProcessedDataAsync(new List<FitsImageData> { data });

        return node;
    }

    public async Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(
        List<string> imagePaths, 
        double x, double y,
        bool centerOnPosition = false)
    {
        if (imagePaths == null || imagePaths.Count == 0)
            throw new ArgumentException("La lista dei file non può essere vuota.", nameof(imagePaths));

        // 1. Scansione preliminare header per dimensioni massime
        double maxWidth = 0;
        double maxHeight = 0;

        foreach (var path in imagePaths)
        {
            try
            {
                var header = await _ioService.ReadHeaderOnlyAsync(path);
                if (header != null)
                {
                    double w = header.GetIntValue("NAXIS1");
                    double h = header.GetIntValue("NAXIS2");
                    if (w > maxWidth) maxWidth = w;
                    if (h > maxHeight) maxHeight = h;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Factory] Warning header {path}: {ex.Message}");
            }
        }
        
        if (maxWidth == 0) maxWidth = 500;
        if (maxHeight == 0) maxHeight = 500;

        var maxSize = new Size(maxWidth, maxHeight);
        
        // 2. Caricamento primo frame per thumbnail/info
        string title;
        FitsImageData? firstImageData;
        try
        {
            firstImageData = await _ioService.LoadAsync(imagePaths[0]);
            
            if (firstImageData == null) 
                throw new InvalidOperationException($"File iniziale non valido: {imagePaths[0]}");
            
            title = firstImageData.FitsHeader.GetStringValue("OBJECT");
            if (string.IsNullOrWhiteSpace(title)) 
                title = Path.GetFileName(imagePaths[0]);
            
            title += $" ({imagePaths.Count} frame)";
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Impossibile inizializzare il nodo multi-immagine.", ex);
        }

        // 3. ViewModel
        var newNodeModel = new MultipleImagesNodeModel
        {
            Title = title,
            X = x,
            Y = y,
            ImagePaths = imagePaths
        };

        var newNodeViewModel = new MultipleImagesNodeViewModel(
            newNodeModel, 
            _ioService, 
            _converter,
            _analysis, 
            maxSize, 
            firstImageData); 
        
        await newNodeViewModel.InitializeAsync();

        if (centerOnPosition)
        {
            Size size = newNodeViewModel.EstimatedTotalSize;
            newNodeViewModel.X = x - (size.Width / 2.0);
            newNodeViewModel.Y = y - (size.Height / 2.0);
        }

        return newNodeViewModel;
    }
}