using System.Text;
using KomaLab.Models.Fits;

// Importante: Usa il modello Core

namespace KomaLab.Services.Fits.Engine;

/// <summary>
/// Gestisce le regole rigide di formattazione del formato FITS (80 caratteri, padding, ecc.).
/// </summary>
public static class FitsFormatting
{
    private const int CardLength = 80;
    
    // FITS Standard: il valore inizia tipicamente alla colonna 11 (indice 10) o 31 (indice 30)
    // Noi cerchiamo di stare sul sicuro.

    /// <summary>
    /// Analizza una riga grezza di 80 caratteri (ASCII) e la converte in un oggetto FitsCard strutturato.
    /// </summary>
    public static FitsCard ParseLine(string line)
    {
        // Se la riga è troppo corta (file corrotto?), la salviamo come commento grezzo
        if (string.IsNullOrEmpty(line) || line.Length < 8) 
            return new FitsCard { Key = "", OriginalRawString = line, IsCommentStyle = true };

        // 1. Estrazione Chiave (Primi 8 caratteri)
        string key = line.Substring(0, 8).Trim();
        string upperKey = key.ToUpper();

        // 2. Rilevamento Tipo di Card
        // Le card di tipo commento (HISTORY, COMMENT, END o vuote) non hanno il segno '='
        // HIERARCH è un caso speciale che gestiamo come standard per ora.
        if (upperKey == "HISTORY" || upperKey == "COMMENT" || upperKey == "END" || string.IsNullOrWhiteSpace(key))
        {
            string commentContent = line.Length > 8 ? line.Substring(8).TrimEnd() : "";
            
            return new FitsCard 
            { 
                Key = key, 
                Value = null, 
                Comment = commentContent,
                IsCommentStyle = true,
                OriginalRawString = line
            };
        }

        // 3. Parsing Card Standard (KEY = VALUE / COMMENT)
        int eqIndex = line.IndexOf('=');

        // Se non troviamo l'uguale, trattiamo tutto come un commento (fallback di sicurezza)
        if (eqIndex < 0)
        {
             return new FitsCard 
             { 
                 Key = key, 
                 Comment = line.Length > 8 ? line.Substring(8).Trim() : "",
                 IsCommentStyle = true,
                 OriginalRawString = line
             };
        }

        // Separiamo la parte dati (dopo l'=)
        string dataPart = line.Substring(eqIndex + 1);
        
        // Dobbiamo separare il VALORE dal COMMENTO.
        // Il separatore è lo slash '/', ma attenzione: lo slash può essere contenuto tra apici (es. 'm/s').
        // Quindi dobbiamo cercare il primo slash che è FUORI dalle virgolette.
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

        return new FitsCard
        {
            Key = key,
            Value = valString, // Nota: Manteniamo gli apici qui se presenti. Li puliremo nel FitsHeader.GetStringValue
            Comment = comment,
            IsCommentStyle = false,
            OriginalRawString = line
        };
    }

    /// <summary>
    /// Genera la stringa esatta di 80 caratteri da scrivere nel file.
    /// Fondamentale per il salvataggio.
    /// </summary>
    public static string PadTo80(FitsCard card)
    {
        var sb = new StringBuilder(CardLength);

        // 1. Chiave (8 char, allineata a sinistra)
        sb.Append(card.Key.PadRight(8, ' '));

        if (card.IsCommentStyle)
        {
            // Caso HISTORY / COMMENT: Scriviamo direttamente il contenuto
            if (!string.IsNullOrEmpty(card.Comment)) 
            {
                sb.Append(card.Comment);
            }
        }
        else
        {
            // Caso Standard: "= VALUE / COMMENT"
            sb.Append("= ");

            // Gestione Valore
            string v = card.Value ?? "";
            
            // Logica di allineamento per estetica (non obbligatoria ma raccomandata)
            // Se il valore è corto (es. numeri), lo allineiamo a destra per farlo finire circa a colonna 30
            if (v.Length < 20)
            {
                sb.Append(v.PadLeft(20, ' '));
            }
            else
            {
                sb.Append(v);
            }

            // Aggiunta Commento
            if (!string.IsNullOrWhiteSpace(card.Comment))
            {
                // Se c'è spazio, aggiungiamo il separatore
                if (sb.Length < CardLength - 3) 
                {
                    sb.Append(" / ");
                    sb.Append(card.Comment);
                }
            }
        }

        // Padding finale obbligatorio a 80 caratteri
        if (sb.Length < CardLength)
        {
            sb.Append(' ', CardLength - sb.Length);
        }
        else if (sb.Length > CardLength)
        {
            // Se siamo andati lunghi, tronchiamo brutalmente (FITS non ammette >80)
            return sb.ToString().Substring(0, CardLength);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Trova l'indice del carattere '/' che funge da separatore di commento,
    /// ignorando gli slash contenuti tra apici singoli ('...').
    /// </summary>
    private static int FindCommentSeparator(string text)
    {
        bool inQuote = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\'') inQuote = !inQuote; // Toggle stato quote
            
            // Se troviamo uno slash e NON siamo tra virgolette, è lui!
            if (text[i] == '/' && !inQuote) return i;
        }
        return -1; // Nessun commento trovato
    }
}