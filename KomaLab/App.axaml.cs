using System;
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
using KomaLab.Services.Processing.Alignment;
using KomaLab.Services.Processing.Batch;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.Services.Processing.Engines;
using KomaLab.Services.Processing.Rendering;
using KomaLab.Services.UI;
using KomaLab.Services.Undo;
using KomaLab.ViewModels;
using KomaLab.ViewModels.Fits;
using KomaLab.Views;
using AlignmentToolViewModel = KomaLab.ViewModels.ImageProcessing.AlignmentToolViewModel;
using HeaderEditorToolViewModel = KomaLab.ViewModels.Fits.HeaderEditorToolViewModel;
using PlateSolvingToolViewModel = KomaLab.ViewModels.Astrometry.PlateSolvingToolViewModel;
using PosterizationToolViewModel = KomaLab.ViewModels.ImageProcessing.PosterizationToolViewModel;

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

        // --- 1. Infrastruttura ---
        services.AddSingleton<LocalFileStreamProvider>();
        services.AddSingleton<AvaloniaAssetStreamProvider>();
        services.AddSingleton<IFileStreamProvider, FileStreamResolver>();

        services.AddSingleton<IDialogService, DialogService>(); 
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IUndoService, UndoService>();

        // --- 2. Dati & Metadata (Core) ---
        services.AddSingleton<FitsReader>();
        services.AddSingleton<FitsWriter>();
        services.AddSingleton<IFitsIoService, FitsIoService>();
        services.AddSingleton<IFitsMetadataService, FitsMetadataService>();
        services.AddSingleton<IFitsDataManager, FitsDataManager>(); 
        services.AddSingleton<IFitsOpenCvConverter, FitsOpenCvConverter>();
        
        // --- FIX: Registrazione Mancante ---
        services.AddSingleton<IFitsHeaderHealthEvaluator, FitsHeaderHealthEvaluator>();

        // --- 3. Engine Scientifici ---
        services.AddSingleton<IStackingEngine, StackingEngine>();
        services.AddSingleton<IRadiometryEngine, RadiometryEngine>();
        services.AddSingleton<IImageEffectsEngine, ImageEffectsEngine>();
        services.AddSingleton<IImageAnalysisEngine, ImageAnalysisEngine>();
        services.AddSingleton<IGeometricEngine, GeometricEngine>(); 
        services.AddSingleton<IImagePresentationService, ImagePresentationService>();

        // --- 4. Servizi di Dominio ---
        services.AddSingleton<IPlateSolvingService, PlateSolvingService>();
        services.AddSingleton<IAlignmentService, AlignmentService>(); 
        services.AddSingleton<IBatchProcessingService, BatchProcessingService>(); 
        services.AddSingleton<IJplHorizonsService, JplHorizonsService>();

        // --- 5. Coordinatori ---
        services.AddSingleton<IAstrometryCoordinator, AstrometryCoordinator>();
        services.AddSingleton<IPosterizationCoordinator, PosterizationCoordinator>();
        services.AddSingleton<IAlignmentCoordinator, AlignmentCoordinator>(); 
        services.AddSingleton<IVideoExportCoordinator, VideoExportCoordinator>();
        services.AddSingleton<IStackingCoordinator, StackingCoordinator>();

        // --- 6. Factories ---
        services.AddSingleton<INodeViewModelFactory, NodeViewModelFactory>();
        services.AddSingleton<IFitsRendererFactory, FitsRendererFactory>();
        services.AddSingleton<FitsHeaderUiMapper>();
        
        // --- 7. ViewModels Principali (Singleton) ---
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<BoardViewModel>();

        // --- 8. Tool ViewModels (Transient) ---
        services.AddTransient<HeaderEditorToolViewModel>();
        services.AddTransient<PosterizationToolViewModel>();
        services.AddTransient<PlateSolvingToolViewModel>();
        services.AddTransient<AlignmentToolViewModel>();

        return services.BuildServiceProvider();
    }
    
    private void CleanUpOrphanedTempFiles()
    {
        try
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "Komalab");
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
        catch { /* File in uso o già eliminati */ }
    }
}