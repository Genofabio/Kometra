using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.Models.Fits;

// ---------------------------------------------------------------------------
// FILE: FitsHeaderItem.cs
// DESCRIZIONE:
// Rappresenta una singola entry (chiave/valore/commento) dell'header FITS.
// Sfrutta CommunityToolkit.Mvvm per la gestione automatica delle notifiche.
// ---------------------------------------------------------------------------

public partial class FitsHeaderItem : ObservableObject
{
    /// <summary>
    /// La parola chiave standard FITS (es. "NAXIS", "EXPTIME").
    /// </summary>
    [ObservableProperty]
    private string _key = "";

    /// <summary>
    /// Il valore associato alla chiave.
    /// La modifica di questa proprietà imposta automaticamente IsModified a true.
    /// </summary>
    [ObservableProperty]
    private string _value = "";

    /// <summary>
    /// Il commento opzionale presente nella riga dell'header.
    /// La modifica di questa proprietà imposta automaticamente IsModified a true.
    /// </summary>
    [ObservableProperty]
    private string _comment = "";

    /// <summary>
    /// Indica se l'elemento è di sola lettura (es. parole chiave obbligatorie come SIMPLE o BITPIX).
    /// </summary>
    [ObservableProperty]
    private bool _isReadOnly;

    /// <summary>
    /// Flag che indica se il valore o il commento sono stati modificati dall'utente.
    /// </summary>
    [ObservableProperty]
    private bool _isModified;

    // --- INTERCETTORI (Hook Methods) ---
    // Scattano automaticamente quando le proprietà Value o Comment cambiano.

    partial void OnValueChanged(string value)
    {
        IsModified = true;
    }

    partial void OnCommentChanged(string value)
    {
        IsModified = true;
    }
}