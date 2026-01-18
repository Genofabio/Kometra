using System;
using System.IO;

namespace KomaLab.Infrastructure;

public class FileStreamResolver : IFileStreamProvider
{
    private readonly LocalFileStreamProvider _local;
    private readonly AvaloniaAssetStreamProvider _avalonia;

    // Notare: iniettiamo le classi CONCRETE, non l'interfaccia. 
    // Questo spezza la dipendenza circolare.
    public FileStreamResolver(LocalFileStreamProvider local, AvaloniaAssetStreamProvider avalonia)
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _avalonia = avalonia ?? throw new ArgumentNullException(nameof(avalonia));
    }

    public Stream Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Il percorso non può essere nullo", nameof(path));

        // Smistamento logico
        if (path.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
        {
            return _avalonia.Open(path);
        }

        return _local.Open(path);
    }
}