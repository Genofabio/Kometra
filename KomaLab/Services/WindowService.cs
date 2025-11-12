using Avalonia.Controls;
using KomaLab.ViewModels;
using KomaLab.Views; 
using System;
using Microsoft.Extensions.DependencyInjection;

namespace KomaLab.Services;

public class WindowService : IWindowService
{
    private Window? _mainWindow;
    
    // --- 1. Aggiungi il campo per il ServiceProvider ---
    private readonly IServiceProvider _serviceProvider;

    // --- 2. Inietta IServiceProvider ---
    // (Ci serve per "costruire" i ViewModel con le loro dipendenze)
    public WindowService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void RegisterMainWindow(Window window)
    {
        _mainWindow = window;
    }

    public void ShowAlignmentWindow(BaseNodeViewModel nodeToAlign)
    {
        if (_mainWindow == null)
        {
            throw new InvalidOperationException("La finestra principale non è stata registrata.");
        }

        // --- 3. "Risolvi" i servizi necessari ---
        var fitsService = _serviceProvider.GetRequiredService<IFitsService>();
        var alignmentService = _serviceProvider.GetRequiredService<IAlignmentService>();
        
        // 4. Crea il ViewModel, passando le dipendenze
        var viewModel = new AlignmentToolViewModel(nodeToAlign, fitsService, alignmentService);
        
        var alignmentWindow = new AlignmentToolView
        {
            DataContext = viewModel
        };

        alignmentWindow.ShowDialog(_mainWindow);
    }
}