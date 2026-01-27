using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using KomaLab.Infrastructure;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing.Enhancement;
using KomaLab.Models.Visualization;
using KomaLab.Services.Astrometry;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Astrometry;
using KomaLab.ViewModels.Fits;
using KomaLab.ViewModels.ImageProcessing;
using KomaLab.ViewModels.Nodes;
using KomaLab.Views;
using Microsoft.Extensions.DependencyInjection;
using ImportViewModel = KomaLab.ViewModels.ImportExport.ImportViewModel;
using VideoExportToolViewModel = KomaLab.ViewModels.ImportExport.VideoExportToolViewModel;

namespace KomaLab.Services.UI;

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

        return await ShowDialogAndGetResultAsync(view, viewModel, vm => vm.FinalProcessedPaths);
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

        await ShowDialogAsync(view, viewModel);

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

        await ShowDialogAsync(view, viewModel);
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

        return await ShowDialogAndGetResultAsync(view, viewModel, vm => vm.ResultPaths);
    }
    
    // =======================================================================
    // 5. IMPORTAZIONE
    // =======================================================================
    public async Task<(List<string> Paths, bool SeparateNodes)?> ShowImportWindowAsync()
    {
        if (_mainWindow == null) return null;

        var dialogService = _serviceProvider.GetRequiredService<IDialogService>();
        var coordinator = _serviceProvider.GetRequiredService<ICalibrationCoordinator>();
        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();

        var viewModel = new ImportViewModel(dialogService, coordinator, dataManager);
        var view = new ImportView { DataContext = viewModel };

        // Restituiamo una tupla con i percorsi E il flag booleano
        return await ShowDialogAndGetResultAsync(
            view, 
            viewModel, 
            vm => (vm.CalibratedResultPaths!, vm.ImportAsSeparateNodes) // Nota il ! per ignorare il warning nullable (gestito dal DialogResult true)
        );
    }
    
    // =======================================================================
    // 6-8. ENHANCEMENT HELPERS
    // =======================================================================
    public async Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowRadialEnhancementWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode)
        => await ShowImageEnhancementToolAsync(sourceFiles, EnhancementCategory.RadialRotational);

    public async Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowStructureExtractionWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode)
        => await ShowImageEnhancementToolAsync(sourceFiles, EnhancementCategory.FeatureExtraction);

    public async Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowLocalContrastWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode)
        => await ShowImageEnhancementToolAsync(sourceFiles, EnhancementCategory.LocalContrast);

    private async Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowImageEnhancementToolAsync(List<FitsFileReference> sourceFiles, EnhancementCategory category)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();
        var coordinator = _serviceProvider.GetRequiredService<IImageEnhancementCoordinator>();
        var metadataService = _serviceProvider.GetRequiredService<IFitsMetadataService>();

        using var viewModel = new ImageEnhancementToolViewModel(category, sourceFiles, dataManager, rendererFactory, coordinator, metadataService);
        var view = new ImageEnhancementToolView { DataContext = viewModel };

        return await ShowDialogAndGetResultAsync(view, viewModel, vm => (vm.ResultPaths, vm.SelectedMode));
    }


    // =======================================================================
    // 9. ESPORTAZIONE VIDEO (VERSIONE INTEGRATA)
    // =======================================================================
    public async Task<VideoExportSettings?> ShowVideoExportDialogAsync(
        ImageNodeViewModel node, 
        VisualizationMode currentMode)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        // Recupero di tutti i servizi necessari per l'operazione autonoma del tool
        var formatProvider = _serviceProvider.GetRequiredService<IVideoFormatProvider>();
        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();
        var videoCoordinator = _serviceProvider.GetRequiredService<IVideoExportCoordinator>();
        var dialogService = _serviceProvider.GetRequiredService<IDialogService>();

        // Inizializzazione on-demand dei backend video (FFMPEG/MSMF)
        await formatProvider.InitializeAsync();

        var originalSize = node.ActiveRenderer.ImageSize;
        var sourceFiles = node.CurrentFiles; 

        // Creazione del ViewModel con iniezione dei coordinatori
        using var viewModel = new VideoExportToolViewModel(
            formatProvider, 
            dataManager,
            rendererFactory,
            videoCoordinator, // Iniettato per gestire l'export interno
            dialogService,    // Iniettato per gestire il SaveFileDialog interno
            sourceFiles,
            currentMode, 
            originalSize);

        var view = new VideoExportToolView { DataContext = viewModel };

        // Apertura dialogo. Il controllo passa al ViewModel.
        // Se l'utente preme "Esporta", il VM gestirà: 1. Selezione Path, 2. Export con Progress, 3. Chiusura.
        return await ShowDialogAndGetResultAsync(view, viewModel, vm => vm.GetSettings(vm.OutputPath));
    }

    // =======================================================================
    // HELPERS PER APERTURA DIALOGHI
    // =======================================================================
    
    private async Task ShowDialogAsync<TVm>(Window view, TVm viewModel) where TVm : class
    {
        Action closeHandler = () => view.Close();
        var eventInfo = typeof(TVm).GetEvent("RequestClose");
        if (eventInfo != null) eventInfo.AddEventHandler(viewModel, closeHandler);

        await view.ShowDialog(_mainWindow!);

        if (eventInfo != null) eventInfo.RemoveEventHandler(viewModel, closeHandler);
    }

    private async Task<TReturn?> ShowDialogAndGetResultAsync<TVm, TReturn>(Window view, TVm viewModel, Func<TVm, TReturn> resultSelector) where TVm : class
    {
        await ShowDialogAsync(view, viewModel);
        var propInfo = typeof(TVm).GetProperty("DialogResult");
        bool success = (bool)(propInfo?.GetValue(viewModel) ?? false);
        return success ? resultSelector(viewModel) : default;
    }
}