using System;
using System.Globalization;
using System.Text;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Fits.Metadata;

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

        // CASO C: COMMENT-STYLE
        bool hasEquals = line.Length > 8 && line[8] == '=';
        if (!hasEquals || key == "HISTORY" || key == "COMMENT" || string.IsNullOrWhiteSpace(key))
        {
            string content = line.Length > 8 ? line.Substring(8).Trim() : "";
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

        if (valString.StartsWith("'") && valString.EndsWith("'") && valString.Length >= 2)
        {
            valString = valString.Substring(1, valString.Length - 2).Replace("''", "'").TrimEnd();
        }

        // Utilizzo del costruttore posizionale del record
        return new FitsCard(key, valString, comment, isCommentStyle);
    }

    public static string PadTo80(FitsCard card)
    {
        var sb = new StringBuilder(CardLength);
        if (card.Key == "END") return "END".PadRight(CardLength, ' ');

        if (card.Key.StartsWith("HIERARCH", StringComparison.OrdinalIgnoreCase))
            sb.Append(card.Key).Append("= ");
        else
        {
            sb.Append(card.Key.PadRight(8, ' '));
            if (!card.IsCommentStyle) sb.Append("= ");
        }

        if (card.IsCommentStyle)
            sb.Append(card.Comment ?? card.Value ?? "");
        else
        {
            string formattedValue = FormatValue(card.Value, card.Key.StartsWith("HIERARCH"));
            sb.Append(formattedValue);

            if (!string.IsNullOrWhiteSpace(card.Comment))
            {
                if (sb.Length < CardLength - 3)
                    sb.Append(" / ").Append(card.Comment);
            }
        }

        string result = sb.ToString();
        return result.Length > CardLength ? result.Substring(0, CardLength) : result.PadRight(CardLength, ' ');
    }

    private static string FormatValue(string? value, bool isHierarch)
    {
        if (value == null) return "";
        bool isLogical = value == "T" || value == "F";
        bool isNumeric = double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);

        if (isLogical || isNumeric)
            return isHierarch ? value : value.PadLeft(20, ' ');

        string escaped = value.Replace("'", "''");
        return $"'{escaped,-8}'";
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