using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Kometra.Assets;

namespace Kometra.Infrastructure;

/// <summary>
/// Gestisce la localizzazione a runtime e la conversione dei caratteri speciali.
/// Utilizza il pattern Singleton per essere accessibile globalmente da XAML e C#.
/// </summary>
public class LocalizationManager : INotifyPropertyChanged
{
    private static readonly LocalizationManager _instance = new();
    public static LocalizationManager Instance => _instance;

    private CultureInfo _currentCulture = CultureInfo.CurrentUICulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    private LocalizationManager()
    {
        // Sincronizza la cultura delle risorse con quella attuale
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
                
                // Notifica alla UI che tutte le stringhe tradotte (gestite dall'indexer) devono essere ricaricate
                // "Item[]" è la stringa standard per notificare il cambiamento di un indicizzatore
                OnPropertyChanged(string.Empty);
                OnPropertyChanged("Item");
                OnPropertyChanged("Item[]");
            }
        }
    }

    /// <summary>
    /// Indexer per lo XAML. Permette il binding: {Binding [NomeChiave], Source={x:Static infrastructure:LocalizationManager.Instance}}
    /// Gestisce automaticamente la conversione dei caratteri \n in ritorni a capo reali.
    /// </summary>
    public string this[string key]
    {
        get
        {
            var translation = Strings.ResourceManager.GetString(key, _currentCulture);
            
            if (translation == null) 
                return $"#{key}#";

            // Converte le sequenze di testo "\n" caricate dal file .resx in caratteri di "A capo" effettivi
            return translation.Replace("\\n", Environment.NewLine);
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