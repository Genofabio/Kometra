using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
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

    // MODIFICA: Accettiamo List<string> invece di ImageNodeViewModel
    // Questo ci permette di passare solo i percorsi leggeri
    public async Task<List<string>?> ShowAlignmentWindowAsync(List<string> sourcePaths)
    {
        if (_mainWindow == null) throw new InvalidOperationException("Finestra principale non registrata.");

        // Risoluzione servizi
        var fitsService = _serviceProvider.GetRequiredService<IFitsService>();
        var alignmentService = _serviceProvider.GetRequiredService<IAlignmentService>();
        var converter = _serviceProvider.GetRequiredService<IFitsDataConverter>();
        var analysis = _serviceProvider.GetRequiredService<IImageAnalysisService>();
    
        // CREAZIONE VIEWMODEL (Versione Low RAM)
        // Passiamo i percorsi, non i dati in memoria
        using var viewModel = new AlignmentToolViewModel(
            sourcePaths,
            fitsService, 
            alignmentService,
            converter,
            analysis
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
}