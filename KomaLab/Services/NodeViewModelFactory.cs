using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using KomaLab.Models;
using KomaLab.ViewModels;

namespace KomaLab.Services;

/// <summary>
/// Implementazione concreta di INodeViewModelFactory.
/// </summary>
public class NodeViewModelFactory : INodeViewModelFactory
{
    // Dipendenza dal servizio che processa le immagini
    private readonly IFitsService _fitsService;

    /// <summary>
    /// Costruttore che riceve le dipendenze necessarie.
    /// </summary>
    public NodeViewModelFactory(IFitsService fitsService)
    {
        _fitsService = fitsService;
    }

    /// <summary>
    /// Crea, carica e posiziona un nuovo SingleImageNodeViewModel.
    /// (Questo metodo è invariato e corretto).
    /// </summary>
    public async Task<SingleImageNodeViewModel> CreateSingleImageNodeAsync(
        BoardViewModel parent, 
        string imagePath, 
        double x, double y, // x, y ora sono il *centro* desiderato
        bool centerOnPosition = false)
    {
        var newNodeModel = new NodeModel
        {
            ImagePath = imagePath,
            Title = Path.GetFileName(imagePath),
            X = x,
            Y = y
        };
    
        var newNodeViewModel = new SingleImageNodeViewModel(parent, newNodeModel, _fitsService);
        
        await newNodeViewModel.LoadDataAsync();
        
        if (centerOnPosition)
        {
            var size = newNodeViewModel.EstimatedTotalSize;
            newNodeViewModel.X = x - (size.Width / 2);
            newNodeViewModel.Y = y - (size.Height / 2);
        }
        
        return newNodeViewModel;
    }
    
    /// <summary>
    /// Crea, pre-scansiona e pre-carica un nuovo MultipleImagesNodeViewModel.
    /// (Versione ottimizzata).
    /// </summary>
    public async Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(
        BoardViewModel parent, 
        List<string> imagePaths, 
        double x, double y)
    {
        // --- 1. ESEGUI LA PRE-SCANSIONE LEGGERA ---
        double maxWidth = 0;
        double maxHeight = 0;
        foreach (var path in imagePaths)
        {
            try
            {
                var imgSize = await _fitsService.GetFitsImageSizeAsync(path);
                if (imgSize.Width > maxWidth) maxWidth = imgSize.Width;
                if (imgSize.Height > maxHeight) maxHeight = imgSize.Height;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Impossibile leggere l'header di {path}: {ex.Message}");
            }
        }
        var maxSize = new Size(maxWidth, maxHeight);
        
        // --- 2. CARICA IL PRIMO FILE (UNA VOLTA SOLA) ---
        string title;
        FitsImageData? firstImageData;
        try
        {
            // Caricamento pesante del primo file
            firstImageData = await _fitsService.LoadFitsFromFileAsync(imagePaths[0]);

            if (firstImageData == null)
            {
                throw new InvalidOperationException("Impossibile caricare i dati FITS dal primo file della pila.");
            }

            // Genera il titolo
            title = firstImageData.FitsHeader.GetStringValue("OBJECT");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = Path.GetFileName(imagePaths[0]);
            }
            title += $" ({imagePaths.Count} immagini)";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore critico durante il caricamento di {imagePaths[0]}: {ex.Message}");
            // Non possiamo continuare se il primo file fallisce
            throw new InvalidOperationException("Impossibile creare la pila di immagini.", ex);
        }

        // --- 3. CREA IL MODELLO ---
        var newNodeModel = new NodeModel
        {
            ImagePath = string.Empty, 
            Title = title,
            X = x,
            Y = y
        };

        // --- 4. CREA IL VIEWMODEL (PASSANDO I DATI PRE-CARICATI) ---
        var newNodeViewModel = new MultipleImagesNodeViewModel(
            parent, 
            newNodeModel, 
            imagePaths, 
            _fitsService,
            maxSize,
            firstImageData); // <-- Passa i dati qui

        // --- 5. CALCOLA IL CENTRAGGIO ---
        // Non è più necessario 'await newNodeViewModel.InitializationTask'
        var size = newNodeViewModel.EstimatedTotalSize;
        newNodeViewModel.X = x - (size.Width / 2);
        newNodeViewModel.Y = y - (size.Height / 2);

        return newNodeViewModel;
    }
}