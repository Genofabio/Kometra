using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services.Data;
using KomaLab.ViewModels.Helpers;

namespace KomaLab.ViewModels;

/// <summary>
/// Classe base per tutti i nodi che visualizzano o manipolano immagini FITS.
/// Gestisce la logica comune di:
/// 1. Geometria (Viewport, Zoom, Pan)
/// 2. Radiometria (Black/White Point, Reset Soglie)
/// 3. Interfaccia con il Renderer attivo
/// </summary>
public abstract partial class ImageNodeViewModel : BaseNodeViewModel
{
    // --- Campi Privati ---
    private Size _viewportSize;

    // --- Proprietà Gestione Vista ---
    public ImageViewport Viewport { get; } = new();

    /// <summary>
    /// Proprietà "Ponte": riceve le dimensioni reali del controllo grafico (View)
    /// tramite Binding OneWayToSource e le passa al Viewport logico.
    /// Fondamentale per far funzionare correttamente Zoom e Pan.
    /// </summary>
    public Size ViewportSize
    {
        get => _viewportSize;
        set
        {
            if (_viewportSize != value)
            {
                _viewportSize = value;
                Viewport.ViewportSize = value;
            }
        }
    }

    // --- Proprietà Gestione Immagine ---
    
    /// <summary>
    /// Riferimento astratto al renderer attualmente visibile.
    /// Le sottoclassi devono implementarlo per restituire l'immagine attiva 
    /// (es. l'unica immagine per SingleNode, o quella selezionata per MultipleNode).
    /// </summary>
    public abstract FitsRenderer? ActiveRenderer { get; }

    [ObservableProperty] 
    private double _blackPoint;

    [ObservableProperty] 
    private double _whitePoint;

    // --- Override BaseNodeViewModel ---

    public override NodeCategory Category => NodeCategory.Image;

    public override Size EstimatedTotalSize
    {
        get
        {
            var contentSize = this.NodeContentSize;
            // Aggiungiamo un minimo di padding logico se necessario
            return new Size(contentSize.Width, contentSize.Height);
        }
    }

    /// <summary>
    /// Dimensione del contenuto (immagine) per il calcolo del layout del nodo.
    /// </summary>
    protected abstract Size NodeContentSize { get; }

    // --- Costruttore ---

    protected ImageNodeViewModel(BaseNodeModel model) : base(model)
    {
    }

    // --- Gestione Soglie (Condivisa) ---

    /// <summary>
    /// Esegue l'Auto-Stretch sull'immagine attiva e aggiorna gli slider.
    /// </summary>
    [RelayCommand]
    public async Task ResetThresholdsAsync()
    {
        if (ActiveRenderer == null) return;

        // 1. Calcola le nuove soglie ideali nel renderer
        await ActiveRenderer.ResetThresholdsAsync();

        // 2. Aggiorna le proprietà del ViewModel (che aggiorneranno la UI)
        // Usiamo SetProperty o assegnazione diretta, ma dobbiamo evitare loop infiniti
        // se i Partial OnChanged richiamano logica pesante.
        // Qui ci limitiamo ad allineare i valori.
        BlackPoint = ActiveRenderer.BlackPoint;
        WhitePoint = ActiveRenderer.WhitePoint;
    }

    // Quando l'utente muove lo slider BlackPoint
    partial void OnBlackPointChanged(double value)
    {
        if (ActiveRenderer != null)
        {
            ActiveRenderer.BlackPoint = value;
        }
    }

    // Quando l'utente muove lo slider WhitePoint
    partial void OnWhitePointChanged(double value)
    {
        if (ActiveRenderer != null)
        {
            ActiveRenderer.WhitePoint = value;
        }
    }

    // --- Gestione Zoom/Pan ---

    /// <summary>
    /// Resetta la vista per i Nodi della Board.
    /// Poiché i nodi si adattano alla dimensione dell'immagine (100%),
    /// il reset imposta Scala 1.0 e Offset 0.
    /// </summary>
    public void ResetView()
    {
        Viewport.Scale = 1.0;
        Viewport.OffsetX = 0;
        Viewport.OffsetY = 0;
    }

    // --- Metodi Astratti (Contratto Dati) ---

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

    /// <summary>
    /// Restituisce i percorsi file (esistenti o temporanei) da passare ai tool esterni.
    /// </summary>
    public abstract Task<List<string>> PrepareInputPathsAsync(IFitsService fitsService);
}