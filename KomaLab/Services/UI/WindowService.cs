using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Visualization;
using KomaLab.Services.Astrometry;
using KomaLab.Services.Fits;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Fits;
using KomaLab.ViewModels.ImageProcessing;
using KomaLab.ViewModels.Nodes;
using KomaLab.Views;
using Microsoft.Extensions.DependencyInjection;

// Alias per evitare ambiguità
using AlignmentToolViewModel = KomaLab.ViewModels.ImageProcessing.AlignmentToolViewModel;
using HeaderEditorToolViewModel = KomaLab.ViewModels.Fits.HeaderEditorToolViewModel;
using PlateSolvingToolViewModel = KomaLab.ViewModels.Astrometry.PlateSolvingToolViewModel;
using PosterizationToolViewModel = KomaLab.ViewModels.ImageProcessing.PosterizationToolViewModel;
using RadialEnhancementToolViewModel = KomaLab.ViewModels.ImageProcessing.RadialEnhancementToolViewModel;
using StructureExtractionToolViewModel = KomaLab.ViewModels.ImageProcessing.StructureExtractionToolViewModel;

namespace KomaLab.Services.UI;

/// <summary>
/// Orchestratore delle Finestre. 
/// Inizializza i ToolViewModel iniettando i Coordinatori e gestendo la chiusura delle View.
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
        List<FitsFileReference> sourceFiles,
        VisualizationMode initialMode = VisualizationMode.Linear) 
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        var coordinator = _serviceProvider.GetRequiredService<IAlignmentCoordinator>();
        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();

        using var viewModel = new AlignmentToolViewModel(sourceFiles, coordinator, dataManager, rendererFactory);
        var view = new AlignmentToolView { DataContext = viewModel };

        Action closeHandler = () => view.Close();
        viewModel.RequestClose += closeHandler;

        await view.ShowDialog<object>(_mainWindow); 

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
    
        var coordinator = _serviceProvider.GetRequiredService<IHeaderEditorCoordinator>();
        var healthEvaluator = _serviceProvider.GetRequiredService<IFitsHeaderHealthEvaluator>();
        var mapper = _serviceProvider.GetRequiredService<FitsHeaderUiMapper>();

        using var viewModel = new HeaderEditorToolViewModel(files, navigator, coordinator, healthEvaluator, mapper);
        var view = new HeaderEditorToolView { DataContext = viewModel };

        Action closeHandler = () => view.Close();
        viewModel.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        viewModel.RequestClose -= closeHandler;

        return navigator.CurrentIndex < files.Count ? files[navigator.CurrentIndex].ModifiedHeader : null;
    }
    
    // =======================================================================
    // 3. PLATE SOLVING
    // =======================================================================

    public async Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node)
    {
        if (_mainWindow == null) return;

        var coordinator = _serviceProvider.GetRequiredService<IPlateSolvingCoordinator>();
        var metadataService = _serviceProvider.GetRequiredService<IFitsMetadataService>();
    
        string targetName = node.ActiveFile?.FileName ?? "Sorgente Ignota";

        using var viewModel = new PlateSolvingToolViewModel(node.CurrentFiles, targetName, coordinator, metadataService);
        var view = new PlateSolvingToolView { DataContext = viewModel };

        Action closeHandler = () => view.Close();
        viewModel.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        viewModel.RequestClose -= closeHandler;
    }
    
    // =======================================================================
    // 4. POSTERIZZAZIONE
    // =======================================================================

    public async Task<List<string>?> ShowPosterizationWindowAsync(
        List<FitsFileReference> sourceFiles,
        VisualizationMode initialMode)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();
        var coordinator = _serviceProvider.GetRequiredService<IPosterizationCoordinator>();
        
        using var viewModel = new PosterizationToolViewModel(sourceFiles, dataManager, rendererFactory, coordinator);
        var view = new PosterizationToolView { DataContext = viewModel };

        Action closeHandler = () => view.Close();
        viewModel.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        viewModel.RequestClose -= closeHandler;
        return viewModel.DialogResult ? viewModel.ResultPaths : null;
    }
    
    // =======================================================================
    // 5. IMPORTAZIONE
    // =======================================================================

    public async Task<List<string>?> ShowImportWindowAsync()
    {
        if (_mainWindow == null) return null;

        var dialogService = _serviceProvider.GetRequiredService<IDialogService>();
        var coordinator = _serviceProvider.GetRequiredService<ICalibrationCoordinator>();

        var viewModel = new ImportViewModel(dialogService, coordinator);
        var view = new ImportView { DataContext = viewModel };

        Action closeHandler = () => view.Close();
        viewModel.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        viewModel.RequestClose -= closeHandler;
        return viewModel.DialogResult ? viewModel.CalibratedResultPaths : null;
    }
    
    // =======================================================================
    // 6. MODELLI RADIALI
    // =======================================================================

    public async Task<List<string>?> ShowRadialEnhancementWindowAsync(
        List<FitsFileReference> sourceFiles,
        VisualizationMode initialMode)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();
        var coordinator = _serviceProvider.GetRequiredService<IRadialEnhancementCoordinator>();

        using var viewModel = new RadialEnhancementToolViewModel(sourceFiles, dataManager, rendererFactory, coordinator);
        var view = new RadialEnhancementToolView { DataContext = viewModel };

        Action closeHandler = () => view.Close();
        viewModel.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        viewModel.RequestClose -= closeHandler;
        return viewModel.DialogResult ? viewModel.ResultPaths : null;
    }

    // =======================================================================
    // 7. ESTRAZIONE STRUTTURE (Larson-Sekanina / RVSF)
    // =======================================================================

    public async Task<List<string>?> ShowStructureExtractionWindowAsync(
        List<FitsFileReference> sourceFiles,
        VisualizationMode initialMode)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        // 1. Risoluzione dipendenze specifiche
        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();
        var coordinator = _serviceProvider.GetRequiredService<IStructureExtractionCoordinator>();
        
        // AGGIUNTO: MetadataService per gestire il ridimensionamento header nel Mosaico
        var metadataService = _serviceProvider.GetRequiredService<IFitsMetadataService>();

        // 2. Inizializzazione
        using var viewModel = new StructureExtractionToolViewModel(
            sourceFiles, 
            dataManager, 
            rendererFactory, 
            coordinator, 
            metadataService); // <--- Iniettato qui

        var view = new StructureExtractionToolView { DataContext = viewModel };

        // 3. Gestione chiusura e risultati
        Action closeHandler = () => view.Close();
        viewModel.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        viewModel.RequestClose -= closeHandler;
        return viewModel.DialogResult ? viewModel.ResultPaths : null;
    }
}