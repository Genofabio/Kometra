using Avalonia.Controls;
using KomaLab.ViewModels;
using KomaLab.Views; 
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
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

    // --- INIZIO SOSTITUZIONE ---
    public async Task<List<FitsImageData>?> ShowAlignmentWindowAsync(BaseNodeViewModel nodeToAlign)
    {
        if (_mainWindow == null)
        {
            throw new InvalidOperationException("La finestra principale non è stata registrata.");
        }

        // 1. Preleva i dati IN MEMORIA dal nodo (non i path)
        var currentData = await nodeToAlign.GetCurrentDataAsync();
        
        // La finestra di allineamento parte "pulita"

        // 2. Risolvi tutti i servizi necessari
        var fitsService = _serviceProvider.GetRequiredService<IFitsService>();
        var alignmentService = _serviceProvider.GetRequiredService<IAlignmentService>();
        var processingService = _serviceProvider.GetRequiredService<IImageProcessingService>();
    
        // 3. Crea il ViewModel (LA FIRMA È CAMBIATA)
        var viewModel = new AlignmentToolViewModel(
            currentData,
            fitsService, 
            alignmentService, 
            processingService);
    
        var alignmentWindow = new AlignmentToolView
        {
            DataContext = viewModel
        };

        // 4. Collega e attendi la chiusura (invariato)
        Action closeHandler = () => { alignmentWindow.Close(); };
        viewModel.RequestClose += closeHandler;

        await alignmentWindow.ShowDialog<object>(_mainWindow); 

        viewModel.RequestClose -= closeHandler;

        // 5. Restituisci i NUOVI dati processati
        if (viewModel.DialogResult) // Se ha premuto "Applica"
        {
            return viewModel.FinalProcessedData; 
        }
    
        return null; // L'utente ha annullato
    }
    // --- FINE SOSTITUZIONE ---
}