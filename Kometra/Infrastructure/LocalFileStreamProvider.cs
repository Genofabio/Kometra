using System;
using System.IO;
using System.Threading;

namespace Kometra.Infrastructure;

public class LocalFileStreamProvider : IFileStreamProvider
{
    public Stream Open(string path)
    {
        if (!File.Exists(path)) 
            throw new FileNotFoundException($"File non trovato sul disco: {path}");

        const int maxRetries = 3;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                // FileShare.ReadWrite è vitale per file FITS aperti da altri visualizzatori
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                if (i == maxRetries - 1) throw;
                Thread.Sleep(50 * (i + 1));
            }
        }
        throw new IOException($"Impossibile accedere al file: {path}");
    }
}