namespace KomaLab.Models;

/// <summary>
/// Metodi matematici per trovare il centro di una stella o del nucleo di una cometa.
/// Usato da AlignmentService e ImageAnalysisService.
/// </summary>
public enum CenteringMethod
{
    /// <summary>
    /// Calcola il centro di massa (baricentro) pesato sull'intensità.
    /// Buono per oggetti diffusi o molto sfocati.
    /// </summary>
    Centroid,
    
    /// <summary>
    /// Trova il pixel più luminoso e applica un fit parabolico locale.
    /// Ottimo per stelle puntiformi.
    /// </summary>
    Peak,
    
    /// <summary>
    /// Esegue un fit Gaussiano 2D (bell curve).
    /// Il metodo scientificamente più accurato per le PSF astronomiche.
    /// </summary>
    GaussianFit,
    
    /// <summary>
    /// Workflow completo: Blob Detection automatica + Fit Gaussiano sulla regione.
    /// È il metodo più robusto per l'allineamento automatico.
    /// </summary>
    LocalRegion
}

/// <summary>
/// Modalità di funzionamento dello strumento di allineamento.
/// Usato da AlignmentToolViewModel e AlignmentService.
/// </summary>
public enum AlignmentMode
{
    /// <summary>
    /// Il sistema tenta di trovare e allineare il soggetto in tutte le immagini automaticamente.
    /// </summary>
    Automatic,
    
    /// <summary>
    /// L'utente definisce la posizione nella prima e nell'ultima immagine;
    /// le intermedie vengono calcolate per interpolazione lineare (traiettoria costante).
    /// </summary>
    Guided,
    
    /// <summary>
    /// L'utente deve cliccare manualmente il soggetto su ogni singola immagine.
    /// </summary>
    Manual,
    
    Stars
}

/// <summary>
/// Macchina a stati per l'interfaccia utente dello strumento di allineamento.
/// Definisce cosa mostrare (Bottoni, Liste, Progress Bar).
/// </summary>
public enum AlignmentState
{
    /// <summary>
    /// Stato iniziale: l'utente naviga, seleziona il target e imposta i parametri.
    /// Visibile: Bottone "Calcola", controlli raggio.
    /// </summary>
    Initial,
    
    /// <summary>
    /// Il sistema sta calcolando le coordinate (fase di analisi).
    /// Visibile: Barra di caricamento "Calcolo centri in corso...".
    /// </summary>
    Calculating,
    
    /// <summary>
    /// I centri sono stati calcolati e mostrati in lista. L'utente può verificarli.
    /// Visibile: Lista coordinate, Bottoni "Applica" / "Annulla".
    /// </summary>
    ResultsReady,
    
    /// <summary>
    /// L'utente ha confermato e il sistema sta generando le nuove immagini (fase di warp).
    /// Visibile: Barra di caricamento "Allineamento in corso...".
    /// </summary>
    Processing   
}