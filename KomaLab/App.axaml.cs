using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using KomaLab.Infrastructure;
using KomaLab.Services.Astrometry;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.IO;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.ImportExport;
using KomaLab.Services.Processing.Alignment;
using KomaLab.Services.Processing.Batch;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.Services.Processing.Engines;
using KomaLab.Services.Processing.Engines.Enhancement;
using KomaLab.Services.Processing.Rendering;
using KomaLab.Services.UI;
using KomaLab.Services.Undo;
using KomaLab.ViewModels;
using KomaLab.ViewModels.Astrometry;
using KomaLab.ViewModels.Fits;
using KomaLab.ViewModels.ImageProcessing;
using KomaLab.ViewModels.ImportExport;
using KomaLab.Views;

namespace KomaLab;

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
        services.AddSingleton<IStackingCoordinator, StackingCoordinator>();
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
        // NOTA: I ViewModel dei Tool (Export, Alignment, HeaderEditor, ecc.) 
        // NON sono registrati qui perché il WindowService li istanzia manualmente 
        // tramite 'new' per iniettare i dati di runtime (es. filePaths).
        
        return services.BuildServiceProvider();
    }
    
    private void CleanUpOrphanedTempFiles()
    {
        try
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "Komalab");
            if (Directory.Exists(tempRoot)) 
            {
                Directory.Delete(tempRoot, true);
            }
        }
        catch { /* Silenzioso se i file sono bloccati */ }
    }
}