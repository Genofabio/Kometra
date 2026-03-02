namespace Kometra.Models.Processing.Alignment
{
    // ---------------------------------------------------------------------------
    // FILE: AlignmentEnums.cs
    // DESCRIZIONE:
    // Contiene le definizioni dei tipi enumerativi utilizzati nel processo
    // di allineamento e stacking delle immagini astronomiche (FITS).
    // Definisce algoritmi, modalità operative, stati del processo e target fisici.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Algoritmi matematici disponibili per la determinazione del centroide (stella o cometa).
    /// </summary>
    public enum CenteringMethod
    {
        /// <summary>
        /// Calcolo del baricentro pesato sull'intensità dei pixel.
        /// Adatto per oggetti estesi, diffusi o fuori fuoco.
        /// </summary>
        Centroid,

        /// <summary>
        /// Identificazione del pixel di picco e fit parabolico locale.
        /// Ideale per sorgenti puntiformi (stelle) ad alto rapporto segnale/rumore.
        /// </summary>
        Peak,

        /// <summary>
        /// Fit Gaussiano 2D (Point Spread Function).
        /// Metodo scientificamente più rigoroso per astrometria di precisione.
        /// </summary>
        GaussianFit,

        /// <summary>
        /// Rilevamento automatico della regione di interesse (Blob Detection) seguito da fit Gaussiano.
        /// Metodo robusto in presenza di rumore o artefatti.
        /// </summary>
        LocalRegion
    }

    /// <summary>
    /// Strategia temporale per l'identificazione della posizione del soggetto nella sequenza di immagini.
    /// </summary>
    public enum AlignmentMode
    {
        /// <summary>
        /// Algoritmo automatico: il sistema tenta di tracciare il soggetto frame per frame.
        /// </summary>
        Automatic,

        /// <summary>
        /// Interpolazione lineare: basata sulla posizione nota nel primo e nell'ultimo frame.
        /// Assume una velocità di spostamento costante (moto rettilineo uniforme).
        /// </summary>
        Guided,

        /// <summary>
        /// Selezione manuale: le coordinate sono fornite esternamente o dall'operatore per ogni singolo frame.
        /// </summary>
        Manual
    }

    /// <summary>
    /// Rappresenta lo stato logico della macchina a stati del processo di allineamento.
    /// Utilizzato per gestire il flusso di lavoro e la disponibilità dei comandi.
    /// </summary>
    public enum AlignmentState
    {
        /// <summary>
        /// Stato di configurazione. Il sistema è in attesa dei parametri di input e della selezione del target.
        /// </summary>
        Initial,

        /// <summary>
        /// Fase di analisi in corso. Il sistema sta elaborando le coordinate (x,y) per ogni immagine.
        /// </summary>
        Calculating,

        /// <summary>
        /// Analisi completata. I risultati (coordinate) sono disponibili per validazione o correzione.
        /// </summary>
        ResultsReady,

        /// <summary>
        /// Fase di trasformazione in corso. Il sistema sta applicando le traslazioni/rotazioni (Warping) per generare l'output.
        /// </summary>
        Processing
    }

    /// <summary>
    /// Definisce la natura fisica del soggetto di riferimento per l'allineamento.
    /// </summary>
    public enum AlignmentTarget
    {
        /// <summary>
        /// Allineamento sul nucleo della cometa (Tracking non siderale).
        /// Le stelle appariranno strisciate.
        /// </summary>
        Comet,

        /// <summary>
        /// Allineamento sulle stelle di campo (Tracking siderale).
        /// La cometa apparirà mossa in base al suo moto proprio.
        /// </summary>
        Stars
    }
}