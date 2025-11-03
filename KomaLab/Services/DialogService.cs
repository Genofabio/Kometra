using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace KomaLab.Services;

public class DialogService : IDialogService
{
    public async Task<IEnumerable<string>?> ShowOpenFitsFileDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null; 
        }
        var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
        if (topLevel == null)
        {
            return null;
        }

        var fitsFilter = new FilePickerFileType("File FITS")
        {
            Patterns = new[] { "*.fits", "*.fit", "*.fts" }
        };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Apri Immagine/i FITS",
            AllowMultiple = true,
            FileTypeFilter = new[] { fitsFilter }
        });

        // Controlla se l'utente ha selezionato almeno un file
        if (files.Count >= 1)
        {
            // Converte la lista di IStorageFile in una lista di stringhe
            return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>();
        }
        
        return null;
    }
}