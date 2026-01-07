using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System.IO; 

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

        // --- FIX NOME FILE ---
        // Se il nome suggerito ha già un'estensione, la rimuoviamo.
        // Il FilePicker aggiungerà automaticamente l'estensione scelta dall'utente (DefaultExtension).
        // Questo evita "immagine.fit.fits".
        string cleanName = Path.GetFileNameWithoutExtension(defaultFileName);
        
        // Sicurezza extra: se c'era doppia estensione (es. .tar.fits), Path.GetFileNameWithoutExtension ne toglie solo una.
        // Controlliamo se finisce ancora con .fit o .fits e puliamo ancora se necessario.
        while (cleanName.EndsWith(".fit", System.StringComparison.OrdinalIgnoreCase) || 
               cleanName.EndsWith(".fits", System.StringComparison.OrdinalIgnoreCase))
        {
            cleanName = Path.GetFileNameWithoutExtension(cleanName);
        }
        // ---------------------

        var fitsFilter = new FilePickerFileType("File FITS")
        {
            Patterns = new[] { "*.fits", "*.fit", "*.fts" }
        };

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salva Immagine FITS",
            SuggestedFileName = cleanName, // Usiamo il nome pulito
            FileTypeChoices = new[] { fitsFilter },
            DefaultExtension = "fits"
        });

        return file?.TryGetLocalPath();
    }
    
    public async Task<string?> ShowSaveFileDialogAsync(string defaultFileName, string filterName, string pattern)
    {
        var storage = GetStorageProvider();
        if (storage == null) return null;

        var fileType = new FilePickerFileType(filterName)
        {
            Patterns = new[] { pattern }
        };

        var defaultExt = pattern.TrimStart('*', '.');
        
        // Applichiamo la stessa logica di pulizia anche qui per sicurezza
        string cleanName = Path.GetFileNameWithoutExtension(defaultFileName);

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Salva {filterName}",
            SuggestedFileName = cleanName,
            FileTypeChoices = new[] { fileType },
            DefaultExtension = defaultExt
        });

        return file?.TryGetLocalPath();
    }

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