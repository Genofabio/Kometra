using System;
using System.Collections.Generic;
using System.Linq;
using System.IO; 
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace KomaLab.Services.UI;

// ---------------------------------------------------------------------------
// FILE: DialogService.cs
// RUOLO: UI Service (File Picking)
// DESCRIZIONE:
// Implementazione specifica per Avalonia del servizio di dialogo.
// Fa da ponte tra la UI (StorageProvider) e il Backend (che si aspetta percorsi stringa).
// ---------------------------------------------------------------------------

public class DialogService : IDialogService
{
    public async Task<IEnumerable<string>?> ShowOpenFitsFileDialogAsync()
    {
        var storage = GetStorageProvider();
        if (storage == null) return null;

        var fitsFilter = new FilePickerFileType("File FITS")
        {
            Patterns = new[] { "*.fits", "*.fit", "*.fts" },
            MimeTypes = new[] { "image/fits", "application/fits" } // Aggiunto MimeType per completezza Linux/Mac
        };

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Apri Immagine/i FITS",
            AllowMultiple = true,
            FileTypeFilter = new[] { fitsFilter }
        });

        if (files.Count >= 1)
        {
            // NOTA: TryGetLocalPath() è cruciale. 
            // Il nostro backend (OpenCV/CSharpFITS) lavora con FileStream su percorsi fisici.
            // Se siamo in un ambiente sandboxed che non espone il path (es. WebAssembly), 
            // questo filtrerà i file, che è il comportamento corretto (fail-safe) per ora.
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
        // Pulisce estensioni multiple o errate prima di proporre il nome.
        string cleanName = Path.GetFileNameWithoutExtension(defaultFileName);
        
        while (cleanName.EndsWith(".fit", StringComparison.OrdinalIgnoreCase) || 
               cleanName.EndsWith(".fits", StringComparison.OrdinalIgnoreCase))
        {
            cleanName = Path.GetFileNameWithoutExtension(cleanName);
        }
        // ---------------------

        var fitsFilter = new FilePickerFileType("File FITS")
        {
            Patterns = new[] { "*.fits", "*.fit", "*.fts" },
            MimeTypes = new[] { "image/fits", "application/fits" }
        };

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salva Immagine FITS",
            SuggestedFileName = cleanName, 
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

    /// <summary>
    /// Recupera il Provider di Storage in modo agnostico rispetto al Lifetime dell'app.
    /// </summary>
    private IStorageProvider? GetStorageProvider()
    {
        // 1. Caso Desktop Classico (MainWindow)
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow == null) return null; // Finestra non ancora inizializzata
            return TopLevel.GetTopLevel(desktop.MainWindow)?.StorageProvider;
        }
        
        // 2. Caso Single View (Mobile / Browser / Embedded)
        // Utile per rendere il servizio "future-proof" se mai porterai KomaLab su Android/Linux Touch
        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
             if (singleView.MainView == null) return null;
             return TopLevel.GetTopLevel(singleView.MainView)?.StorageProvider;
        }

        return null;
    }
}