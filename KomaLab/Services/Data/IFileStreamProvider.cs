using System.IO;

namespace KomaLab.Services.Data;

public interface IFileStreamProvider
{
    /// <summary>
    /// Apre uno stream dato un percorso. 
    /// Può gestire file locali, risorse embedded o URL a seconda dell'implementazione.
    /// </summary>
    Stream Open(string path);
}