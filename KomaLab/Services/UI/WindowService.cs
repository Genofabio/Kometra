using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using KomaLab.Models;
using KomaLab.Models.Visualization; // <--- NECESSARIO PER VisualizationMode
using KomaLab.Services.Data;
using KomaLab.Services.Imaging;
using KomaLab.ViewModels;
using KomaLab.Views;
using Microsoft.Extensions.DependencyInjection;

namespace KomaLab.Services.UI;

public class WindowService : IWindowService
{
    private Window? _mainWindow;
    private readonly IServiceProvider _serviceProvider;

    public WindowService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void RegisterMainWindow(Window window)
    {
        _mainWindow = window;
    }

    // MODIFICA: Aggiunto parametro opzionale 'initialMode'
    public async Task<List<string>?> ShowAlignmentWindowAsync(
        List<string> sourcePaths, 
        VisualizationMode initialMode = VisualizationMode.Linear) 
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        // Risoluzione servizi
        var fitsService = _serviceProvider.GetRequiredService<IFitsService>();
        var alignmentService = _serviceProvider.GetRequiredService<IAlignmentService>();
        var converter = _serviceProvider.GetRequiredService<IFitsDataConverter>();
        var analysis = _serviceProvider.GetRequiredService<IImageAnalysisService>();
    
        // CREAZIONE VIEWMODEL
        // Passiamo il modo di visualizzazione ricevuto dal BoardViewModel
        using var viewModel = new AlignmentToolViewModel(
            sourcePaths,
            fitsService, 
            alignmentService,
            converter,
            analysis,
            initialMode // <--- PASSAGGIO DEL PARAMETRO
        );
    
        var alignmentWindow = new AlignmentToolView
        {
            DataContext = viewModel
        };

        Action closeHandler = () => { alignmentWindow.Close(); };
        viewModel.RequestClose += closeHandler;

        // Mostra la finestra modale
        await alignmentWindow.ShowDialog<object>(_mainWindow); 

        viewModel.RequestClose -= closeHandler;

        if (viewModel.DialogResult) 
        {
            return viewModel.FinalProcessedPaths; 
        }
    
        return null; 
    }
    
    public async Task<nom.tam.fits.Header?> ShowHeaderEditorAsync(ImageNodeViewModel node)
    {
        if (_mainWindow == null) return null;
        var editorVm = new HeaderEditorViewModel(node);
        
        var editorView = new KomaLab.Views.HeaderEditorView
        {
            DataContext = editorVm
        };

        await editorView.ShowDialog(_mainWindow);

        if (editorView.IsSaved)
        {
            return editorVm.GetUpdatedHeader();
        }
        
        return null;
    }
    
    public async Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node)
    {
        if (_mainWindow == null) return;

        var plateVm = new PlateSolvingViewModel(node);

        var plateView = new KomaLab.Views.PlateSolvingView
        {
            DataContext = plateVm
        };

        await plateView.ShowDialog(_mainWindow);
    }
    
    public async Task<List<string>?> ShowPosterizationWindowAsync(
        List<string> sourcePaths, 
        VisualizationMode initialMode) // <---
    {
        if (_mainWindow == null) throw new InvalidOperationException("Main window not registered");

        var fitsService = _serviceProvider.GetRequiredService<IFitsService>();
        var postService = _serviceProvider.GetRequiredService<IPosterizationService>();
        var converter = _serviceProvider.GetRequiredService<IFitsDataConverter>();
        var analysis = _serviceProvider.GetRequiredService<IImageAnalysisService>();

        // Passiamo 'initialMode' al costruttore
        using var vm = new PosterizationToolViewModel(
            sourcePaths, fitsService, postService, converter, analysis, initialMode);
            
        var view = new KomaLab.Views.PosterizationToolView
        {
            DataContext = vm
        };

        // Gestione chiusura (pattern standard)
        Action closeHandler = () => view.Close();
        vm.RequestClose += closeHandler;

        await view.ShowDialog(_mainWindow);

        vm.RequestClose -= closeHandler;

        if (vm.DialogResult)
        {
            return vm.ResultPaths;
        }
        return null;
    }
}