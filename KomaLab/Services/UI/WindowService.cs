using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Fits;
using KomaLab.ViewModels.Nodes;
using KomaLab.Views;
using Microsoft.Extensions.DependencyInjection;
using AlignmentToolViewModel = KomaLab.ViewModels.ImageProcessing.AlignmentToolViewModel;
using HeaderEditorToolViewModel = KomaLab.ViewModels.Fits.HeaderEditorToolViewModel;
using PlateSolvingToolViewModel = KomaLab.ViewModels.Astrometry.PlateSolvingToolViewModel;
using PosterizationToolViewModel = KomaLab.ViewModels.ImageProcessing.PosterizationToolViewModel;

namespace KomaLab.Services.UI;

/// <summary>
/// Orchestratore delle Finestre. 
/// Risolve i Coordinatori e i DataManager per inizializzare i ToolViewModel.
/// </summary>
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

    // =======================================================================
    // 1. TOOL DI ALLINEAMENTO
    // =======================================================================

    public async Task<List<string>?> ShowAlignmentWindowAsync(
        List<string> sourcePaths, 
        VisualizationMode initialMode = VisualizationMode.Linear) 
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        // Risoluzione Dipendenze per AlignmentToolViewModel
        var coordinator = _serviceProvider.GetRequiredService<IAlignmentCoordinator>();
        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();

        using var viewModel = new AlignmentToolViewModel(
            sourcePaths,
            coordinator,
            dataManager,
            rendererFactory
        );
    
        var alignmentWindow = new AlignmentToolView { DataContext = viewModel };

        Action closeHandler = () => alignmentWindow.Close();
        viewModel.RequestClose += closeHandler;

        await alignmentWindow.ShowDialog<object>(_mainWindow); 

        viewModel.RequestClose -= closeHandler;
        return viewModel.DialogResult ? viewModel.FinalProcessedPaths : null; 
    }
    
    // =======================================================================
    // 2. EDITOR DELL'HEADER
    // =======================================================================

    public async Task<FitsHeader?> ShowHeaderEditorAsync(
        IReadOnlyList<FitsFileReference> files, 
        IImageNavigator navigator)
    {
        if (_mainWindow == null) return null;
        
        // Risoluzione dipendenze per l'editor
        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var healthEvaluator = _serviceProvider.GetRequiredService<IFitsHeaderHealthEvaluator>();
        var mapper = _serviceProvider.GetRequiredService<FitsHeaderUiMapper>();

        // Usiamo il costruttore a 5 argomenti di HeaderEditorToolViewModel
        using var editorVm = new HeaderEditorToolViewModel(
            files, 
            navigator, 
            dataManager, 
            healthEvaluator, 
            mapper);
        
        var editorView = new HeaderEditorToolView { DataContext = editorVm };

        await editorView.ShowDialog(_mainWindow);

        // Poiché l'editor modifica i file in-place, restituiamo l'header del file attivo nel navigatore
        return navigator.CurrentIndex < files.Count ? files[navigator.CurrentIndex].ModifiedHeader : null;
    }
    
    // =======================================================================
    // 3. PLATE SOLVING
    // =======================================================================

    public async Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node)
    {
        if (_mainWindow == null) return;

        // Risoluzione del nuovo AstrometryCoordinator
        var astrometryCoordinator = _serviceProvider.GetRequiredService<IAstrometryCoordinator>();
        
        // Deriviamo il nome target dai metadati del file attivo se disponibile
        string targetName = node.ActiveFile?.FileName ?? "Sorgente Ignota";

        // Usiamo il costruttore a 3 argomenti: (files, targetName, coordinator)
        var plateVm = new PlateSolvingToolViewModel(
            node.CurrentFiles, 
            targetName, 
            astrometryCoordinator);
        
        var plateView = new PlateSolvingToolView { DataContext = plateVm };

        await plateView.ShowDialog(_mainWindow);
    }
    
    // =======================================================================
    // 4. POSTERIZZAZIONE
    // =======================================================================

    public async Task<List<string>?> ShowPosterizationWindowAsync(
        List<string> sourcePaths, 
        VisualizationMode initialMode)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        // Risoluzione Dipendenze per PosterizationToolViewModel
        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();
        var coordinator = _serviceProvider.GetRequiredService<IPosterizationCoordinator>();
        
        using var vm = new PosterizationToolViewModel(
            sourcePaths, 
            dataManager, 
            rendererFactory, 
            coordinator);
            
        var view = new PosterizationToolView { DataContext = vm };

        Action closeHandler = () => view.Close();
        vm.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        vm.RequestClose -= closeHandler;
        return vm.DialogResult ? vm.ResultPaths : null;
    }
}