using System.IO;

namespace KomaLab.Services.Data;

// ---------------------------------------------------------------------------
// FILE: LocalFileStreamProvider.cs
// RUOLO: Infrastruttura I/O
// DESCRIZIONE:
// Implementazione concreta per l'accesso al file system locale.
// Include una politica di retry per gestire file temporaneamente bloccati
// (es. da antivirus o indicizzatori), aumentando la stabilità dell'app.
// ---------------------------------------------------------------------------

public class LocalFileStreamProvider : IFileStreamProvider
{
    public Stream Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"File non trovato: {path}");

        // Retry policy per resilienza contro file lock temporanei
        int maxRetries = 3;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                // FileShare.ReadWrite è importante: permette di aprire il file anche se 
                // altri processi lo stanno leggendo/scrivendo (entro i limiti del SO).
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                // Se è l'ultimo tentativo, rilanciamo l'eccezione
                if (i == maxRetries - 1) throw;
                
                // Backoff lineare (50ms, 100ms, 150ms)
                System.Threading.Thread.Sleep(50 * (i + 1));
            }
        }
        throw new IOException($"Impossibile aprire il file dopo {maxRetries} tentativi: {path}");
    }
}