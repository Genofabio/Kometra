using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Kometra.Infrastructure;
using Kometra.Models.Export;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing.Enhancement;
using Kometra.Models.Visualization;
using Kometra.Services.Astrometry;
using Kometra.Services.Factories;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.ImportExport;
using Kometra.Services.Processing.Coordinators;
using Kometra.ViewModels.Astrometry;
using Kometra.ViewModels.Fits;
using Kometra.ViewModels.ImageProcessing;
using Kometra.ViewModels.ImportExport;
using Kometra.ViewModels.Nodes;
using Kometra.Views;
using Microsoft.Extensions.DependencyInjection;
using ImportViewModel = Kometra.ViewModels.ImportExport.ImportViewModel;
using VideoExportToolViewModel = Kometra.ViewModels.ImportExport.VideoExportToolViewModel;

namespace Kometra.Services.UI;

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

        return await ShowDialogAndGetResultAsync(
            view, 
            viewModel, 
            vm => (vm.CalibratedResultPaths!, vm.ImportAsSeparateNodes) 
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

        var formatProvider = _serviceProvider.GetRequiredService<IVideoFormatProvider>();
        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();
        var videoCoordinator = _serviceProvider.GetRequiredService<IVideoExportCoordinator>();
        var dialogService = _serviceProvider.GetRequiredService<IDialogService>();

        await formatProvider.InitializeAsync();

        var originalSize = node.ActiveRenderer.ImageSize;
        var sourceFiles = node.CurrentFiles; 

        using var viewModel = new VideoExportToolViewModel(
            formatProvider, 
            dataManager,
            rendererFactory,
            videoCoordinator, 
            dialogService,    
            sourceFiles,
            currentMode, 
            originalSize);

        var view = new VideoExportToolView { DataContext = viewModel };

        return await ShowDialogAndGetResultAsync(view, viewModel, vm => vm.GetSettings(vm.OutputPath));
    }
    
    // =======================================================================
    // 10. MASCHERAMENTO STELLE
    // =======================================================================
    public async Task<List<string>?> ShowStarMaskingWindowAsync(List<FitsFileReference> sourceFiles)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        var coordinator = _serviceProvider.GetRequiredService<IMaskingCoordinator>();
        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();

        using var viewModel = new StarMaskingViewModel(sourceFiles, coordinator, dataManager, rendererFactory);
        var view = new StarMaskingView { DataContext = viewModel };

        return await ShowDialogAndGetResultAsync(view, viewModel, vm => vm.ResultPaths);
    }

    // =======================================================================
    // 11. ESPORTAZIONE BATCH (FITS Multi-HDU, PNG, JPG) [AGGIORNATO]
    // =======================================================================
    public async Task ShowExportWindowAsync(IEnumerable<string> filePaths)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        // Risoluzione manuale delle dipendenze (Uniforme agli altri metodi)
        var coordinator = _serviceProvider.GetRequiredService<IExportCoordinator>();
        var dialogService = _serviceProvider.GetRequiredService<IDialogService>();
        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();

        // Istanziazione manuale del ViewModel con passaggio dei dati a runtime
        using var viewModel = new ExportViewModel(
            coordinator, 
            dialogService, 
            dataManager, 
            rendererFactory, 
            filePaths);
            
        var view = new ExportView { DataContext = viewModel };

        await ShowDialogAsync(view, viewModel);
    }
    
    // =======================================================================
    // 12. CROP TOOL (RITAGLIO)
    // =======================================================================
    public async Task<List<string>?> ShowCropToolWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode)
    {
        // 1. Controllo finestra principale
        if (_mainWindow == null) 
            throw new InvalidOperationException("Finestra principale non registrata nel WindowService.");

        // 2. Risoluzione dipendenze (Assicurati di aver registrato ICropCoordinator in App.cs!)
        var coordinator = _serviceProvider.GetRequiredService<ICropCoordinator>();
        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();

        // 3. Creazione ViewModel
        using var viewModel = new CropToolViewModel(sourceFiles, coordinator, dataManager, rendererFactory);
    
        // IMPORTANTE: Passiamo la modalità di visualizzazione (Logaritmica, ecc.)
        viewModel.VisualizationMode = initialMode;

        // 4. Creazione View e assegnazione DataContext
        var view = new CropToolView 
        { 
            DataContext = viewModel 
        };

        // 5. Apertura Finestra Modale
        await view.ShowDialog(_mainWindow);

        // 6. RECUPERO DEL RISULTATO
        // Questo è il punto critico: quando ShowDialog ritorna (finestra chiusa),
        // dobbiamo leggere cosa ha prodotto il ViewModel.
        return viewModel.ResultPaths;
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