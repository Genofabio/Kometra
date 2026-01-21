using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry;

namespace KomaLab.Services.Astrometry;

public class JplHorizonsService : IJplHorizonsService
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://ssd.jpl.nasa.gov/api/horizons.api";

    public JplHorizonsService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<(double Ra, double Dec)?> GetEphemerisAsync(
        string objectName, 
        DateTime observationTime, 
        GeographicLocation? observerLocation = null,
        CancellationToken token = default)
    {
        // 1. PULIZIA SMART DEL NOME
        var (desName, fullName) = ExtractSmartNames(objectName);

        // Debug Log
        System.Diagnostics.Debug.WriteLine($"[JPL LOGIC] Input: '{objectName}' -> DES: '{desName}', Full: '{fullName}'");

        // 2. STRATEGIA A: RICERCA DIRETTA PER DESIGNAZIONE (DES=...)
        // Ideale per "67P", "12P", ecc.
        string response = await CallJplApi($"DES={desName};", observationTime, observerLocation, token);

        if (IsSuccessResponse(response)) return ParseJplResponse(response);

        // 3. STRATEGIA B: RICERCA GENERICA (Fallback)
        if (ContainsError(response))
        {
            System.Diagnostics.Debug.WriteLine("[JPL LOGIC] Strategy A failed. Trying Strategy B (Name search)...");

            // FIX: Rimosse le virgolette singole manuali che rompevano l'URL
            // Proviamo con virgolette doppie per stringa esatta
            response = await CallJplApi($"\"{fullName}\";", observationTime, observerLocation, token);
            if (IsSuccessResponse(response)) return ParseJplResponse(response);
            
            // Ultimo tentativo: prova senza virgolette, solo il nome nudo e crudo
             if (ContainsError(response))
             {
                 System.Diagnostics.Debug.WriteLine("[JPL LOGIC] Strategy B (Quoted) failed. Trying Strategy B (Raw)...");
                 response = await CallJplApi($"{fullName};", observationTime, observerLocation, token);
                 if (IsSuccessResponse(response)) return ParseJplResponse(response);
             }
        }

        // 4. STRATEGIA C: GESTIONE AMBIGUITÀ (Lista ID)
        if (IsAmbiguousResponse(response))
        {
            string bestId = FindBestIdFromAmbiguityList(response, observationTime.Year);
            System.Diagnostics.Debug.WriteLine($"[JPL LOGIC] Ambiguity detected. Best ID candidate: '{bestId}'");

            if (!string.IsNullOrEmpty(bestId))
            {
                response = await CallJplApi(bestId, observationTime, observerLocation, token);
                if (IsSuccessResponse(response)) return ParseJplResponse(response);
            }
        }

        System.Diagnostics.Debug.WriteLine("[JPL LOGIC] All strategies failed.");
        return null;
    }

    // =======================================================================
    // HELPERS DI LOGICA
    // =======================================================================

    private (string DesName, string FullName) ExtractSmartNames(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return ("", "");

        // Rimuove parentesi extra: "C/2023 A3 (Atlas)" -> "C/2023 A3"
        string clean = rawName.Split('(')[0].Trim();

        // Regex per Comete Periodiche (es. 67P, 12P, etc.)
        // Se inizia con "numero + P + /", prendiamo solo "numero + P" per la DES
        var match = Regex.Match(clean, @"^(\d+P)/");
        if (match.Success)
        {
            return (match.Groups[1].Value, clean); // ("67P", "67P/Churyumov...")
        }

        // Regex per Comete Non Periodiche (es. C/2023 A3)
        // Per queste, la DES solitamente è tutto il codice
        return (clean, clean);
    }

    private async Task<string> CallJplApi(
        string command, 
        DateTime time, 
        GeographicLocation? loc, 
        CancellationToken token)
    {
        var queryParams = new System.Collections.Generic.List<string>
        {
            "format=text",
            // FIX: Command escaping corretto
            $"COMMAND='{Uri.EscapeDataString(command)}'",
            "OBJ_DATA='NO'",
            "MAKE_EPHEM='YES'",
            "EPHEM_TYPE='OBSERVER'",
            "QUANTITIES='1'", 
            "CSV_FORMAT='YES'",
            "START_TIME='" + time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "'",
            "STOP_TIME='" + time.AddMinutes(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "'",
            "STEP_SIZE='1m'"
        };

        if (loc != null)
        {
            queryParams.Add("CENTER='coord@399'");
            queryParams.Add("COORD_TYPE='GEODETIC'");
            string siteCoord = $"'{loc.Longitude.ToString(CultureInfo.InvariantCulture)},{loc.Latitude.ToString(CultureInfo.InvariantCulture)},{loc.AltitudeKm.ToString(CultureInfo.InvariantCulture)}'";
            queryParams.Add($"SITE_COORD={siteCoord}");
        }
        else
        {
            queryParams.Add("CENTER='500'");
        }

        var builder = new UriBuilder(BaseUrl) { Query = string.Join("&", queryParams) };
        string fullUrl = builder.ToString();

        // ---------------------------------------------------------
        // DEBUG LOGS (RIPRISTINATI)
        // ---------------------------------------------------------
        System.Diagnostics.Debug.WriteLine("--------------------------------------------------");
        System.Diagnostics.Debug.WriteLine($"[JPL REQUEST] Internal Command: {command}");
        System.Diagnostics.Debug.WriteLine($"[JPL URL]: {fullUrl}");
        System.Diagnostics.Debug.WriteLine("--------------------------------------------------");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(20)); // Timeout aumentato a 20s
            
            var response = await _httpClient.GetStringAsync(fullUrl, cts.Token);

            // ---------------------------------------------------------
            // DEBUG RESPONSE
            // ---------------------------------------------------------
            System.Diagnostics.Debug.WriteLine($"[JPL RESPONSE LENGTH]: {response.Length} chars");
            string preview = response.Length > 2000 ? response.Substring(0, 2000) + "... [TRUNCATED]" : response;
            System.Diagnostics.Debug.WriteLine($"[JPL RESPONSE BODY]:\n{preview}");
            System.Diagnostics.Debug.WriteLine("--------------------------------------------------");

            return response;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[JPL ERROR]: {ex.GetType().Name} - {ex.Message}");
            return string.Empty;
        }
    }

    private (double Ra, double Dec)? ParseJplResponse(string response)
    {
        int start = response.IndexOf("$$SOE", StringComparison.Ordinal);
        int end = response.IndexOf("$$EOE", StringComparison.Ordinal);
        
        if (start == -1 || end == -1) return null;

        var dataSection = response.Substring(start + 5, end - (start + 5)).Trim();
        var lines = dataSection.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var cleanLine = line.Trim();
            if (string.IsNullOrEmpty(cleanLine)) continue;

            var columns = cleanLine.Split(',');
            
            string? rawRa = null;
            string? rawDec = null;

            // Iteriamo sulle colonne cercando RA e DEC
            foreach (var rawCol in columns)
            {
                string col = rawCol.Trim();
                if (string.IsNullOrWhiteSpace(col)) continue;

                // 1. FILTRO DI SICUREZZA:
                // La data contiene lettere (es. "Feb"), le coordinate NO.
                // Se c'è una lettera, saltiamo la colonna.
                bool hasLetters = false;
                foreach (char c in col)
                {
                    if (char.IsLetter(c)) 
                    { 
                        hasLetters = true; 
                        break; 
                    }
                }
                if (hasLetters) continue;

                // 2. IDENTIFICAZIONE COORDINATA:
                // Deve iniziare con un numero, un '+' o un '-' (Sì, il MENO è controllato qui!)
                // E deve contenere uno spazio o un punto (formato "HH MM SS" o decimale)
                bool startsWithSignOrDigit = char.IsDigit(col[0]) || col[0] == '-' || col[0] == '+';
                bool hasStructure = col.Contains(" ") || col.Contains(".");

                if (startsWithSignOrDigit && hasStructure)
                {
                    if (rawRa == null) rawRa = col; // La prima che troviamo è RA
                    else 
                    {
                        rawDec = col; // La seconda è DEC
                        break; // Trovate entrambe, usciamo dal loop colonne
                    }
                }
            }

            if (rawRa != null && rawDec != null)
            {
                double? ra = AstroParser.ParseDegrees(rawRa);
                double? dec = AstroParser.ParseDegrees(rawDec);

                if (ra.HasValue && dec.HasValue)
                {
                    double finalRa = ra.Value;
                    // Se RA è in formato ore (ha spazi o due punti), converti in gradi
                    if (rawRa.Contains(" ") || rawRa.Contains(":")) 
                    {
                        finalRa *= 15.0;
                    }

                    System.Diagnostics.Debug.WriteLine($"[JPL PARSER] Success -> RA: {finalRa}, DEC: {dec.Value}");
                    return (finalRa, dec.Value);
                }
            }
        }

        System.Diagnostics.Debug.WriteLine($"[JPL PARSER] Failed to find valid numeric coordinates in:\n{dataSection}");
        return null;
    }

    private string FindBestIdFromAmbiguityList(string response, int targetYear)
    {
        // Regex per catturare il Record # (prima colonna) e l'Epoch-yr (seconda colonna)
        // Esempio riga: " 90000700     2006      67P ..."
        var regex = new Regex(@"^\s*(\d+)\s+(\d{4})", RegexOptions.Multiline);
        
        string bestId = "";
        int minDiff = int.MaxValue;

        foreach (Match match in regex.Matches(response))
        {
            if (match.Value.Contains("Epoch") || match.Value.Contains("JDT")) continue;

            string id = match.Groups[1].Value; // Cattura "90000700"
            
            if (int.TryParse(match.Groups[2].Value, out int epochYear))
            {
                int diff = Math.Abs(epochYear - targetYear);
                if (diff < minDiff) 
                { 
                    minDiff = diff; 
                    bestId = id; 
                }
            }
        }

        // FIX CRITICO: Se è un ID numerico (Record #), NON AGGIUNGERE "DES="!
        // JPL vuole solo "90000700;" per cercare per ID.
        if (string.IsNullOrEmpty(bestId)) return "";
        
        return $"{bestId};"; 
    }

    private bool IsSuccessResponse(string r) => !string.IsNullOrEmpty(r) && r.Contains("$$SOE");
    private bool ContainsError(string r) => string.IsNullOrEmpty(r) || r.Contains("No matches found") || r.Contains("Error");
    private bool IsAmbiguousResponse(string r) => !string.IsNullOrEmpty(r) && (r.Contains("Multiple major-bodies") || r.Contains("Matching small-bodies"));
}