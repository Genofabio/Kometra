using System;
using System.IO;
using Avalonia.Platform;

namespace KomaLab.Infrastructure;

public class AvaloniaAssetStreamProvider : IFileStreamProvider
{
    public Stream Open(string path)
    {
        try
        {
            var uri = new Uri(path);
            return AssetLoader.Open(uri);
        }
        catch (Exception ex)
        {
            throw new FileNotFoundException($"Risorsa Avalonia non trovata: {path}", ex);
        }
    }
}