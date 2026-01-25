using System;
using System.Globalization;
using Avalonia.Data.Converters;
using KomaLab.Models.Processing.Enhancement;

namespace KomaLab.Converters;

public class RadialEnhancementDescriptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is RadialEnhancementMode mode)
        {
            return mode switch
            {
                RadialEnhancementMode.InverseRho => 
                    "Compensa il calo di luce moltiplicando i pixel per la distanza dal centro (1/rho). Appiattisce il gradiente centrale svelando getti e strutture esterne. Ideale per lo studio delle polveri, meno adatto per le emissioni di gas.",
                
                RadialEnhancementMode.AzimuthalAverage => 
                    "Divide ogni pixel per la luminosità media del suo anello concentrico (escludendo outlier). Rimuove il gradiente naturale esaltando le variazioni strutturali. Nota: i valori a distanze radiali diverse non sono più confrontabili.",
                
                RadialEnhancementMode.AzimuthalMedian => 
                    "Divide ogni pixel per la mediana del suo anello. Grazie alla robustezza statistica, ignora automaticamente stelle e difetti senza filtri complessi. Ottimo per evidenziare strutture deboli in campi stellari densi.",
                
                RadialEnhancementMode.AzimuthalRenormalization => 
                    "Uniforma il contrasto trattando ogni anello indipendentemente. I pixel di ogni raggio vengono 'stirati' per coprire l'intero intervallo dinamico. Efficace per recuperare dettagli in aree a bassissimo contrasto locale.",
                
                _ => ""
            };
        }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) 
        => throw new NotSupportedException();
}