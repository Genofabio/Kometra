using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace KomaLab.Services;

public class DialogService : IDialogService
{
    public async Task<string?> ShowOpenFitsFileDialogAsync()
    {
        // Ottieni un riferimento alla finestra principale (necessario per il dialogo)
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null; // Non siamo in un'app desktop?
        }

        var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
        if (topLevel == null)
        {
            return null;
        }

        // Definisci i filtri per i file FITS
        var fitsFilter = new FilePickerFileType("File FITS")
        {
            Patterns = new[] { "*.fits", "*.fit", "*.fts" }
        };

        // Prepara la finestra di dialogo
        var file = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Apri Immagine FITS",
            AllowMultiple = false,
            FileTypeFilter = new[] { fitsFilter }
        });

        // Controlla se l'utente ha selezionato un file
        if (file.Count >= 1)
        {
            // Restituisce il percorso (path) del file come stringa
            // Usiamo TryGetLocalPath() per ottenere un path C# standard
            return file[0].TryGetLocalPath();
        }

        // L'utente ha annullato
        return null;
    }
}