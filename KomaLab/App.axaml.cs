using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KomaLab.ViewModels;
using KomaLab.Views;
using KomaLab.Services; // <-- Assicurati che ci sia
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
        Services = ConfigureServices();
        var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 1. La finestra principale viene creata
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel 
            };
            
            // 2. Ottieni l'istanza del servizio Finestra
            var windowService = Services.GetRequiredService<IWindowService>();
            
            // 3. REGISTRA la finestra principale nel servizio
            windowService.RegisterMainWindow(desktop.MainWindow);
            
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        
        services.AddSingleton<IFitsService, FitsService>();
        services.AddSingleton<INodeViewModelFactory, NodeViewModelFactory>();
        services.AddSingleton<IDialogService, DialogService>(); 
        services.AddSingleton<IWindowService, WindowService>();

        services.AddSingleton<BoardViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        
        return services.BuildServiceProvider();
    }
}