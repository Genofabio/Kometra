using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
// Necessario per FilePickerFileType

namespace KomaLab.Services.UI;

public class DialogService : IDialogService
{
    public async Task<IEnumerable<string>?> ShowOpenFitsFileDialogAsync()
    {
        var storage = GetStorageProvider();
        if (storage == null) return null;

        var fitsFilter = new FilePickerFileType("File FITS")
        {
            Patterns = new[] { "*.fits", "*.fit", "*.fts" }
        };

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Apri Immagine/i FITS",
            AllowMultiple = true,
            FileTypeFilter = new[] { fitsFilter }
        });

        if (files.Count >= 1)
        {
            // Nota: TryGetLocalPath restituisce null se il file è in cloud/virtuale.
            // Per un'app desktop classica va bene, ma tienilo a mente.
            return files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Cast<string>();
        }
        
        return null;
    }
    
    public async Task<string?> ShowSaveFitsFileDialogAsync(string defaultFileName)
    {
        var storage = GetStorageProvider();
        if (storage == null) return null;

        var fitsFilter = new FilePickerFileType("File FITS")
        {
            Patterns = new[] { "*.fits", "*.fit", "*.fts" }
        };

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salva Immagine FITS",
            SuggestedFileName = defaultFileName,
            FileTypeChoices = new[] { fitsFilter },
            DefaultExtension = "fits"
        });

        return file?.TryGetLocalPath();
    }

    // --- Helper Privato per ridurre duplicazione ---
    private IStorageProvider? GetStorageProvider()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
            return topLevel?.StorageProvider;
        }
        return null;
    }
}