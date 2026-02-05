using System;
using System.Globalization;
using Avalonia.Data.Converters;
using KomaLab.Models.Processing.Enhancement;

namespace KomaLab.Converters;

public class ImageEnhancementDescriptionConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ImageEnhancementMode mode)
        {
            return mode switch
            {
                // --- Radial & Rotational (Gradienti) ---
                ImageEnhancementMode.LarsonSekaninaStandard => 
                    "Metodo sottrattivo classico. Ruota l'immagine di un piccolo angolo e la sottrae dall'originale. Ideale per evidenziare bordi netti e strutture a spirale (girandola) nei getti della chioma.",
                
                ImageEnhancementMode.LarsonSekaninaSymmetric => 
                    "Evoluzione del metodo standard che somma una rotazione positiva e una negativa. Riduce drasticamente gli artefatti scuri ('buchi') tipici del metodo standard, preservando meglio il contrasto naturale.",

                ImageEnhancementMode.AdaptiveLaplacianRVSF => 
                    "Filtro a pendenza variabile (Radial Variable Slope). Modifica l'intensità del filtraggio in base alla distanza dal nucleo. Ottimizzato per seguire il naturale decadimento della luminosità della cometa.",

                ImageEnhancementMode.AdaptiveLaplacianMosaic => 
                    "Genera un mosaico 4x2 applicando l'RVSF con diverse combinazioni di parametri simultaneamente. Utile per confrontare rapidamente diverse intensità di filtro e trovare quella ottimale.",

                // --- Polar & Digital Models (Geometria) ---
                ImageEnhancementMode.InverseRho => 
                    "Filtro geometrico puro. Compensa la naturale caduta di luce (1/raggio) tipica delle comete. Appiattisce il forte bagliore centrale permettendo di vedere i dettagli deboli della polvere esterna.",

                ImageEnhancementMode.RadialWeightedModel =>
                    "Modello Radiale Pesato (R.W.M.). Sottrae il fondo cielo e moltiplica ogni pixel per la sua distanza dal centro. Simile all'Inverse Rho ma spesso offre un contrasto migliore nelle zone periferiche.",

                ImageEnhancementMode.MedianComaModel =>
                    "Modello della Chioma Mediana (M.C.M.). Costruisce un modello sintetico della cometa analizzando la mediana degli anelli concentrici e lo sottrae. È il metodo più robusto per isolare getti e shell senza creare artefatti geometrici.",

                // --- Azimuthal Filters (Polari) ---
                ImageEnhancementMode.AzimuthalAverage => 
                    "Calcola la luminosità media di ogni anello concentrico e divide l'immagine per questo modello. Rimuove la simmetria radiale perfetta, lasciando visibili solo le asimmetrie (getti, code).",

                ImageEnhancementMode.AzimuthalMedian => 
                    "Simile alla media azimutale, ma utilizza la mediana. È molto più efficace nel gestire campi stellari densi, impedendo alle stelle di influenzare il calcolo del modello di fondo.",

                ImageEnhancementMode.AzimuthalRenormalization => 
                    "Normalizza il contrasto locale anello per anello. Invece di mostrare la luminosità assoluta, mostra quanto un dettaglio è 'anomalo' rispetto ai suoi vicini sullo stesso raggio. Estremamente potente per dettagli elusivi.",

                // --- Feature Extraction (Morfologia) ---
                ImageEnhancementMode.FrangiVesselnessFilter =>
                    "Analisi basata sulla curvatura (Hessiana). Progettato per identificare strutture tubolari o filamentose. Eccellente per isolare getti sottili e code ioniche ignorando le stelle puntiformi.",

                ImageEnhancementMode.StructureTensorCoherence =>
                    "Analizza la coerenza del flusso locale. Evidenzia le regioni dove la texture segue una direzione precisa (come flussi di gas e code), sopprimendo il rumore di fondo caotico.",

                ImageEnhancementMode.WhiteTopHatExtraction =>
                    "Filtro morfologico che estrae dettagli luminosi più piccoli della dimensione del kernel specificato, rimuovendo completamente il gradiente di fondo a bassa frequenza.",

                // --- Local Contrast ---
                ImageEnhancementMode.UnsharpMaskingMedian => 
                    "Tecnica di maschera di contrasto che utilizza una mediana per sfocare il fondo. Sottrae le basse frequenze per aumentare drasticamente la nitidezza dei bordi e delle strutture fini.",

                ImageEnhancementMode.ClaheLocalContrast =>
                    "Equalizzazione adattiva dell'istogramma (CLAHE). Massimizza il contrasto locale lavorando su piccole sezioni dell'immagine. Utile per visualizzare dettagli sia nel nucleo luminoso che nella coda scura.",

                ImageEnhancementMode.AdaptiveLocalNormalization =>
                    "Normalizzazione statistica locale (Local Z-Score). Ricalcola ogni pixel basandosi sulla media e deviazione standard dei vicini. Metodo scientifico ideale per massimizzare la visibilità di strutture nascoste nel rumore.",
                
                _ => "Seleziona una modalità per visualizzare la descrizione e i parametri."
            };
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}