using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using KomaLab.Models;
using KomaLab.Services.Data;
using KomaLab.ViewModels.Helpers;

namespace KomaLab.ViewModels;

/// <summary>
/// Specializzazione astratta per nodi che manipolano immagini FITS.
/// </summary>
public abstract class ImageNodeViewModel : BaseNodeViewModel
{

    protected ImageNodeViewModel(BaseNodeModel model) : base(model)
    {
    }
    
    public ImageViewport Viewport { get; } = new();
    
    public override NodeCategory Category => NodeCategory.Image;

    public override Size EstimatedTotalSize
    {
        get
        {
            var contentSize = this.NodeContentSize;
            return new Size(contentSize.Width, contentSize.Height);
        }
    }

    protected abstract Size NodeContentSize { get; }

    // --- Metodi Astratti (Contratto per le sottoclassi) ---
    
    /// <summary>
    /// Ricalcola i livelli di visualizzazione (Auto-Stretch).
    /// </summary>
    public abstract Task ResetThresholdsAsync();
    
    /// <summary>
    /// Recupera la lista completa dei dati gestiti da questo nodo 
    /// (es. per passarli al tool di allineamento o stacking).
    /// </summary>
    public abstract Task<List<FitsImageData?>> GetCurrentDataAsync();
    
    /// <summary>
    /// Restituisce l'immagine singola attualmente visualizzata 
    /// (es. per il salvataggio file).
    /// </summary>
    public abstract FitsImageData? GetActiveImageData();
    
    /// <summary>
    /// Inietta dati processati direttamente in memoria (es. risultato di uno stack).
    /// </summary>
    public abstract Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData);

    // Restituisce i percorsi file (esistenti o temporanei) da passare al tool.
    // Richiede IFitsService per salvare eventuali dati in memoria.
    public abstract Task<List<string>> PrepareInputPathsAsync(IFitsService fitsService);
}