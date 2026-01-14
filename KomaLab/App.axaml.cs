using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KomaLab.ViewModels;
using KomaLab.Views;
using Microsoft.Extensions.DependencyInjection;
using System;
using KomaLab.Services.Astrometry;
using KomaLab.Services.Factories; // Namespace Factories
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using KomaLab.Services.UI;
using KomaLab.Services.Undo;

namespace KomaLab;

public class App : Application
{
    // Proprietà statica per l'accesso al container (se necessario in casi legacy/view)
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        CleanUpOrphanedTempFiles();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 1. Configurazione DI Container
        Services = ConfigureServices();
        
        // 2. Risoluzione del ViewModel principale
        var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();

        // 3. Setup della Finestra Principale (Desktop)
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel 
            };
             
            // Registrazione dell'istanza della finestra nel WindowService
            var windowService = Services.GetRequiredService<IWindowService>();
            windowService.RegisterMainWindow(desktop.MainWindow);
            
            // Cleanup alla chiusura
            desktop.Exit += (_, _) => CleanUpOrphanedTempFiles();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        
        // --- 1. Client HTTP (NASA JPL) ---
        services.AddHttpClient<IJplHorizonsService, JplHorizonsService>(client => 
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "KomaLab/1.0");
        });
        
        // --- 2. Infrastruttura Base ---
        services.AddSingleton<IFileStreamProvider, AvaloniaAwareStreamProvider>();
        services.AddSingleton<IDialogService, DialogService>(); 
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IUndoService, UndoService>();

        // --- 3. Servizi Dati (IO & Metadati) ---
        services.AddSingleton<KomaLab.Services.Fits.Engine.FitsReader>();
        services.AddSingleton<IFitsIoService, FitsIoService>();
        services.AddSingleton<IFitsMetadataService, FitsMetadataService>();
        services.AddSingleton<IFitsImageDataConverter, FitsImageDataConverter>();

        // --- 4. Servizi Imaging & Processing ---
        services.AddSingleton<IImageAnalysisService, ImageAnalysisService>();
        services.AddSingleton<IImageOperationService, ImageOperationService>();
        services.AddSingleton<IMediaExportService, MediaExportService>();
        services.AddSingleton<IPosterizationService, PosterizationService>();
        services.AddSingleton<IPlateSolvingService, PlateSolvingService>();
        services.AddSingleton<IAlignmentService, AlignmentService>();

        // --- 5. Factories ---
        services.AddSingleton<INodeViewModelFactory, NodeViewModelFactory>();
        services.AddSingleton<IFitsRendererFactory, FitsRendererFactory>();
        
        // --- 6. ViewModels ---
        services.AddSingleton<BoardViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
    
    private void CleanUpOrphanedTempFiles()
    {
        try
        {
            string tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Komalab");
            if (System.IO.Directory.Exists(tempRoot))
            {
                System.IO.Directory.Delete(tempRoot, true);
            }
        }
        catch 
        { 
            // Ignora errori di file in uso
        }
    }
}