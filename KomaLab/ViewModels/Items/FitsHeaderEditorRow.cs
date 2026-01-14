using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.ViewModels.Items;

// ---------------------------------------------------------------------------
// FILE: FitsHeaderEditorRow.cs
// RUOLO: ViewModel per una riga modificabile nell'editor Header.
// ---------------------------------------------------------------------------

public partial class FitsHeaderEditorRow : ObservableObject
{
    /// <summary>
    /// Costruttore per inizializzare i dati SENZA far scattare il flag IsModified.
    /// </summary>
    public FitsHeaderEditorRow(string key, string value, string comment, bool isReadOnly)
    {
        // Impostiamo direttamente i campi privati (backing fields)
        // per evitare che scattino i metodi "OnChanged".
        _key = key;
        _value = value;
        _comment = comment;
        _isReadOnly = isReadOnly;
        _isModified = false;
    }

    [ObservableProperty]
    private string _key = "";

    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private string _comment = "";

    /// <summary>
    /// Se True, la riga non può essere modificata (es. NAXIS, BITPIX).
    /// </summary>
    [ObservableProperty]
    private bool _isReadOnly;

    /// <summary>
    /// Se True, indica che l'utente ha cambiato qualcosa rispetto al caricamento.
    /// Utile per colorare la riga o abilitare il tasto Salva.
    /// </summary>
    [ObservableProperty]
    private bool _isModified;

    // --- INTERCETTORI (Hook Methods) ---
    // Questi metodi vengono chiamati AUTOMATICAMENTE dal Toolkit 
    // quando le proprietà pubbliche (Value, Comment) cambiano.

    partial void OnValueChanged(string value)
    {
        IsModified = true;
    }

    partial void OnCommentChanged(string value)
    {
        IsModified = true;
    }
}