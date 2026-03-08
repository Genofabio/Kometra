using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Infrastructure;

namespace Kometra.ViewModels.Settings;

public partial class SettingsViewModel : ObservableObject
{
    // --- LINGUA ---
    [ObservableProperty] private LanguageOption _selectedLanguage;

    public List<LanguageOption> AvailableLanguages { get; } = new()
    {
        new LanguageOption("English", "en"),
        new LanguageOption("Italiano", "it")
    };

    // --- COLORI E PATH (Mock momentanei) ---
    [ObservableProperty] private string _boardBackgroundColor = "#1E1E1E";
    [ObservableProperty] private string _primarySelectionColor = "#FEC530";
    [ObservableProperty] private string _astapPath = @"C:\Program Files\astap\astap.exe";

    public event Action? RequestClose;

    public SettingsViewModel()
    {
        // Inizializza la lingua corrente leggendola dal manager
        var currentCode = LocalizationManager.Instance.CurrentCulture.TwoLetterISOLanguageName;
        _selectedLanguage = AvailableLanguages.FirstOrDefault(l => l.CultureCode == currentCode) 
                            ?? AvailableLanguages.First();
    }

    // Intercetta il cambio della proprietà SelectedLanguage e aggiorna il Manager
    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value != null)
        {
            LocalizationManager.Instance.SetLanguage(value.CultureCode);
        }
    }

    [RelayCommand]
    private void Save()
    {
        // Qui in futuro salverai i colori e l'ASTAP path nei setting persistenti (es. JSON o AppSettings)
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }
}

// Classe di appoggio per la ComboBox delle lingue
public record LanguageOption(string DisplayName, string CultureCode);