using System.Threading.Tasks;
using KomaLab.Models.Visualization;

namespace KomaLab.Services.Imaging;

public interface IPosterizationService
{
    /// <summary>
    /// Esegue il processo di posterizzazione su un file FITS.
    /// Riduce la profondità colore (livelli), applica uno stretch (Log/Linear) 
    /// e salva il risultato come FITS a 8-bit.
    /// </summary>
    /// <param name="inputPath">Percorso file sorgente.</param>
    /// <param name="outputFolder">Cartella di destinazione.</param>
    /// <param name="levels">Numero di livelli di grigio (es. 4, 8, 16).</param>
    /// <param name="mode">Modalità di visualizzazione (Linear, Log, Sqrt).</param>
    /// <param name="blackPoint">Valore di cut-off nero.</param>
    /// <param name="whitePoint">Valore di cut-off bianco.</param>
    /// <returns>Il percorso completo del file generato.</returns>
    Task<string> PosterizeAndSaveAsync(
        string inputPath,
        string outputFolder,
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint
    );
}