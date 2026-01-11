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
using KomaLab.ViewModels;

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
    private readonly IFitsIoService _ioService;      // Sostituisce il vecchio FitsService
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

    /// <summary>
    /// Crea un nodo immagine singola caricando il file da disco.
    /// </summary>
    public async Task<SingleImageNodeViewModel> CreateSingleImageNodeAsync(
        string imagePath, 
        double x, double y, 
        bool centerOnPosition = false)
    {
        // 1. Creazione del Modello Dati
        // FIX: Usiamo l'inizializzatore di proprietà invece del costruttore con parametri
        var newNodeModel = new SingleImageNodeModel
        {
            ImagePath = imagePath,
            Title = Path.GetFileName(imagePath),
            X = x,
            Y = y
        };
    
        // 2. Istanziazione del ViewModel con i NUOVI servizi
        var newNodeViewModel = new SingleImageNodeViewModel(
            newNodeModel,
            _ioService,  // Iniettiamo il servizio I/O puro
            _converter,
            _analysis
        );
        
        // 3. Caricamento asincrono dei dati (Header + Raw)
        await newNodeViewModel.LoadDataAsync();
        
        // 4. Centratura opzionale basata sulle dimensioni reali caricate
        if (centerOnPosition)
        {
            // EstimatedTotalSize è di tipo Avalonia.Size
            Size size = newNodeViewModel.EstimatedTotalSize;
            newNodeViewModel.X = x - (size.Width / 2.0);
            newNodeViewModel.Y = y - (size.Height / 2.0);
        }
        
        return newNodeViewModel;
    }

    /// <summary>
    /// Crea un nodo immagine singola iniettando direttamente dati in memoria (es. da un'operazione di stack).
    /// </summary>
    public async Task<SingleImageNodeViewModel> CreateSingleImageNodeFromDataAsync(
        FitsImageData data, 
        string title, 
        double x, 
        double y)
    {
        // FIX: Inizializzatore di proprietà anche qui
        var model = new SingleImageNodeModel
        {
            ImagePath = string.Empty, // Path vuoto per dati in memoria
            Title = title,
            X = x,
            Y = y
        };

        var node = new SingleImageNodeViewModel(
            model, 
            _ioService, 
            _converter, 
            _analysis);

        // Inietta i dati processati in memoria
        await node.ApplyProcessedDataAsync(new List<FitsImageData> { data });

        return node;
    }

    /// <summary>
    /// Crea un nodo multi-immagine (stacking/video).
    /// </summary>
    public async Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(
        List<string> imagePaths, 
        double x, double y,
        bool centerOnPosition = false)
    {
        if (imagePaths == null || imagePaths.Count == 0)
            throw new ArgumentException("La lista dei file non può essere vuota.", nameof(imagePaths));

        // --- 1. Pre-scansione Dimensioni (Ottimizzata) ---
        // Calcoliamo la bounding box massima leggendo SOLO gli header
        double maxWidth = 0;
        double maxHeight = 0;

        foreach (var path in imagePaths)
        {
            try
            {
                // Usiamo il metodo efficiente di IoService che legge solo l'header
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
        
        // Fallback size se qualcosa va storto
        if (maxWidth == 0) maxWidth = 500;
        if (maxHeight == 0) maxHeight = 500;

        // Usiamo Avalonia.Size
        var maxSize = new Size(maxWidth, maxHeight);
        
        // --- 2. Caricamento dati iniziali dal primo file ---
        // Necessario per avere un'immagine di riferimento da mostrare subito
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
            Debug.WriteLine($"[Factory] Errore critico load first image: {ex.Message}");
            throw new InvalidOperationException("Impossibile inizializzare il nodo multi-immagine.", ex);
        }

        // --- 3. Creazione Modello e ViewModel ---
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
        
        // Inizializzazione asincrona
        await newNodeViewModel.InitializeAsync();

        // --- 4. Centratura ---
        if (centerOnPosition)
        {
            Size size = newNodeViewModel.EstimatedTotalSize;
            newNodeViewModel.X = x - (size.Width / 2.0);
            newNodeViewModel.Y = y - (size.Height / 2.0);
        }

        return newNodeViewModel;
    }
}