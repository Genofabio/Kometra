using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kometra.Models.Settings;
using System.Runtime.InteropServices;
using System.Linq;
using Kometra.Services.UI;

namespace Kometra.Services;

/// <summary>
/// Gestisce la persistenza delle impostazioni nella cartella dell'applicazione (Portable Mode).
/// Ottimizzato per la ricerca di ASTAP CLI su più piattaforme.
/// </summary>
public class ConfigurationService : IConfigurationService, INotifyPropertyChanged
{
    private static readonly string BasePath = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string FilePath = Path.Combine(BasePath, "settings.json");

    private AppSettings _current;

    public AppSettings Current 
    { 
        get => _current;
        private set
        {
            _current = value;
            OnPropertyChanged();
        }
    }

    // Definizione dei nomi eseguibili in base alla piattaforma
    private static string CliExeName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "astap_cli.exe" : "astap_cli";
    private static string StandardExeName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "astap.exe" : "astap";

    public ConfigurationService()
    {
        _current = Load();
        
        ThemeService.ApplyPrimaryColor(_current.PrimarySelectionColor);
        
        // Se al caricamento ASTAP non è impostato, proviamo a cercarlo automaticamente
        // if (string.IsNullOrWhiteSpace(_current.AstapFolder))
        // {
        //     string? found = TryFindAstap();
        //     if (found != null)
        //     {
        //         _current.AstapFolder = found;
        //         Save(); 
        //     }
        // }
    }

    public bool ValidateAstapFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return false;

        // STAMPA QUI COSA STA CERCANDO PER CAPIRE L'ERRORE
        var cliPath = Path.Combine(folderPath, CliExeName);
        var stdPath = Path.Combine(folderPath, StandardExeName);
    
        bool exists = File.Exists(cliPath) || File.Exists(stdPath);
    
        if (!exists) {
            System.Diagnostics.Debug.WriteLine($"ASTAP non trovato in: {folderPath}");
            System.Diagnostics.Debug.WriteLine($"Cercato: {cliPath} oppure {stdPath}");
        }

        return exists;
    }

    public void UpdateSettings(AppSettings newSettings)
    {
        Current = newSettings ?? throw new ArgumentNullException(nameof(newSettings));
    
        // Applichiamo il colore primario globalmente
        ThemeService.ApplyPrimaryColor(Current.PrimarySelectionColor);
    
        Save();
    }

    public void Save()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(Current, options);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Errore salvataggio impostazioni: {ex.Message}");
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? CreateDefaultSettings();
            }
        }
        catch { /* Errore di lettura o JSON corrotto */ }

        // Se il file non esiste o è corrotto, generiamo i default basati sul sistema
        return CreateDefaultSettings();
    }
    
    private AppSettings CreateDefaultSettings()
    {
        var settings = new AppSettings();

        // Controlliamo la lingua del sistema (ISO a due lettere, es: "it", "en", "fr")
        string systemLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        // Se è italiano usa "it", altrimenti forza "en"
        settings.Language = (systemLang == "it") ? "it" : "en";

        return settings;
    }

    private string? TryFindAstap()
    {
        // 1. Cerchiamo nel PATH di sistema (priorità alla versione CLI)
        string? cliInPath = FindInSystemPath(CliExeName);
        if (cliInPath != null) return Path.GetDirectoryName(cliInPath);

        string? stdInPath = FindInSystemPath(StandardExeName);
        if (stdInPath != null) return Path.GetDirectoryName(stdInPath);

        // 2. Controllo directory comuni
        string[] searchNames = { CliExeName, StandardExeName };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string[] winPaths = { 
                @"C:\Program Files\astap", 
                @"C:\astap", 
                @"D:\astap", 
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "astap") 
            };
            return SearchInDirectories(winPaths, searchNames);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string[] linuxPaths = { "/usr/bin", "/usr/local/bin", "/opt/astap" };
            return SearchInDirectories(linuxPaths, searchNames);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string[] macPaths = { "/Applications/astap.app/Contents/MacOS", "/usr/local/bin" };
            return SearchInDirectories(macPaths, searchNames);
        }

        return null;
    }

    private string? SearchInDirectories(string[] directories, string[] fileNames)
    {
        foreach (var dir in directories)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in fileNames)
            {
                if (File.Exists(Path.Combine(dir, file))) return dir;
            }
        }
        return null;
    }

    private string? FindInSystemPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        var paths = pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.Combine(path, fileName);
                if (File.Exists(fullPath)) return fullPath;
            }
            catch { }
        }
        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) 
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}