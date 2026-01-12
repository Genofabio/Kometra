using System;
using System.IO;
using Avalonia.Platform; // Qui è permesso! Siamo nel layer UI.
using KomaLab.Services.Fits;

namespace KomaLab; // O KomaLab.Infrastructure

public class AvaloniaAwareStreamProvider : IFileStreamProvider
{
    public Stream Open(string path)
    {
        // 1. Gestione Risorse Embedded Avalonia
        if (path.StartsWith("avares://"))
        {
            var uri = new Uri(path);
            if (AssetLoader.Exists(uri))
            {
                return AssetLoader.Open(uri);
            }
            throw new FileNotFoundException($"Asset Avalonia non trovato: {path}");
        }

        // 2. Gestione File System Locale (Copia logica o usa base class)
        if (!File.Exists(path)) throw new FileNotFoundException($"File non trovato: {path}");

        // Retry logic semplice
        for (int i = 0; i < 3; i++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                if (i == 2) throw;
                System.Threading.Thread.Sleep(50);
            }
        }
        throw new IOException($"Impossibile aprire {path}");
    }
}