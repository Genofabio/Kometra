using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using System;
using KomaLab.Services;
using KomaLab.ViewModels;
using KomaLab.Views;

namespace KomaLab;

public class App : Application
{
    /// <summary>
    /// Contiene tutti i servizi registrati dell'applicazione.
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {

        // 1. Chiama il metodo che configura e costruisce i servizi
        Services = ConfigureServices();

        // 2. Ottiene il MainWindowViewModel dal contenitore
        //    Il contenitore creerà automaticamente TUTTE le dipendenze:
        //    - MainWindowViewModel -> richiede BoardViewModel
        //    - BoardViewModel -> richiede INodeViewModelFactory
        //    - NodeViewModelFactory -> richiede IFitsService
        //    - IFitsService -> viene creato come FitsService
        var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                // 3. Assegna il ViewModel (già pronto) alla finestra
                DataContext = mainViewModel 
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Metodo che crea il contenitore DI e registra tutte le "ricette".
    /// </summary>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // --- Registra i Servizi ---
        services.AddSingleton<IFitsService, FitsService>();
        
        services.AddSingleton<INodeViewModelFactory, NodeViewModelFactory>();

        // --- Registra i ViewModel ---
        // (Registriamo anche i VM principali come Singleton
        // perché mantengono lo stato dell'applicazione)
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<BoardViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        // Costruisce e restituisce il contenitore di servizi finale
        return services.BuildServiceProvider();
    }
}