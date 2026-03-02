namespace Kometra.Models.Processing.Stacking
{
    // ---------------------------------------------------------------------------
    // FILE: StackingMode.cs
    // DESCRIZIONE:
    // Enum per gli algoritmi matematici per la combinazione (stacking) di più immagini.
    // Determina come viene calcolato il valore del singolo pixel nel risultato finale.
    // ---------------------------------------------------------------------------

    public enum StackingMode
    {
        /// <summary>
        /// Somma semplice dei pixel. Aumenta il segnale ma satura facilmente.
        /// Utile per conteggi di fotoni o dati grezzi.
        /// </summary>
        Sum,

        /// <summary>
        /// Media aritmetica. Riduce il rumore (SNR migliora con la radice quadrata di N).
        /// Metodo standard per la maggior parte delle integrazioni.
        /// </summary>
        Average,

        /// <summary>
        /// Mediana. Eccellente per rimuovere raggi cosmici, satelliti o pixel caldi
        /// che appaiono solo in alcuni frame.
        /// </summary>
        Median
    }
}