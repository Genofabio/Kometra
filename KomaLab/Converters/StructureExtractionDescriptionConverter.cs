using KomaLab.Models.Processing.Enhancement;
using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace KomaLab.Converters;

public class StructureExtractionDescriptionConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is StructureExtractionMode mode)
        {
            return mode switch
            {
                StructureExtractionMode.LarsonSekaninaStandard => 
                    "Metodo standard (Sottrattivo: $I - Rot$). Ruota l'immagine di un piccolo angolo e la sottrae dall'originale. Ideale per evidenziare la morfologia a 'girandola' dei getti e delle spirali nella chioma interna.",
                
                StructureExtractionMode.LarsonSekaninaSymmetric => 
                    "Metodo simmetrico ($2I - Rot(+) - Rot(-)$). Riduce gli artefatti lineari tipici del metodo standard e aumenta il contrasto delle strutture, ma tende ad incrementare il rumore di fondo.",

                StructureExtractionMode.UnsharpMaskingMedian => 
                    "Sottrae un background artificiale generato tramite mediana locale. È un metodo veloce per isolare strutture ad alta frequenza (getti sottili) senza la complessità del filtro RVSF completo.",

                StructureExtractionMode.AdaptiveLaplacianRVSF => 
                    "Radial Variable Slope Filter. Utilizza un kernel laplaciano il cui raggio varia con la distanza dal nucleo secondo la formula $R = A + B \\cdot \\rho^N$. Ottimizzato per esaltare strutture che si espandono radialmente.",

                StructureExtractionMode.AdaptiveLaplacianMosaic => 
                    "Genera un'unica immagine mosaico (4x2) applicando l'RVSF con 8 combinazioni diverse dei parametri. Fondamentale per determinare empiricamente i coefficienti migliori per la cometa in esame.",

                StructureExtractionMode.FrangiVesselnessFilter =>
                    "Analisi multiscala dell'Hessiana. Identifica strutture filamentose basandosi sulla curvatura locale. Eccellente per isolare getti deboli e curvi sopprimendo stelle e chioma diffusa.",

                StructureExtractionMode.StructureTensorCoherence =>
                    "Esaltazione tramite Tensore di Struttura. Calcola l'anisotropia locale per isolare i flussi direzionali e i getti lineari, attenuando stelle e rumore isotropico (puntiforme).",

                StructureExtractionMode.WhiteTopHatExtraction =>
                    "Operazione morfologica ($I - Apertura$). Estrae dettagli luminosi più piccoli del kernel ignorando il bagliore a bassa frequenza. Ideale per vedere getti 'annegati' nella luminosità del nucleo.",

                StructureExtractionMode.ClaheLocalContrast =>
                    "Equalizzazione adattiva dell'istogramma (workflow a 16-bit). Aumenta il contrasto locale in modo dinamico, permettendo di visualizzare simultaneamente dettagli in zone scure e molto luminose.",

                StructureExtractionMode.AdaptiveLocalNormalization =>
                    "Normalizzazione statistica locale (LSN). Calcola la deviazione standard locale per ogni pixel ($z-score$). Metodo scientifico 100% Floating Point che preserva l'integrità del dato FITS originale.",
                
                _ => string.Empty
            };
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}