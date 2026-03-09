using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Kometra.Infrastructure;
using Kometra.Services;
using Kometra.Services.Astrometry;
using Kometra.Services.Factories;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Fits.IO;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.ImportExport;
using Kometra.Services.Processing.Alignment;
using Kometra.Services.Processing.Batch;
using Kometra.Services.Processing.Coordinators;
using Kometra.Services.Processing.Engines;
using Kometra.Services.Processing.Engines.Enhancement;
using Kometra.Services.Processing.Rendering;
using Kometra.Services.UI;
using Kometra.Services.Undo;
using Kometra.ViewModels;
using Kometra.ViewModels.Fits;
using Kometra.Views;

namespace Kometra;

public class App : Application
{
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        CleanUpOrphanedTempFiles();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = ConfigureServices();
        
        // --- INIZIALIZZAZIONE IMPOSTAZIONI ---
        // Recuperiamo il servizio di configurazione e applichiamo la lingua salvata
        var configService = Services.GetRequiredService<IConfigurationService>();
        LocalizationManager.Instance.SetLanguage(configService.Current.Language);
        ThemeService.ApplyPrimaryColor(configService.Current.PrimarySelectionColor);

        var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = mainViewModel };
            
            var windowService = Services.GetRequiredService<IWindowService>();
            windowService.RegisterMainWindow(desktop.MainWindow);
            
            desktop.Exit += (_, _) => CleanUpOrphanedTempFiles();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        
        // --- 0. Supporto Microsoft Extensions e Rete ---
        services.AddMemoryCache(); 
        services.AddHttpClient(); 

        // --- 1. Infrastruttura & I/O di Base ---
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<LocalFileStreamProvider>();
        services.AddSingleton<AvaloniaAssetStreamProvider>();
        services.AddSingleton<IFileStreamProvider, FileStreamResolver>();
        services.AddSingleton<IDialogService, DialogService>(); 
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IUndoService, UndoService>();

        // --- 2. Dati & Metadata (Core FITS) ---
        services.AddSingleton<FitsReader>();
        services.AddSingleton<FitsWriter>();
        services.AddSingleton<IFitsIoService, FitsIoService>();
        services.AddSingleton<IFitsMetadataService, FitsMetadataService>();
        services.AddSingleton<IFitsDataManager, FitsDataManager>(); 
        services.AddSingleton<IFitsOpenCvConverter, FitsOpenCvConverter>();
        services.AddSingleton<IFitsHeaderHealthEvaluator, FitsHeaderHealthEvaluator>();

        // --- 3. Engine Scientifici & Rendering ---
        services.AddSingleton<IStackingEngine, StackingEngine>();
        services.AddSingleton<IRadiometryEngine, RadiometryEngine>();
        services.AddSingleton<IImageEffectsEngine, ImageEffectsEngine>();
        services.AddSingleton<IImageAnalysisEngine, ImageAnalysisEngine>();
        services.AddSingleton<IGeometricEngine, GeometricEngine>(); 
        services.AddSingleton<IArithmeticEngine, ArithmeticEngine>(); // <-- AGGIUNTO
        services.AddSingleton<IImagePresentationService, ImagePresentationService>();
        services.AddSingleton<ICalibrationEngine, CalibrationEngine>();
        services.AddSingleton<IGradientRadialEngine, GradientRadialEngine>();
        services.AddSingleton<ILocalContrastEngine, LocalContrastEngine>();
        services.AddSingleton<IStructureShapeEngine, StructureShapeEngine>();
        services.AddSingleton<ISegmentationEngine, SegmentationEngine>();
        services.AddSingleton<IInpaintingEngine, InpaintingEngine>();

        // --- 4. Servizi di Dominio & Multimedia ---
        services.AddSingleton<IPlateSolvingService, PlateSolvingService>();
        services.AddSingleton<IAlignmentService, AlignmentService>(); 
        services.AddSingleton<IBatchProcessingService, BatchProcessingService>(); 
        services.AddSingleton<IJplHorizonsService, JplHorizonsService>();
        
        // Infrastruttura Video e Export
        services.AddSingleton<IVideoFormatProvider, VideoFormatProvider>();
        services.AddTransient<IVideoEncoder, OpenCvVideoEncoder>();
        services.AddSingleton<IBitmapExportService, BitmapExportService>(); 

        // --- 5. Coordinatori (Orchestratori) ---
        services.AddSingleton<IPlateSolvingCoordinator, PlateSolvingCoordinator>();
        services.AddSingleton<IHeaderEditorCoordinator, HeaderEditorCoordinator>(); 
        services.AddSingleton<IPosterizationCoordinator, PosterizationCoordinator>();
        services.AddSingleton<IAlignmentCoordinator, AlignmentCoordinator>(); 
        services.AddSingleton<ICropCoordinator, CropCoordinator>();
        services.AddSingleton<IStackingCoordinator, StackingCoordinator>();
        services.AddSingleton<IArithmeticCoordinator, ArithmeticCoordinator>(); // <-- AGGIUNTO
        services.AddSingleton<ICalibrationCoordinator, CalibrationCoordinator>();
        services.AddSingleton<IImageEnhancementCoordinator, ImageEnhancementCoordinator>();
        services.AddSingleton<IMaskingCoordinator, MaskingCoordinator>();
        services.AddSingleton<IVideoExportCoordinator, VideoExportCoordinator>();
        services.AddSingleton<IExportCoordinator, ExportCoordinator>();

        // --- 6. Factories ---
        services.AddSingleton<INodeViewModelFactory, NodeViewModelFactory>();
        services.AddSingleton<IFitsRendererFactory, FitsRendererFactory>();
        services.AddSingleton<FitsHeaderUiMapper>();
        
        // --- 7. ViewModels Principali (Risolti via DI) ---
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<BoardViewModel>();

        // --- 8. Tool ViewModels ---
        // I ViewModel dei Tool vengono istanziati manualmente nel WindowService
        
        return services.BuildServiceProvider();
    }
    
    private void CleanUpOrphanedTempFiles()
    {
        try
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "Kometra");
            if (Directory.Exists(tempRoot)) 
            {
                Directory.Delete(tempRoot, true);
            }
        }
        catch { /* Silenzioso se i file sono bloccati */ }
    }
}