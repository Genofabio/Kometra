using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using KomaLab.Models.Visualization;
using KomaLab.Services.Astrometry; // Necessario per IJplHorizonsService
using KomaLab.Services.Data;       // IFitsIoService, IFitsMetadataService
using KomaLab.Services.Imaging;
using KomaLab.ViewModels;
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
// 1. Risoluzione delle dipendenze per i tool tramite IServiceProvider.
// 2. Iniezione dei servizi nei ViewModel dei tool (Alignment, Posterization, ecc.).
// 3. Gestione del ciclo di vita delle finestre modali (ShowDialog).
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

        // 1. Risoluzione dei servizi necessari dal container DI
        var ioService = _serviceProvider.GetRequiredService<IFitsIoService>();
        var metadataService = _serviceProvider.GetRequiredService<IFitsMetadataService>(); // NUOVO
        var alignmentService = _serviceProvider.GetRequiredService<IAlignmentService>();
        var converter = _serviceProvider.GetRequiredService<IFitsImageDataConverter>();
        var analysis = _serviceProvider.GetRequiredService<IImageAnalysisService>();
        var jplService = _serviceProvider.GetRequiredService<IJplHorizonsService>(); // NUOVO

        // 2. Iniezione dipendenze nel ViewModel del tool
        using var viewModel = new AlignmentToolViewModel(
            sourcePaths,
            ioService,          // Updated
            metadataService,    // New
            alignmentService,
            converter,
            analysis,
            jplService,         // New
            initialMode
        );
    
        var alignmentWindow = new AlignmentToolView
        {
            DataContext = viewModel
        };

        // 3. Gestione chiusura
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
        
        // HeaderEditorViewModel prende solo il nodo, le dipendenze interne 
        // le recupera dal nodo o sono parser statici.
        var editorVm = new HeaderEditorViewModel(node);
        var editorView = new HeaderEditorView
        {
            DataContext = editorVm
        };

        await editorView.ShowDialog(_mainWindow);

        return editorView.IsSaved ? editorVm.GetUpdatedHeader() : null;
    }
    
    /// <summary>
    /// Mostra il tool di Plate Solving.
    /// </summary>
    public async Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node)
    {
        if (_mainWindow == null) return;

        var plateVm = new PlateSolvingViewModel(node);
        var plateView = new PlateSolvingView
        {
            DataContext = plateVm
        };

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

        // Risoluzione Dipendenze Posterizzazione
        var ioService = _serviceProvider.GetRequiredService<IFitsIoService>();
        var postService = _serviceProvider.GetRequiredService<IPosterizationService>();
        var converter = _serviceProvider.GetRequiredService<IFitsImageDataConverter>();
        var analysis = _serviceProvider.GetRequiredService<IImageAnalysisService>();

        using var vm = new PosterizationToolViewModel(
            sourcePaths, 
            ioService,      // Updated
            postService, 
            converter, 
            analysis, 
            initialMode);
            
        var view = new PosterizationToolView
        {
            DataContext = vm
        };

        Action closeHandler = () => view.Close();
        vm.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        vm.RequestClose -= closeHandler;

        return vm.DialogResult ? vm.ResultPaths : null;
    }
}