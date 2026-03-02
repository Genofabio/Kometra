using System;
using System.Collections.Generic;
using System.Linq;
using System.IO; 
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Kometra.Services.UI;

// ---------------------------------------------------------------------------
// FILE: DialogService.cs
// RUOLO: UI Service (File & Folder Picking)
// DESCRIZIONE:
// Implementazione Avalonia per dialoghi di file e cartelle.
// ---------------------------------------------------------------------------

public class DialogService : IDialogService
{
    public async Task<IEnumerable<string>?> ShowOpenFitsFileDialogAsync()
    {
        var storage = GetStorageProvider();
        if (storage == null) return null;

        // [MODIFICA] Aggiunto supporto per .fz (FITS compresso fpack)
        var fitsFilter = new FilePickerFileType("File FITS")
        {
            Patterns = new[] { "*.fits", "*.fit", "*.fts", "*.fz", "*.fits.fz" },
            MimeTypes = new[] { "image/fits", "application/fits", "application/x-fits" }
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

        string cleanName = Path.GetFileNameWithoutExtension(defaultFileName);
        
        // Pulizia iterativa per gestire casi come image.fits.fz -> image
        while (cleanName.EndsWith(".fit", StringComparison.OrdinalIgnoreCase) || 
               cleanName.EndsWith(".fits", StringComparison.OrdinalIgnoreCase) ||
               cleanName.EndsWith(".fz", StringComparison.OrdinalIgnoreCase))
        {
            cleanName = Path.GetFileNameWithoutExtension(cleanName);
        }

        // [MODIFICA] Anche in salvataggio permettiamo di vedere i .fz esistenti
        var fitsFilter = new FilePickerFileType("File FITS")
        {
            Patterns = new[] { "*.fits", "*.fit", "*.fts", "*.fz" },
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

    public async Task<string?> ShowOpenFolderDialogAsync(string title)
    {
        var storage = GetStorageProvider();
        if (storage == null) return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        return folder?.TryGetLocalPath();
    }

    private IStorageProvider? GetStorageProvider()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var activeWindow = desktop.Windows.LastOrDefault(w => w.IsActive) 
                               ?? desktop.Windows.LastOrDefault();

            if (activeWindow == null) return null;
            return TopLevel.GetTopLevel(activeWindow)?.StorageProvider;
        }
    
        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            if (singleView.MainView == null) return null;
            return TopLevel.GetTopLevel(singleView.MainView)?.StorageProvider;
        }

        return null;
    }
}