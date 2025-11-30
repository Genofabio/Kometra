using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KomaLab.ViewModels;
using KomaLab.Views;
using KomaLab.Services;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace KomaLab;

public class App : Application
{
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Configura tutti i servizi (vecchi e nuovi)
        Services = ConfigureServices();
        
        var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel 
            };
             
            // Manteniamo la tua logica per il WindowService
            var windowService = Services.GetRequiredService<IWindowService>();
            windowService.RegisterMainWindow(desktop.MainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        
        services.AddSingleton<IFitsDataConverter, FitsDataConverter>();
        services.AddSingleton<IImageAnalysisService, ImageAnalysisService>();
        services.AddSingleton<IImageOperationService, ImageOperationService>();
        services.AddSingleton<IFitsService, FitsService>();
        services.AddSingleton<IAlignmentService, AlignmentService>();
        services.AddSingleton<INodeViewModelFactory, NodeViewModelFactory>();
        services.AddSingleton<IDialogService, DialogService>(); 
        services.AddSingleton<IWindowService, WindowService>();
        
        services.AddSingleton<BoardViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}