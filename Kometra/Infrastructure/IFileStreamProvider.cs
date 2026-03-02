using System.IO;

namespace Kometra.Infrastructure;

public interface IFileStreamProvider
{
    /// <summary>
    /// Apre uno stream dato un percorso (Locale o Risorsa di sistema).
    /// </summary>
    Stream Open(string path);
}