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
        var (desName, fullName) = ExtractSmartNames(objectName);
        System.Diagnostics.Debug.WriteLine($"\n[JPL START] Object: '{objectName}' | Observation: {observationTime:yyyy-MM-dd HH:mm}");

        // --- STRATEGIA A: DESIGNAZIONE ---
        System.Diagnostics.Debug.WriteLine($"[JPL] Attempting Strategy A (DES={desName})...");
        string response = await CallJplApi($"DES={desName};", observationTime, observerLocation, token);

        if (IsSuccessResponse(response)) return ParseJplResponse(response);

        // --- STRATEGIA B: RICERCA NOME ---
        if (ContainsError(response))
        {
            System.Diagnostics.Debug.WriteLine("[JPL] Strategy A failed. Trying Strategy B (Quoted Name)...");
            response = await CallJplApi($"\"{fullName}\";", observationTime, observerLocation, token);
            
            if (IsSuccessResponse(response)) return ParseJplResponse(response);
            
             if (ContainsError(response))
             {
                 System.Diagnostics.Debug.WriteLine("[JPL] Strategy B (Quoted) failed. Trying Strategy B (Raw Name)...");
                 response = await CallJplApi($"{fullName};", observationTime, observerLocation, token);
                 if (IsSuccessResponse(response)) return ParseJplResponse(response);
             }
        }

        // --- STRATEGIA C: AMBIGUITÀ ---
        if (IsAmbiguousResponse(response))
        {
            System.Diagnostics.Debug.WriteLine("[JPL] Ambiguity detected. Analyzing list...");
            string bestId = FindBestIdFromAmbiguityList(response, observationTime.Year);

            if (!string.IsNullOrEmpty(bestId))
            {
                System.Diagnostics.Debug.WriteLine($"[JPL] Selected Best ID: {bestId}. Retrying...");
                response = await CallJplApi(bestId, observationTime, observerLocation, token);
                if (IsSuccessResponse(response)) return ParseJplResponse(response);
            }
        }

        System.Diagnostics.Debug.WriteLine("[JPL FINISH] All strategies failed for this object.");
        return null;
    }

    private async Task<string> CallJplApi(string command, DateTime time, GeographicLocation? loc, CancellationToken token)
    {
        var queryParams = new System.Collections.Generic.List<string>
        {
            "format=text",
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
        else { queryParams.Add("CENTER='500'"); }

        var builder = new UriBuilder(BaseUrl) { Query = string.Join("&", queryParams) };
        string fullUrl = builder.ToString();

        System.Diagnostics.Debug.WriteLine($"[JPL REQ] Command: {command}");
        System.Diagnostics.Debug.WriteLine($"[JPL REQ] URL: {fullUrl}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            
            // Usiamo GetAsync per poter loggare lo StatusCode in caso di errore
            var httpResponse = await _httpClient.GetAsync(fullUrl, cts.Token);
            string content = await httpResponse.Content.ReadAsStringAsync();

            if (!httpResponse.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[JPL HTTP ERROR] Code: {httpResponse.StatusCode} | Body: {content.Substring(0, Math.Min(200, content.Length))}");
                return string.Empty;
            }

            System.Diagnostics.Debug.WriteLine($"[JPL RESP] Received {content.Length} chars.");
            return content;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[JPL EXCEPTION] {ex.Message}");
            return string.Empty;
        }
    }

    private (double Ra, double Dec)? ParseJplResponse(string response)
    {
        int start = response.IndexOf("$$SOE", StringComparison.Ordinal);
        int end = response.IndexOf("$$EOE", StringComparison.Ordinal);
        
        if (start == -1 || end == -1)
        {
            System.Diagnostics.Debug.WriteLine("[JPL PARSE] Markers $$SOE/$$EOE not found. Possible content error.");
            return null;
        }

        var dataSection = response.Substring(start + 5, end - (start + 5)).Trim();
        var lines = dataSection.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var columns = line.Trim().Split(',');
            System.Diagnostics.Debug.WriteLine($"[JPL PARSE] Analyzing CSV line: {line.Trim()}");
            
            string? rawRa = null, rawDec = null;

            foreach (var rawCol in columns)
            {
                string col = rawCol.Trim();
                if (string.IsNullOrWhiteSpace(col) || Regex.IsMatch(col, @"[a-zA-Z]")) continue;

                if (char.IsDigit(col[0]) || col[0] == '-' || col[0] == '+')
                {
                    if (rawRa == null) rawRa = col;
                    else { rawDec = col; break; }
                }
            }

            if (rawRa != null && rawDec != null)
            {
                double? ra = AstroParser.ParseDegrees(rawRa);
                double? dec = AstroParser.ParseDegrees(rawDec);

                if (ra.HasValue && dec.HasValue)
                {
                    double finalRa = rawRa.Contains(" ") || rawRa.Contains(":") ? ra.Value * 15.0 : ra.Value;
                    System.Diagnostics.Debug.WriteLine($"[JPL PARSE] SUCCESS -> RA:{finalRa} DEC:{dec.Value}");
                    return (finalRa, dec.Value);
                }
            }
            System.Diagnostics.Debug.WriteLine($"[JPL PARSE] Failed to extract coordinates from line: {line}");
        }
        return null;
    }

    private string FindBestIdFromAmbiguityList(string response, int targetYear)
    {
        var regex = new Regex(@"^\s*(\d+)\s+(\d{4})", RegexOptions.Multiline);
        string bestId = "";
        int minDiff = int.MaxValue;

        System.Diagnostics.Debug.WriteLine("[JPL AMBIGUITY] Found candidates:");
        foreach (Match match in regex.Matches(response))
        {
            if (match.Value.Contains("Epoch")) continue;

            string id = match.Groups[1].Value;
            if (int.TryParse(match.Groups[2].Value, out int epochYear))
            {
                int diff = Math.Abs(epochYear - targetYear);
                System.Diagnostics.Debug.WriteLine($"  -> ID: {id}, Year: {epochYear} (Diff: {diff})");
                if (diff < minDiff) { minDiff = diff; bestId = id; }
            }
        }
        return string.IsNullOrEmpty(bestId) ? "" : $"{bestId};"; 
    }

    private bool IsSuccessResponse(string r) => !string.IsNullOrEmpty(r) && r.Contains("$$SOE");
    private bool ContainsError(string r) => string.IsNullOrEmpty(r) || r.Contains("No matches found") || r.Contains("Error");
    private bool IsAmbiguousResponse(string r) => !string.IsNullOrEmpty(r) && (r.Contains("Multiple major-bodies") || r.Contains("Matching small-bodies"));

    private (string DesName, string FullName) ExtractSmartNames(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return ("", "");

        // 1. Portiamo tutto in maiuscolo subito
        string clean = rawName.Trim().ToUpper();

        // 2. Gestione specifica comete (es. 240P o 240P/Kishida)
        // Se l'utente scrive "240p", deve diventare "240P"
        var match = Regex.Match(clean, @"^(\d+P)");
        if (match.Success)
        {
            string des = match.Groups[1].Value; // Esempio: "240P"
            return (des, clean); 
        }

        // Per le comete non periodiche (es. C/2023 A3)
        return (clean, clean);
    }
}