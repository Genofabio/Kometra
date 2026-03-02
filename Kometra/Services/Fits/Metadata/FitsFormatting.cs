using System;
using System.Globalization;
using System.Text;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;

namespace Kometra.Services.Fits.Metadata;

public static class FitsFormatting
{
    private const int CardLength = 80;

    public static FitsCard ParseLine(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Length < 8) 
            return new FitsCard(Key: "", IsCommentStyle: true);

        string key = line.Substring(0, 8).Trim().ToUpper();

        // CASO A: END
        if (key == "END") return new FitsCard(Key: "END", IsCommentStyle: true);

        // CASO B: HIERARCH
        if (key == "HIERARCH")
        {
            int eqIndex = line.IndexOf('=');
            if (eqIndex > 8)
            {
                string fullKey = line.Substring(0, eqIndex).Trim();
                return ParseValueAndComment(fullKey, line.Substring(eqIndex + 1), false);
            }
        }

        // CASO C: COMMENT-STYLE (HISTORY, COMMENT, o senza '=')
        // Standard FITS: HISTORY e COMMENT non hanno mai l'uguale.
        bool hasEquals = line.Length > 8 && line[8] == '=';
        if (!hasEquals || key == "HISTORY" || key == "COMMENT" || string.IsNullOrWhiteSpace(key))
        {
            // Tutto ciò che segue la chiave è contenuto libero (non c'è value/comment separati)
            string content = line.Length > 8 ? line.Substring(8).Trim() : "";
            // Per questi tipi, mettiamo tutto nel Comment o Value? 
            // Per uniformità con la scrittura, lo mettiamo come Value "raw" o gestiamo IsCommentStyle=true
            return new FitsCard(key, Value: null, Comment: content, IsCommentStyle: true);
        }

        // CASO D: STANDARD
        return ParseValueAndComment(key, line.Substring(9), false);
    }

    private static FitsCard ParseValueAndComment(string key, string dataPart, bool isCommentStyle)
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

        // Rimuovi apici solo se presenti
        if (valString.StartsWith("'") && valString.EndsWith("'") && valString.Length >= 2)
        {
            valString = valString.Substring(1, valString.Length - 2).Replace("''", "'").TrimEnd();
        }

        return new FitsCard(key, valString, comment, isCommentStyle);
    }

    public static string PadTo80(FitsCard card)
    {
        var sb = new StringBuilder(CardLength);
        
        // 1. END KEYWORD
        if (card.Key == "END") return "END".PadRight(CardLength, ' ');

        string keyUpper = card.Key.Trim().ToUpperInvariant();
        bool isCommentKey = keyUpper == "HISTORY" || keyUpper == "COMMENT" || string.IsNullOrEmpty(keyUpper);

        // 2. SCRITTURA CHIAVE
        if (keyUpper.StartsWith("HIERARCH"))
        {
            sb.Append(keyUpper).Append("= ");
        }
        else
        {
            sb.Append(keyUpper.PadRight(8, ' '));
            
            // AGGIUNTA UGUALE: Solo se NON è una chiave di commento e NON è IsCommentStyle
            if (!card.IsCommentStyle && !isCommentKey) 
            {
                sb.Append("= ");
            }
        }

        // 3. SCRITTURA VALORE / CONTENUTO
        // Se è HISTORY/COMMENT, scriviamo direttamente il valore (o commento) senza formattazione FITS
        if (isCommentKey || card.IsCommentStyle)
        {
            // Uniamo Value e Comment se esistono, preferendo Value se presente
            string rawContent = (card.Value ?? "") + (card.Comment ?? "");
            // IMPORTANTE: HISTORY/COMMENT non hanno apici, sono testo libero
            sb.Append(rawContent); 
        }
        else
        {
            // Valore Standard (formattato FITS)
            string formattedValue = FormatValue(card.Value, keyUpper.StartsWith("HIERARCH"));
            sb.Append(formattedValue);

            // Commento Standard (con slash)
            if (!string.IsNullOrWhiteSpace(card.Comment))
            {
                // Calcola spazio rimanente per il commento
                int currentLen = sb.Length;
                if (currentLen < CardLength - 3) // Spazio minimo per " / "
                {
                    sb.Append(" / ").Append(card.Comment);
                }
            }
        }

        // 4. PADDING FINALE A 80 CARATTERI
        string result = sb.ToString();
        if (result.Length > CardLength) 
            return result.Substring(0, CardLength); // Tronca se troppo lungo
        
        return result.PadRight(CardLength, ' ');
    }

    private static string FormatValue(string? value, bool isHierarch)
    {
        if (value == null) return ""; // Campo valore vuoto

        // Rilevamento Tipi Logici e Numerici (non vanno quotati)
        // Nota: Qui assumiamo che il valore in ingresso sia "pulito" (es. "T", "1.23")
        // Se contiene apici, è sicuramente una stringa.
        bool isLogical = value == "T" || value == "F";
        
        // Controllo numerico rigoroso (evita falsi positivi su stringhe che iniziano con numeri)
        bool isNumeric = double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);

        // Se è logico o numerico, lo scriviamo così com'è (allineato a destra)
        if (isLogical || isNumeric)
        {
            // Hierarch non ha standard di posizionamento rigido, FITS standard sì (colonna 30)
            // Ma per semplicità e robustezza writer, spesso si usa solo spazio.
            // Lo standard richiede che il valore finisca alla colonna 30 se possibile, ma padLeft 20 è una buona approx.
            return isHierarch ? value : value.PadLeft(20, ' ');
        }

        // GESTIONE STRINGHE (FIX TRIPLE QUOTES)
        // Se la stringa è già quotata (inizia e finisce con '), non aggiungiamo altri apici.
        string trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith("'") && trimmed.EndsWith("'"))
        {
            // Già formattata correttamente (es. dal MetadataService)
            // La allineiamo solo se necessario (FITS standard preferisce >= 8 chars)
            return isHierarch ? trimmed : trimmed.PadRight(8, ' ');
        }

        // Altrimenti, aggiungiamo gli apici standard
        string escaped = value.Replace("'", "''");
        return $"'{escaped}'".PadRight(8, ' '); // Padding minimo a destra per estetica
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