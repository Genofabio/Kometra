using System;
using System.IO;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Models.Fits;

// ---------------------------------------------------------------------------
// 5. FITS FILE REFERENCE (Runtime Wrapper)
// L'oggetto chiave che il ViewModel manipola. Collega il file su disco
// allo stato volatile (modifiche non salvate).
// ---------------------------------------------------------------------------
public class FitsFileReference
{
    // Dati Identità (Immutabili)
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    // Stato Volatile (Mutabile)
    // Se diverso da null, il Renderer deve usare questo invece di quello su disco.
    public FitsHeader? ModifiedHeader { get; set; }

    public FitsFileReference(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
        FilePath = path;
    }
}