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
/// Assicura che i dati vengano pre-caricati per garantire un layout UI immediato e coerente.
/// </summary>
public class NodeViewModelFactory : INodeViewModelFactory
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly IFitsRendererFactory _rendererFactory;

    public NodeViewModelFactory(
        IFitsIoService ioService,
        IFitsImageDataConverter converter,
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
        FitsImageData? initialData = null;
        if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
        {
            try { initialData = await _ioService.LoadAsync(imagePath); }
            catch { /* Fallback gestito dal VM */ }
        }

        var model = new SingleImageNodeModel
        {
            ImagePath = imagePath,
            Title = Path.GetFileName(imagePath),
            X = x,
            Y = y
        };

        var imageSize = initialData != null 
            ? new Size(initialData.Width, initialData.Height) 
            : new Size(500, 500);

        var vm = new SingleImageNodeViewModel(model, _ioService, _rendererFactory, imageSize, initialData);
        
        await vm.InitializeAsync(centerOnPosition: true);
        
        ApplyNodeCentering(vm, x, y, centerOnPosition);
        return vm;
    }

    public async Task<SingleImageNodeViewModel> CreateSingleImageNodeFromDataAsync(
        FitsImageData data, string title, double x, double y)
    {
        var model = new SingleImageNodeModel { ImagePath = string.Empty, Title = title, X = x, Y = y };
        var imageSize = new Size(data.Width, data.Height);

        var vm = new SingleImageNodeViewModel(model, _ioService, _rendererFactory, imageSize, data);
        
        await vm.InitializeAsync(centerOnPosition: true);
        return vm;
    }

    public async Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(
        List<string> imagePaths, double x, double y, bool centerOnPosition = false)
    {
        if (imagePaths == null || !imagePaths.Any())
            throw new ArgumentException("La lista dei file non può essere vuota.");

        // 1. Determina le dimensioni massime analizzando gli header (Layout Deterministic)
        var maxSize = await CalculateMaxDimensionsAsync(imagePaths);
        
        // 2. Caricamento primo frame per inizializzazione metadati e titolo
        var firstImageData = await _ioService.LoadAsync(imagePaths[0]) 
            ?? throw new InvalidOperationException($"File non valido: {imagePaths[0]}");

        var title = GetNodeTitle(firstImageData, imagePaths.Count);

        var model = new MultipleImagesNodeModel
        {
            Title = title,
            X = x,
            Y = y,
            ImagePaths = imagePaths
        };

        var vm = new MultipleImagesNodeViewModel(model, _ioService, _converter, _analysis, _rendererFactory, maxSize, firstImageData);
        
        await vm.InitializeAsync(centerOnPosition: true);

        ApplyNodeCentering(vm, x, y, centerOnPosition);
        return vm;
    }

    // --- HELPER PRIVATI ---

    /// <summary>
    /// Scansiona gli header dei file per trovare la dimensione massima.
    /// </summary>
    private async Task<Size> CalculateMaxDimensionsAsync(List<string> paths)
    {
        double maxWidth = 0, maxHeight = 0;
        foreach (var path in paths)
        {
            var header = await _ioService.ReadHeaderOnlyAsync(path);
            if (header == null) continue;

            maxWidth = Math.Max(maxWidth, header.GetIntValue("NAXIS1"));
            maxHeight = Math.Max(maxHeight, header.GetIntValue("NAXIS2"));
        }
        return (maxWidth > 0) ? new Size(maxWidth, maxHeight) : new Size(500, 500);
    }

    /// <summary>
    /// Estrae il titolo dell'oggetto dai metadati FITS o dal nome file.
    /// </summary>
    private string GetNodeTitle(FitsImageData data, int count)
    {
        var objName = data.FitsHeader.GetStringValue("OBJECT");
        var baseName = !string.IsNullOrWhiteSpace(objName) ? objName : "Stack";
        return $"{baseName} ({count} frame)";
    }

    /// <summary>
    /// Applica la centratura del nodo rispetto alle coordinate fornite se richiesto.
    /// </summary>
    private void ApplyNodeCentering(BaseNodeViewModel vm, double x, double y, bool center)
    {
        if (!center) return;
        var size = vm.EstimatedTotalSize;
        vm.X = x - (size.Width / 2.0);
        vm.Y = y - (size.Height / 2.0);
    }
}