using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using KomaLab.Models;

namespace KomaLab.ViewModels;

/// <summary>
/// Specializzazione per nodi che manipolano immagini FITS.
/// </summary>
public abstract class ImageNodeViewModel : BaseNodeViewModel
{
    // Qui manteniamo la logica della dimensione stimata se ti serve per i connettori
    protected const double ESTIMATED_UI_HEIGHT = 60.0;

    protected ImageNodeViewModel(BaseNodeModel model) : base(model)
    {
        this.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NodeContentSize))
            {
                OnPropertyChanged(nameof(EstimatedTotalSize));
            }
        };
    }

    public virtual Size EstimatedTotalSize
    {
        get
        {
            var contentSize = this.NodeContentSize;
            return new Size(contentSize.Width, contentSize.Height + ESTIMATED_UI_HEIGHT);
        }
    }

    protected abstract Size NodeContentSize { get; }

    // --- Metodi Astratti Specifici per Immagini ---
    
    public abstract Task ResetThresholdsAsync();
    public abstract Task<List<FitsImageData?>> GetCurrentDataAsync();
    public abstract FitsImageData? GetActiveImageData();
    public abstract Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData);
}