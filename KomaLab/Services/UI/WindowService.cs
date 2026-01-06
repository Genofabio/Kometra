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
    
    public async Task<nom.tam.fits.Header?> ShowHeaderEditorAsync(ImageNodeViewModel node)
    {
        if (_mainWindow == null) return null;
        var editorVm = new HeaderEditorViewModel(node);
        
        // 2. Crea la View
        var editorView = new KomaLab.Views.HeaderEditorView
        {
            DataContext = editorVm
        };

        // 3. Mostra Dialog
        await editorView.ShowDialog(_mainWindow);

        // 4. Se salvato, restituisci l'header aggiornato
        if (editorView.IsSaved)
        {
            return editorVm.GetUpdatedHeader();
        }
        
        return null; // Annullato
    }
    
    public async Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node)
    {
        if (_mainWindow == null) return;

        // 1. Crea il ViewModel
        // Nota: PlateSolvingViewModel istanzia internamente il suo servizio,
        // quindi non serve il serviceProvider qui, a meno che tu non voglia usare DI pure lì.
        var plateVm = new PlateSolvingViewModel(node);

        // 2. Crea la View
        var plateView = new KomaLab.Views.PlateSolvingView
        {
            DataContext = plateVm
        };

        // 3. Mostra la finestra come Dialog (blocca l'interazione sotto finché aperta)
        await plateView.ShowDialog(_mainWindow);
        
        // Non serve ritornare nulla perché ASTAP modifica direttamente il file su disco
        // e il ViewModel si occupa di ricaricare i dati.
    }
}