using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Infrastructure;
using Kometra.Models.Settings;
using Kometra.Services;
using Kometra.Services.UI;

namespace Kometra.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigurationService _configService;
    private readonly IDialogService _dialogService;
    private readonly string _originalLanguage; // Memorizziamo lo stato iniziale

    [ObservableProperty] private AppSettings _draftSettings;
    
    [ObservableProperty] private string? _astapErrorMessage;

    // --- GESTIONE LINGUA ---
    public List<LanguageOption> AvailableLanguages { get; } = new()
    {
        new LanguageOption("English", "en"),
        new LanguageOption("Italiano", "it")
    };

    [ObservableProperty] private LanguageOption _selectedLanguage;

    // --- PROPRIETÀ PONTE ---

    public string AstapFolderPath
    {
        get => DraftSettings.AstapFolder;
        set
        {
            DraftSettings.AstapFolder = value;
            OnPropertyChanged();
        }
    }

    public Color BoardColor
    {
        get => Color.Parse(DraftSettings.BoardBackgroundColor);
        set 
        { 
            DraftSettings.BoardBackgroundColor = value.ToString(); 
            OnPropertyChanged(); 
        }
    }

    public Color SelectionColor
    {
        get => Color.Parse(DraftSettings.PrimarySelectionColor);
        set 
        { 
            DraftSettings.PrimarySelectionColor = value.ToString(); 
            // Anteprima istantanea!
            ThemeService.ApplyPrimaryColor(DraftSettings.PrimarySelectionColor);
            OnPropertyChanged(); 
        }
    }

    public event Action? RequestClose;

    public SettingsViewModel(IConfigurationService configService, IDialogService dialogService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

        // 1. Memorizziamo la lingua originale prima di qualsiasi modifica
        _originalLanguage = _configService.Current.Language;

        // 2. Cloniamo i settaggi per la bozza
        _draftSettings = _configService.Current.Clone();

        // 3. Impostiamo la selezione iniziale nella ComboBox
        _selectedLanguage = AvailableLanguages.FirstOrDefault(l => l.CultureCode == _originalLanguage) 
                            ?? AvailableLanguages.First();
    }

    [RelayCommand]
    private async Task BrowseAstapFolder()
    {
        var title = LocalizationManager.Instance["SettingsSelectAstapFolder"];
        var path = await _dialogService.ShowOpenFolderDialogAsync(title);

        if (string.IsNullOrWhiteSpace(path)) return;

        AstapErrorMessage = null;

        if (_configService.ValidateAstapFolder(path))
        {
            AstapFolderPath = path;
        }
        else
        {
            AstapErrorMessage = LocalizationManager.Instance["SettingsAstapInvalidFolder"];
        }
    }

    [RelayCommand]
    private void Save()
    {
        // Il salvataggio conferma la lingua del Draft come definitiva
        DraftSettings.Language = SelectedLanguage.CultureCode;
        
        _configService.UpdateSettings(DraftSettings);
        
        // Applichiamo globalmente
        LocalizationManager.Instance.SetLanguage(DraftSettings.Language);

        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        // Ripristiniamo tutto come all'inizio
        LocalizationManager.Instance.SetLanguage(_originalLanguage);
    
        // RIPRISTINA IL COLORE ORIGINALE
        ThemeService.ApplyPrimaryColor(_configService.Current.PrimarySelectionColor);

        RequestClose?.Invoke();
    }

    // Gestisce l'anteprima istantanea della lingua nella UI
    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value != null)
        {
            LocalizationManager.Instance.SetLanguage(value.CultureCode);
        }
    }
}

public record LanguageOption(string DisplayName, string CultureCode);