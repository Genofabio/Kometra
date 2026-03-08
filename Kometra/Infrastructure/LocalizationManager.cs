using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Kometra.Assets;

// Assicurati che questo namespace corrisponda a dove hai creato i file .resx

namespace Kometra.Infrastructure;

/// <summary>
/// Gestisce la localizzazione a runtime. 
/// Utilizza il pattern Singleton per essere facilmente accessibile dallo XAML.
/// </summary>
public class LocalizationManager : INotifyPropertyChanged
{
    private static readonly LocalizationManager _instance = new();
    public static LocalizationManager Instance => _instance;

    private CultureInfo _currentCulture = CultureInfo.CurrentUICulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    private LocalizationManager()
    {
        // Imposta una lingua di default se vuoi (es. basata sul sistema)
        Strings.Culture = _currentCulture;
    }

    public CultureInfo CurrentCulture
    {
        get => _currentCulture;
        set
        {
            if (!Equals(_currentCulture, value))
            {
                _currentCulture = value;
                Strings.Culture = value;
                
                // Notifica alla UI che TUTTE le stringhe tradotte devono essere ricaricate
                OnPropertyChanged("Item"); 
            }
        }
    }

    /// <summary>
    /// Indexer fondamentale per lo XAML. Permette di fare: Binding [NomeChiave]
    /// </summary>
    public string this[string key]
    {
        get
        {
            var translation = Strings.ResourceManager.GetString(key, CurrentCulture);
            return translation ?? $"#{key}#"; // Restituisce la chiave tra # se manca la traduzione
        }
    }

    public void SetLanguage(string cultureCode)
    {
        CurrentCulture = new CultureInfo(cultureCode);
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}