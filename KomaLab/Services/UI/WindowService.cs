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
// DESCRIZIONE:
// Implementazione concreta del servizio finestre per Avalonia.
// Responsabilità:
// 1. Risoluzione delle dipendenze per i tool tramite IServiceProvider (DI).
// 2. Iniezione dei servizi nei ViewModel dei tool (Alignment, Posterization, ecc.).
// 3. Gestione del ciclo di vita delle finestre modali e pulizia della memoria (Dispose).
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

        var ioService = _serviceProvider.GetRequiredService<IFitsIoService>();
        var metadataService = _serviceProvider.GetRequiredService<IFitsMetadataService>();
        var alignmentService = _serviceProvider.GetRequiredService<IAlignmentService>();
        var converter = _serviceProvider.GetRequiredService<IFitsImageDataConverter>();
        var analysis = _serviceProvider.GetRequiredService<IImageAnalysisService>();
        var jplService = _serviceProvider.GetRequiredService<IJplHorizonsService>();

        using var viewModel = new AlignmentToolViewModel(
            sourcePaths,
            ioService,
            metadataService,
            alignmentService,
            converter,
            analysis,
            jplService,
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
        
        // Risoluzione servizio metadati per l'editor
        var metadataService = _serviceProvider.GetRequiredService<IFitsMetadataService>();

        // Usiamo 'using' perché HeaderEditorToolViewModel è IDisposable
        using var editorVm = new HeaderEditorToolViewModel(node, metadataService);
        
        var editorView = new HeaderEditorToolView { DataContext = editorVm };

        await editorView.ShowDialog(_mainWindow);

        // Se l'utente ha salvato (proprietà IsSaved nella View), restituiamo l'header aggiornato
        return editorView.IsSaved ? editorVm.GetUpdatedHeader() : null;
    }
    
    /// <summary>
    /// Mostra il tool di Plate Solving.
    /// </summary>
    public async Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node)
    {
        if (_mainWindow == null) return;

        // Risoluzione servizio astrometrico
        var solverService = _serviceProvider.GetRequiredService<IPlateSolvingService>();

        // PlateSolvingToolViewModel non è ancora IDisposable ma seguiamo la pratica della DI
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

        // Usiamo 'using' per smaltire le matrici OpenCV nel ViewModel una volta chiusa la finestra
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