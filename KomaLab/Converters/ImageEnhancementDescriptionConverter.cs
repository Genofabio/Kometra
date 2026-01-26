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
                // --- Radial & Rotational ---
                ImageEnhancementMode.LarsonSekaninaStandard => 
                    "Metodo standard (Sottrattivo: $I - Rot$). Ruota l'immagine di un piccolo angolo e la sottrae dall'originale. Ideale per evidenziare la morfologia a 'girandola' dei getti.",
                
                ImageEnhancementMode.LarsonSekaninaSymmetric => 
                    "Metodo simmetrico ($2I - Rot(+) - Rot(-)$). Riduce gli artefatti lineari tipici del metodo standard e aumenta il contrasto delle strutture.",

                ImageEnhancementMode.AdaptiveLaplacianRVSF => 
                    "Radial Variable Slope Filter. Filtro radiale adattivo ($R = A + B \\cdot \\rho^N$) ottimizzato per esaltare strutture che si espandono radialmente.",

                ImageEnhancementMode.AdaptiveLaplacianMosaic => 
                    "Genera un mosaico (4x2) applicando l'RVSF con 8 combinazioni di parametri diverse per trovare quella ottimale.",

                ImageEnhancementMode.InverseRho => 
                    "Compensa il calo di luce ($1/\\rho$). Appiattisce il gradiente centrale svelando getti esterni. Ideale per lo studio delle polveri.",

                ImageEnhancementMode.AzimuthalAverage => 
                    "Divide ogni pixel per la media del suo anello (escludendo outlier). Rimuove il gradiente naturale esaltando le variazioni strutturali.",

                ImageEnhancementMode.AzimuthalMedian => 
                    "Divide ogni pixel per la mediana del suo anello. Molto robusto contro i campi stellari densi.",

                ImageEnhancementMode.AzimuthalRenormalization => 
                    "Normalizza il contrasto locale anello per anello (Mclaughlin/Z-Score). Efficace per recuperare dettagli debolissimi.",

                // --- Feature Extraction ---
                ImageEnhancementMode.FrangiVesselnessFilter =>
                    "Analisi Hessiana multiscala. Identifica strutture filamentose (vasi/getti) basandosi sulla curvatura locale, sopprimendo le stelle.",

                ImageEnhancementMode.StructureTensorCoherence =>
                    "Calcola l'anisotropia locale (Tensore) per isolare flussi direzionali e getti lineari, attenuando il rumore puntiforme.",

                ImageEnhancementMode.WhiteTopHatExtraction =>
                    "Operazione morfologica ($I - Apertura$). Estrae dettagli luminosi più piccoli del kernel ignorando il bagliore di fondo.",

                // --- Local Contrast ---
                ImageEnhancementMode.UnsharpMaskingMedian => 
                    "Sottrae un background sfocato (mediana). Metodo veloce per isolare strutture ad alta frequenza.",

                ImageEnhancementMode.ClaheLocalContrast =>
                    "Equalizzazione istogramma adattiva (16-bit). Massimizza il contrasto locale dinamico in zone HDR.",

                ImageEnhancementMode.AdaptiveLocalNormalization =>
                    "Normalizzazione statistica ($z-score$). Metodo scientifico 100% Float che preserva l'integrità del dato FITS.",
                
                _ => "Seleziona una modalità per vedere la descrizione."
            };
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}