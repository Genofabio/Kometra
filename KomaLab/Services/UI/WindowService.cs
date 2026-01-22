using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Visualization;
using KomaLab.Services.Astrometry;
using KomaLab.Services.Fits;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Fits;
using KomaLab.ViewModels.Nodes;
using KomaLab.Views;
using Microsoft.Extensions.DependencyInjection;
using AlignmentToolViewModel = KomaLab.ViewModels.ImageProcessing.AlignmentToolViewModel;
using HeaderEditorToolViewModel = KomaLab.ViewModels.Fits.HeaderEditorToolViewModel;
using PlateSolvingToolViewModel = KomaLab.ViewModels.Astrometry.PlateSolvingToolViewModel;
using PosterizationToolViewModel = KomaLab.ViewModels.ImageProcessing.PosterizationToolViewModel;

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
        List<FitsFileReference> sourceFiles, // MODIFICATO: Da string a FitsFileReference
        VisualizationMode initialMode = VisualizationMode.Linear) 
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        var coordinator = _serviceProvider.GetRequiredService<IAlignmentCoordinator>();
        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();

        // Ora i tipi corrispondono: List<FitsFileReference>
        using var viewModel = new AlignmentToolViewModel(sourceFiles, coordinator, dataManager, rendererFactory);
        var view = new AlignmentToolView { DataContext = viewModel };

        Action closeHandler = () => view.Close();
        viewModel.RequestClose += closeHandler;

        await view.ShowDialog<object>(_mainWindow); 

        viewModel.RequestClose -= closeHandler;
        return viewModel.DialogResult ? viewModel.FinalProcessedPaths : null; 
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

        // Pattern Consistente: 'using' per gestire il Dispose (Rollback sessione se non salvato)
        using var viewModel = new HeaderEditorToolViewModel(files, navigator, coordinator, healthEvaluator, mapper);
        var view = new HeaderEditorToolView { DataContext = viewModel };

        // Anche qui aggiungiamo il closeHandler se il ViewModel lo supporta
        Action closeHandler = () => view.Close();
        viewModel.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        viewModel.RequestClose -= closeHandler;

        return navigator.CurrentIndex < files.Count ? files[navigator.CurrentIndex].ModifiedHeader : null;
    }
    
    // =======================================================================
// 3. PLATE SOLVING (Uniformato e Purista)
// =======================================================================

    public async Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node)
    {
        if (_mainWindow == null) return;

        // Recuperiamo le dipendenze necessarie dal ServiceProvider
        var coordinator = _serviceProvider.GetRequiredService<IPlateSolvingCoordinator>();
        var metadataService = _serviceProvider.GetRequiredService<IFitsMetadataService>(); // <--- AGGIUNTO
    
        string targetName = node.ActiveFile?.FileName ?? "Sorgente Ignota";

        // Iniezione delle dipendenze nel ViewModel
        // Note: Passiamo il metadataService affinché il VM possa formattare i dati WCS
        using var viewModel = new PlateSolvingToolViewModel(
            node.CurrentFiles, 
            targetName, 
            coordinator, 
            metadataService); // <--- INIETTATO

        var view = new PlateSolvingToolView { DataContext = viewModel };

        // Gestione della chiusura tramite evento
        Action closeHandler = () => view.Close();
        viewModel.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        // Cleanup dell'evento per evitare memory leak
        viewModel.RequestClose -= closeHandler;
    
        // NOTA: Poiché usiamo 'using', all'uscita dal metodo verrà chiamato viewModel.Dispose().
        // Questo a sua volta chiamerà _coordinator.ClearSession(), pulendo i file temporanei 
        // e gli header pendenti se l'utente ha chiuso la finestra senza fare "Applica".
    }
    
    // =======================================================================
    // 4. POSTERIZZAZIONE
    // =======================================================================

    public async Task<List<string>?> ShowPosterizationWindowAsync(
        List<FitsFileReference> sourceFiles, // MODIFICATO: Da string a FitsFileReference
        VisualizationMode initialMode)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        var dataManager = _serviceProvider.GetRequiredService<IFitsDataManager>();
        var rendererFactory = _serviceProvider.GetRequiredService<IFitsRendererFactory>();
        var coordinator = _serviceProvider.GetRequiredService<IPosterizationCoordinator>();
        
        // Ora i tipi corrispondono: List<FitsFileReference>
        using var viewModel = new PosterizationToolViewModel(sourceFiles, dataManager, rendererFactory, coordinator);
        var view = new PosterizationToolView { DataContext = viewModel };

        Action closeHandler = () => view.Close();
        viewModel.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        viewModel.RequestClose -= closeHandler;
        return viewModel.DialogResult ? viewModel.ResultPaths : null;
    }
}