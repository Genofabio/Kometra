using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Kometra.Models.Nodes;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Metadata;
using Kometra.ViewModels.Nodes;

namespace Kometra.Services.Factories;

public class NodeViewModelFactory : INodeViewModelFactory
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsRendererFactory _rendererFactory;

    public NodeViewModelFactory(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsRendererFactory rendererFactory)
    {
        _dataManager = dataManager;
        _metadataService = metadataService;
        _rendererFactory = rendererFactory;
    }

    public async Task<SingleImageNodeViewModel> CreateSingleImageNodeAsync(string path, double x, double y)
    {
        // 1. Calcoliamo le dimensioni reali dall'header
        var size = await CalculateMaxDimensionsAsync(new List<string> { path });

        var model = new SingleImageNodeModel
        {
            ImagePath = path,
            Title = Path.GetFileName(path),
            X = x,
            Y = y
        };

        // 2. Creazione VM (Senza dipendenze di export)
        var vm = new SingleImageNodeViewModel(model, _dataManager, _rendererFactory, size);
    
        await vm.InitializeAsync();
        ApplyNodeCentering(vm, x, y);
    
        return vm;
    }

    public async Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(List<string> paths, double x, double y)
    {
        if (paths == null || !paths.Any()) throw new ArgumentException("Nessun file selezionato.");

        // 1. Analisi preliminare per il layout e titolo intelligente
        var maxSize = await CalculateMaxDimensionsAsync(paths);
        string title = await DetermineSmartTitleAsync(paths[0], paths.Count);

        var model = new MultipleImagesNodeModel
        {
            ImagePaths = paths,
            Title = title,
            X = x,
            Y = y
        };

        // 2. Istanziamento VM ripulito
        // Passiamo solo le dipendenze necessarie alla gestione della sequenza
        var vm = new MultipleImagesNodeViewModel(
            model, 
            _dataManager, 
            _rendererFactory, 
            maxSize);
        
        await vm.InitializeAsync();
        ApplyNodeCentering(vm, x, y);

        return vm;
    }

    // --- Helpers Strategici ---

    private async Task<string> DetermineSmartTitleAsync(string firstPath, int count)
    {
        var header = await _dataManager.GetHeaderOnlyAsync(firstPath);
        if (header != null)
        {
            var obj = _metadataService.GetStringValue(header, "OBJECT");
            if (!string.IsNullOrEmpty(obj)) return $"{obj} ({count} frames)";
        }
        return $"Sequence ({count} frames)";
    }

    private async Task<Size> CalculateMaxDimensionsAsync(List<string> paths)
    {
        using var semaphore = new SemaphoreSlim(10); 
    
        var tasks = paths.Select(async path =>
        {
            await semaphore.WaitAsync();
            try
            {
                var header = await _dataManager.GetHeaderOnlyAsync(path);
                if (header == null) return new Size(0, 0);

                return new Size(
                    _metadataService.GetIntValue(header, "NAXIS1"),
                    _metadataService.GetIntValue(header, "NAXIS2")
                );
            }
            finally
            {
                semaphore.Release();
            }
        });

        var sizes = await Task.WhenAll(tasks);
    
        double maxWidth = sizes.Max(s => s.Width);
        double maxHeight = sizes.Max(s => s.Height);

        return (maxWidth > 0) ? new Size(maxWidth, maxHeight) : new Size(512, 512);
    }

    private void ApplyNodeCentering(BaseNodeViewModel vm, double x, double y)
    {
        var size = vm.EstimatedTotalSize;
        if (size.Width > 0 && size.Height > 0)
        {
            vm.X = x - (size.Width / 2.0);
            vm.Y = y - (size.Height / 2.0);
        }
        else
        {
            vm.X = x;
            vm.Y = y;
        }
    }
}