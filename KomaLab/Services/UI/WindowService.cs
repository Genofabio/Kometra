using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using KomaLab.Infrastructure;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing.Enhancement; // Per EnhancementCategory
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
    public async Task<List<string>?> ShowImportWindowAsync()
    {
        if (_mainWindow == null) return null;

        var dialogService = _serviceProvider.GetRequiredService<IDialogService>();
        var coordinator = _serviceProvider.GetRequiredService<ICalibrationCoordinator>();

        var viewModel = new ImportViewModel(dialogService, coordinator);
        var view = new ImportView { DataContext = viewModel };

        return await ShowDialogAndGetResultAsync(view, viewModel, vm => vm.CalibratedResultPaths);
    }
    
    // =======================================================================
    // 6. ENHANCEMENT: MODELLI RADIALI
    // =======================================================================
    // Cambiamo il tipo di ritorno in una Tupla (Percorsi, Modalità)
    public async Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowRadialEnhancementWindowAsync(
        List<FitsFileReference> sourceFiles,
        VisualizationMode initialMode)
    {
        return await ShowImageEnhancementToolAsync(sourceFiles, EnhancementCategory.RadialRotational);
    }

    // =======================================================================
    // 7. ENHANCEMENT: ESTRAZIONE STRUTTURE
    // =======================================================================
    public async Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowStructureExtractionWindowAsync(
        List<FitsFileReference> sourceFiles,
        VisualizationMode initialMode)
    {
        return await ShowImageEnhancementToolAsync(sourceFiles, EnhancementCategory.FeatureExtraction);
    }

    // =======================================================================
    // 8. ENHANCEMENT: CONTRASTO LOCALE
    // =======================================================================
    public async Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowLocalContrastWindowAsync(
        List<FitsFileReference> sourceFiles,
        VisualizationMode initialMode)
    {
        return await ShowImageEnhancementToolAsync(sourceFiles, EnhancementCategory.LocalContrast);
    }

    // =======================================================================
    // HELPER GENERICO AGGIORNATO
    // =======================================================================
    private async Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowImageEnhancementToolAsync(
        List<FitsFileReference> sourceFiles,
        EnhancementCategory category)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();
        var coordinator = _serviceProvider.GetRequiredService<IImageEnhancementCoordinator>();
        var metadataService = _serviceProvider.GetRequiredService<IFitsMetadataService>();

        using var viewModel = new ImageEnhancementToolViewModel(
            category, 
            sourceFiles, 
            dataManager, 
            rendererFactory, 
            coordinator, 
            metadataService);

        var view = new ImageEnhancementToolView { DataContext = viewModel };

        // ORA RESTITUIAMO SIA I PATH CHE LA MODALITÀ SELEZIONATA DAL VM
        return await ShowDialogAndGetResultAsync(
            view, 
            viewModel, 
            vm => (vm.ResultPaths, vm.SelectedMode) // <--- Estrazione Tupla
        );
    }


    // =======================================================================
    // HELPER PER APERTURA DIALOGHI (Riduce duplicazione codice)
    // =======================================================================
    
    private async Task ShowDialogAsync<TVm>(Window view, TVm viewModel) 
        where TVm : class
    {
        // Pattern standard: Subscribe -> ShowDialog -> Unsubscribe
        Action closeHandler = () => view.Close();
        
        // Reflection per trovare l'evento RequestClose (comune a tutti i tuoi VM)
        var eventInfo = typeof(TVm).GetEvent("RequestClose");
        if (eventInfo != null)
            eventInfo.AddEventHandler(viewModel, closeHandler);

        await view.ShowDialog(_mainWindow!);

        if (eventInfo != null)
            eventInfo.RemoveEventHandler(viewModel, closeHandler);
    }

    private async Task<TReturn?> ShowDialogAndGetResultAsync<TVm, TReturn>(
        Window view, 
        TVm viewModel, 
        Func<TVm, TReturn> resultSelector) 
        where TVm : class
    {
        await ShowDialogAsync(view, viewModel);

        // Reflection per leggere DialogResult
        var propInfo = typeof(TVm).GetProperty("DialogResult");
        bool success = (bool)(propInfo?.GetValue(viewModel) ?? false);

        return success ? resultSelector(viewModel) : default;
    }
    
    // =======================================================================
    // 9. ESPORTAZIONE VIDEO (VERSIONE FINALE)
    // =======================================================================
    public async Task<VideoExportSettings?> ShowVideoExportDialogAsync(
        ImageNodeViewModel node, // Passiamo il nodo per estrarre dimensioni e conteggio frame
        VisualizationMode currentMode)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        var formatProvider = _serviceProvider.GetRequiredService<IVideoFormatProvider>();
        
        // 1. INIZIALIZZAZIONE "ON-DEMAND"
        // Esegue i test dei codec solo alla prima chiamata, garantendo che la finestra
        // mostri solo opzioni realmente funzionanti sul sistema.
        await formatProvider.InitializeAsync();

        // 2. Recupero dati necessari dal nodo
        // Estraiamo la dimensione originale dall'ultimo renderer attivo e il numero di file totali
        var originalSize = node.ActiveRenderer.ImageSize;
        int totalFrames = node.CurrentFiles.Count;
        string defaultFileName = node.ActiveFile?.FileName ?? "VideoExport";

        // 3. Setup del ViewModel
        // Passiamo le dimensioni originali per il calcolo della risoluzione finale (FinalWidth/Height)
        // e il numero di frame per il calcolo della durata (DurationText).
        using var viewModel = new VideoExportToolViewModel(
            formatProvider, 
            currentMode, 
            originalSize, 
            totalFrames);

        var view = new VideoExportToolView { DataContext = viewModel };

        // Mostra il dialogo stile KomaLab
        await ShowDialogAsync(view, viewModel);

        if (viewModel.DialogResult)
        {
            var dialogService = _serviceProvider.GetRequiredService<IDialogService>();
            
            // Recuperiamo l'estensione corretta in base al container selezionato (es. .mp4)
            string extension = formatProvider.GetExtension(viewModel.SelectedContainer);
            string filterName = $"{viewModel.SelectedContainer} Video";

            // Chiediamo all'utente dove salvare il file
            var outputPath = await dialogService.ShowSaveFileDialogAsync(
                $"{defaultFileName}{extension}", 
                filterName, 
                $"*{extension}");

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                // Restituiamo le impostazioni pronte per il VideoExportCoordinator
                return viewModel.GetSettings(outputPath);
            }
        }

        return null;
    }
    
    
}