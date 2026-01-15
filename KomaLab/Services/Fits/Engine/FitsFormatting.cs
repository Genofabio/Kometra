using System;
using System.Text;
using KomaLab.Models.Fits;

namespace KomaLab.Services.Fits.Engine;

public static class FitsFormatting
{
    private const int CardLength = 80;

    public static FitsCard ParseLine(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Length < 8) 
            return new FitsCard { Key = "", OriginalRawString = line, IsCommentStyle = true };

        // 1. Estrazione Chiave Standard (Primi 8 caratteri)
        string keyChunk = line.Substring(0, 8).Trim();
        string upperKey = keyChunk.ToUpper();

        // 2. GESTIONE SPECIALE: HIERARCH (Chiavi lunghe con spazi)
        if (upperKey == "HIERARCH")
        {
            // Formato: HIERARCH [Spazio] [Chiave Lunga] [=] [Valore]
            int eqIndexH = line.IndexOf('=');
            if (eqIndexH > 8) // L'uguale deve essere dopo "HIERARCH"
            {
                // La chiave è tutto ciò che sta tra l'inizio e l'uguale
                string fullKey = line.Substring(0, eqIndexH).Trim();
                
                string dataPartH = line.Substring(eqIndexH + 1);
                return ParseValueAndComment(fullKey, dataPartH, line);
            }
        }

        // 3. Rilevamento Tipo Commento
        if (upperKey == "HISTORY" || upperKey == "COMMENT" || upperKey == "END" || string.IsNullOrWhiteSpace(keyChunk))
        {
            string commentContent = line.Length > 8 ? line.Substring(8).TrimEnd() : "";
            return new FitsCard 
            { 
                Key = keyChunk, 
                Value = null, 
                Comment = commentContent,
                IsCommentStyle = true,
                OriginalRawString = line
            };
        }

        // 4. Parsing Card Standard (KEY = VALUE / COMMENT)
        int eqIndex = line.IndexOf('=');

        // Se non troviamo l'uguale (e non è HIERARCH o commento), è una riga corrotta o custom
        // Trattiamola come commento per non perdere dati.
        if (eqIndex < 0)
        {
             return new FitsCard 
             { 
                 Key = keyChunk, 
                 Comment = line.Length > 8 ? line.Substring(8).Trim() : "",
                 IsCommentStyle = true,
                 OriginalRawString = line
             };
        }
        
        // Verifica di sicurezza: l'uguale per le card standard deve essere entro i primi 8+1 caratteri?
        // In realtà lo standard dice che l'uguale è al carattere 9 (indice 8), ma molti file non lo rispettano.
        // Accettiamo l'uguale ovunque, ma la chiave è limitata a 8 caratteri per le card non-Hierarch.
        // Se eqIndex > 8, tecnicamente è una violazione standard se non è HIERARCH, 
        // ma noi usiamo keyChunk (primi 8) come chiave.
        
        string dataPart = line.Substring(eqIndex + 1);
        return ParseValueAndComment(keyChunk, dataPart, line);
    }

    // Helper estratto per evitare duplicazione logica tra Standard e HIERARCH
    private static FitsCard ParseValueAndComment(string key, string dataPart, string originalLine)
    {
        int slashIndex = FindCommentSeparator(dataPart);
        
        string valString;
        string comment = "";

        if (slashIndex >= 0)
        {
            valString = dataPart.Substring(0, slashIndex).Trim();
            comment = dataPart.Substring(slashIndex + 1).Trim();
        }
        else
        {
            valString = dataPart.Trim();
        }

        // --- CORREZIONE: Pulizia Apici (Unquote) ---
        // Se il valore è una stringa FITS (es: 'MyValue'), rimuoviamo gli apici esterni.
        if (valString.Length >= 2 && valString.StartsWith("'") && valString.EndsWith("'"))
        {
            // Rimuove primo e ultimo carattere
            valString = valString.Substring(1, valString.Length - 2).Trim();
            
            // FITS standard dice che due apici consecutivi '' dentro una stringa contano come uno solo '.
            valString = valString.Replace("''", "'"); 
        }
        // --------------------------------------------

        return new FitsCard
        {
            Key = key, 
            Value = valString, 
            Comment = comment,
            IsCommentStyle = false,
            OriginalRawString = originalLine
        };
    }

    public static string PadTo80(FitsCard card)
    {
        var sb = new StringBuilder(CardLength);

        // Se è HIERARCH, la chiave è lunga e contiene già "HIERARCH ..."
        // Altrimenti è standard (max 8 char)
        if (card.Key.StartsWith("HIERARCH", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(card.Key); // Scriviamo la chiave intera (es: "HIERARCH ESO INS...")
            sb.Append(" = ");    // Aggiungiamo l'uguale con spazi
        }
        else
        {
            // Standard: Chiave 8 char + "= "
            sb.Append(card.Key.PadRight(8, ' '));
            if (!card.IsCommentStyle) sb.Append("= ");
        }

        if (card.IsCommentStyle)
        {
            if (!string.IsNullOrEmpty(card.Comment)) sb.Append(card.Comment);
        }
        else
        {
            string v = card.Value ?? "";
            
            // Allineamento (solo per card standard corte, per HIERARCH scriviamo di seguito)
            if (!card.Key.StartsWith("HIERARCH", StringComparison.OrdinalIgnoreCase) && v.Length < 20)
            {
                sb.Append(v.PadLeft(20, ' '));
            }
            else
            {
                sb.Append(v);
            }

            if (!string.IsNullOrWhiteSpace(card.Comment))
            {
                // Cerchiamo di mettere il commento se c'è spazio
                if (sb.Length < CardLength - 3) 
                {
                    sb.Append(" / ");
                    sb.Append(card.Comment);
                }
            }
        }

        // Padding
        if (sb.Length < CardLength)
        {
            sb.Append(' ', CardLength - sb.Length);
        }
        else if (sb.Length > CardLength)
        {
            return sb.ToString().Substring(0, CardLength);
        }

        return sb.ToString();
    }

    private static int FindCommentSeparator(string text)
    {
        bool inQuote = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\'') inQuote = !inQuote;
            if (text[i] == '/' && !inQuote) return i;
        }
        return -1;
    }
}