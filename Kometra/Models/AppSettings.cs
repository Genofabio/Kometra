namespace Kometra.Models.Settings;

/// <summary>
/// Rappresenta i dati grezzi delle impostazioni dell'applicazione.
/// </summary>
public class AppSettings
{
    // --- LINGUA ---
    public string Language { get; set; }

    // --- INTERFACCIA (Colori salvati come stringhe Hex) ---
    public string BoardBackgroundColor { get; set; }
    public string PrimarySelectionColor { get; set; }

    // --- MOTORI ESTERNI (Percorso cartella) ---
    public string AstapFolder { get; set; } = string.Empty;
    
    public AppSettings()
    {
        // Default basato sul sistema
        string systemLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Language = (systemLang == "it") ? "it" : "en";
        
        // Altri default fissi
        BoardBackgroundColor = "#121212";
        PrimarySelectionColor = "#8058E8";
    }

    /// <summary>
    /// Crea una copia profonda delle impostazioni per la gestione "Draft" nel ViewModel.
    /// </summary>
    public AppSettings Clone()
    {
        return new AppSettings
        {
            Language = this.Language,
            BoardBackgroundColor = this.BoardBackgroundColor,
            PrimarySelectionColor = this.PrimarySelectionColor,
            AstapFolder = this.AstapFolder
        };
    }
}