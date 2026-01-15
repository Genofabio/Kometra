using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using KomaLab.Models.Fits;
using KomaLab.Models.Nodes;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.Services.Factories;

/// <summary>
/// Factory per la creazione e inizializzazione dei ViewModel dei nodi.
/// Ottimizzata per leggere solo gli Header FITS durante la fase di istanziazione.
/// </summary>
public class NodeViewModelFactory : INodeViewModelFactory
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly IFitsRendererFactory _rendererFactory;

    public NodeViewModelFactory(
        IFitsIoService ioService,
        IFitsOpenCvConverter converter,
        IImageAnalysisService analysis,
        IFitsRendererFactory rendererFactory)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
    }

    // --- API PUBBLICA ---

    public async Task<SingleImageNodeViewModel> CreateSingleImageNodeAsync(
        string imagePath, double x, double y, bool centerOnPosition = false)
    {
        // 1. Lettura Header Leggera (No Pixel Load)
        Size imageSize = new Size(500, 500);
        string fileName = Path.GetFileName(imagePath);

        if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
        {
            try 
            { 
                var header = await _ioService.ReadHeaderAsync(imagePath);
                if (header != null)
                {
                    imageSize = new Size(header.GetIntValue("NAXIS1"), header.GetIntValue("NAXIS2"));
                }
            }
            catch { /* Fallback size */ }
        }

        // 2. Setup Modello
        var model = new SingleImageNodeModel
        {
            ImagePath = imagePath,
            Title = fileName,
            X = x,
            Y = y
        };

        // 3. Creazione VM (Passiamo Collection implicita nel costruttore del VM se null)
        var vm = new SingleImageNodeViewModel(model, _ioService, _rendererFactory, imageSize, initialCollection: null);
        
        // 4. Trigger Caricamento Asincrono dei Dati Reali
        await vm.InitializeAsync(centerOnPosition: true);
        
        ApplyNodeCentering(vm, x, y, centerOnPosition);
        return vm;
    }

    public async Task<SingleImageNodeViewModel> CreateNodeFromCollectionAsync(
        FitsCollection collection, string title, double x, double y)
    {
        // 1. Determina path principale e dimensioni
        string mainPath = collection.Count > 0 ? collection[0].FilePath : string.Empty;
        Size imageSize = new Size(500, 500);

        if (!string.IsNullOrEmpty(mainPath))
        {
            // Proviamo a leggere l'header dal file su disco o dalla cache se l'abbiamo (qui leggiamo disco per sicurezza)
            var header = await _ioService.ReadHeaderAsync(mainPath);
            if (header != null)
            {
                imageSize = new Size(header.GetIntValue("NAXIS1"), header.GetIntValue("NAXIS2"));
            }
        }

        var model = new SingleImageNodeModel 
        { 
            ImagePath = mainPath, 
            Title = title, 
            X = x, 
            Y = y 
        };

        // Passiamo esplicitamente la collection già pronta
        var vm = new SingleImageNodeViewModel(model, _ioService, _rendererFactory, imageSize, initialCollection: collection);
        
        await vm.InitializeAsync(centerOnPosition: true);
        return vm;
    }

    public async Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(
        List<string> imagePaths, double x, double y, bool centerOnPosition = false)
    {
        if (imagePaths == null || !imagePaths.Any())
            throw new ArgumentException("La lista dei file non può essere vuota.");

        // 1. Determina dimensioni massime (Header Only)
        var maxSize = await CalculateMaxDimensionsAsync(imagePaths);
        
        // 2. Determina Titolo dal primo header
        string title = "Stack";
        try
        {
            var firstHeader = await _ioService.ReadHeaderAsync(imagePaths[0]);
            if (firstHeader != null)
            {
                title = GetNodeTitle(firstHeader, imagePaths.Count);
            }
        }
        catch { /* Ignore title error */ }

        // 3. Setup Modello
        var model = new MultipleImagesNodeModel
        {
            Title = title,
            X = x,
            Y = y,
            ImagePaths = imagePaths
        };

        // 4. Creazione Collection
        var collection = new FitsCollection(imagePaths, cacheSize: 5);

        // 5. Creazione VM
        var vm = new MultipleImagesNodeViewModel(
            model, _ioService, _converter, _analysis, _rendererFactory, maxSize, initialCollection: collection);
        
        await vm.InitializeAsync(centerOnPosition: true);

        ApplyNodeCentering(vm, x, y, centerOnPosition);
        return vm;
    }

    // --- HELPER PRIVATI ---

    private async Task<Size> CalculateMaxDimensionsAsync(List<string> paths)
    {
        double maxWidth = 0, maxHeight = 0;
        foreach (var path in paths)
        {
            // Header Only = Molto veloce
            var header = await _ioService.ReadHeaderAsync(path);
            if (header == null) continue;

            maxWidth = Math.Max(maxWidth, header.GetIntValue("NAXIS1"));
            maxHeight = Math.Max(maxHeight, header.GetIntValue("NAXIS2"));
        }
        return (maxWidth > 0) ? new Size(maxWidth, maxHeight) : new Size(500, 500);
    }

    private string GetNodeTitle(FitsHeader header, int count)
    {
        var objName = header.GetStringValue("OBJECT");
        var baseName = !string.IsNullOrWhiteSpace(objName) ? objName : "Sequence";
        return $"{baseName} ({count} frame)";
    }

    private void ApplyNodeCentering(BaseNodeViewModel vm, double x, double y, bool center)
    {
        if (!center) return;
        var size = vm.EstimatedTotalSize;
        vm.X = x - (size.Width / 2.0);
        vm.Y = y - (size.Height / 2.0);
    }
}