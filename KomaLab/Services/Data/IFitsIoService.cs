using System.Threading.Tasks;
using KomaLab.Models.Fits;
using nom.tam.fits;

namespace KomaLab.Services.Data;

public interface IFitsIoService
{
    /// <summary>
    /// Carica un file FITS dal disco o risorsa embedded.
    /// Legge il primo HDU immagine disponibile.
    /// </summary>
    Task<FitsImageData?> LoadAsync(string path);

    /// <summary>
    /// Legge solo l'header FITS per un accesso rapido ai metadati (es. Data, WCS)
    /// senza caricare l'intera matrice pixel.
    /// </summary>
    Task<Header?> ReadHeaderOnlyAsync(string path);

    /// <summary>
    /// Salva l'immagine su disco.
    /// Utilizza il FitsMetadataService per trasferire e sanitizzare correttamente
    /// i metadati dall'header in memoria al nuovo file.
    /// </summary>
    Task SaveAsync(FitsImageData data, string path);
}