using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using KomaLab.Models.Visualization;
using KomaLab.Services.Astrometry;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using KomaLab.ViewModels.Nodes;
using KomaLab.ViewModels.Tools;
using KomaLab.Views;
using Microsoft.Extensions.DependencyInjection;
using nom.tam.fits;

namespace KomaLab.Services.UI;

// ---------------------------------------------------------------------------
// FILE: WindowService.cs
// RUOLO: Orchestratore delle Finestre (UI Coordinator)
// ---------------------------------------------------------------------------

public class WindowService : IWindowService
{
    private Window? _mainWindow;
    private readonly IServiceProvider _serviceProvider;

    public WindowService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public void RegisterMainWindow(Window window)
    {
        _mainWindow = window;
    }

    /// <summary>
    /// Mostra il tool di allineamento risolvendo tutte le dipendenze scientifiche necessarie.
    /// </summary>
    public async Task<List<string>?> ShowAlignmentWindowAsync(
        List<string> sourcePaths, 
        VisualizationMode initialMode = VisualizationMode.Linear) 
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        // Risoluzione Dipendenze
        var ioService = _serviceProvider.GetRequiredService<IFitsIoService>();
        var metadataService = _serviceProvider.GetRequiredService<IFitsMetadataService>();
        var alignmentService = _serviceProvider.GetRequiredService<IAlignmentService>();
        var converter = _serviceProvider.GetRequiredService<IFitsImageDataConverter>();
        var analysis = _serviceProvider.GetRequiredService<IImageAnalysisService>();
        var jplService = _serviceProvider.GetRequiredService<IJplHorizonsService>();
        
        // RECUPERO NUOVO SERVIZIO
        var mediaExport = _serviceProvider.GetRequiredService<IMediaExportService>(); 

        using var viewModel = new AlignmentToolViewModel(
            sourcePaths,
            ioService,
            metadataService,
            alignmentService,
            converter,
            analysis,
            jplService,
            mediaExport, // <--- INIEZIONE NEL COSTRUTTORE
            initialMode
        );
    
        var alignmentWindow = new AlignmentToolView { DataContext = viewModel };

        Action closeHandler = () => alignmentWindow.Close();
        viewModel.RequestClose += closeHandler;

        await alignmentWindow.ShowDialog<object>(_mainWindow); 

        viewModel.RequestClose -= closeHandler;
        return viewModel.DialogResult ? viewModel.FinalProcessedPaths : null; 
    }
    
    /// <summary>
    /// Apre l'editor dell'header in modalità modale.
    /// </summary>
    public async Task<Header?> ShowHeaderEditorAsync(ImageNodeViewModel node)
    {
        if (_mainWindow == null) return null;
        
        var metadataService = _serviceProvider.GetRequiredService<IFitsMetadataService>();

        using var editorVm = new HeaderEditorToolViewModel(node, metadataService);
        
        var editorView = new HeaderEditorToolView { DataContext = editorVm };

        await editorView.ShowDialog(_mainWindow);

        return editorView.IsSaved ? editorVm.GetUpdatedHeader() : null;
    }
    
    /// <summary>
    /// Mostra il tool di Plate Solving.
    /// </summary>
    public async Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node)
    {
        if (_mainWindow == null) return;

        var solverService = _serviceProvider.GetRequiredService<IPlateSolvingService>();

        var plateVm = new PlateSolvingToolViewModel(node, solverService);
        
        var plateView = new PlateSolvingToolView { DataContext = plateVm };

        await plateView.ShowDialog(_mainWindow);
    }
    
    /// <summary>
    /// Mostra il tool di posterizzazione artistica.
    /// </summary>
    public async Task<List<string>?> ShowPosterizationWindowAsync(
        List<string> sourcePaths, 
        VisualizationMode initialMode)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        var ioService = _serviceProvider.GetRequiredService<IFitsIoService>();
        var postService = _serviceProvider.GetRequiredService<IPosterizationService>();
        var converter = _serviceProvider.GetRequiredService<IFitsImageDataConverter>();
        var analysis = _serviceProvider.GetRequiredService<IImageAnalysisService>();
        
        // NOTA: Se anche PosterizationToolViewModel usa internamente un FitsRenderer,
        // dovrai aggiungere IMediaExportService anche qui. Per ora lascio inalterato
        // assumendo che PosterizationToolViewModel non sia stato ancora aggiornato.

        using var vm = new PosterizationToolViewModel(
            sourcePaths, 
            ioService,
            postService, 
            converter, 
            analysis, 
            initialMode);
            
        var view = new PosterizationToolView { DataContext = vm };

        Action closeHandler = () => view.Close();
        vm.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        vm.RequestClose -= closeHandler;
        return vm.DialogResult ? vm.ResultPaths : null;
    }
}