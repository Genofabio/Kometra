using Avalonia.Controls;
using KomaLab.ViewModels;
using KomaLab.Views; 
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models;
using Microsoft.Extensions.DependencyInjection;

namespace KomaLab.Services;

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
    
    public async Task<List<FitsImageData>?> ShowAlignmentWindowAsync(ImageNodeViewModel nodeToAlign)
    {
        if (_mainWindow == null)
        {
            throw new InvalidOperationException("La finestra principale non è stata registrata.");
        }

        // ORA FUNZIONA: ImageNodeViewModel ha questo metodo
        var currentData = await nodeToAlign.GetCurrentDataAsync();
        
        // Risoluzione dei nuovi servizi (come visto nello step precedente)
        var fitsService = _serviceProvider.GetRequiredService<IFitsService>();
        var alignmentService = _serviceProvider.GetRequiredService<IAlignmentService>();
        var converter = _serviceProvider.GetRequiredService<IFitsDataConverter>();
        var analysis = _serviceProvider.GetRequiredService<IImageAnalysisService>();
    
        var viewModel = new AlignmentToolViewModel(
            currentData,
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

        await alignmentWindow.ShowDialog<object>(_mainWindow); 

        viewModel.RequestClose -= closeHandler;

        if (viewModel.DialogResult) 
        {
            return viewModel.FinalProcessedData; 
        }
    
        return null; 
    }
}