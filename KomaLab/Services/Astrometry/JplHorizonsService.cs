using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry;

namespace KomaLab.Services.Astrometry;

// ---------------------------------------------------------------------------
// FILE: JplHorizonsService.cs
// RUOLO: Servizio Esterno (API Client)
// DESCRIZIONE:
// Interroga le effemeridi NASA JPL Horizons per ottenere coordinate RA/DEC
// di comete/asteroidi in un dato istante e per una data posizione osservatore.
// DISACCOPPIAMENTO: Utilizza AstroParser per la logica matematica universale.
// ---------------------------------------------------------------------------

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
        // 1. TENTATIVO PRINCIPALE (Usa il nome così com'è)
        string response = await CallJplApi(objectName, observationTime, observerLocation, token);

        if (IsSuccessResponse(response)) 
            return ParseJplResponse(response);

        // 2. FALLBACK PER NOMI COMPLESSI (es. "C/2023 A3 (Tsuchinshan-ATLAS)")
        if (response.Contains("No matches found") && objectName.Contains("/"))
        {
            string shortName = objectName.Split(new[] { '/', '(' })[0].Trim();
            
            if (objectName.StartsWith("C/", StringComparison.OrdinalIgnoreCase) && !shortName.StartsWith("C/"))
                shortName = "C/" + shortName;

            response = await CallJplApi(shortName, observationTime, observerLocation, token);
            if (IsSuccessResponse(response)) 
                return ParseJplResponse(response);
        }

        // 3. GESTIONE AMBIGUITÀ (Lista di candidati multipli)
        if (IsAmbiguousResponse(response))
        {
            string bestId = FindBestIdFromAmbiguityList(response, observationTime.Year);
            
            if (!string.IsNullOrEmpty(bestId))
            {
                if (!bestId.EndsWith(";")) bestId += ";";
                
                response = await CallJplApi(bestId, observationTime, observerLocation, token);
                
                if (IsSuccessResponse(response))
                    return ParseJplResponse(response);
            }
        }

        return null;
    }

    // =======================================================================
    // HELPERS DI COMUNICAZIONE (API CALL)
    // =======================================================================

    private async Task<string> CallJplApi(
        string command, 
        DateTime time, 
        GeographicLocation? loc, 
        CancellationToken token)
    {
        try
        {
            string startTime = time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            string stopTime = time.AddMinutes(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            
            string centerParam = "'500'"; // Default Geocentrico
            string coordExtras = "";

            if (loc != null)
            {
                centerParam = "'coord@399'"; 
                string lon = loc.Longitude.ToString(CultureInfo.InvariantCulture);
                string lat = loc.Latitude.ToString(CultureInfo.InvariantCulture);
                string alt = loc.AltitudeKm.ToString(CultureInfo.InvariantCulture);
                coordExtras = $"&COORD_TYPE='GEODETIC'&SITE_COORD='{lon},{lat},{alt}'";
            }

            var builder = new UriBuilder(BaseUrl);
            string query = $"format=text" +
                           $"&COMMAND='{Uri.EscapeDataString(command)}'" +
                           $"&OBJ_DATA='NO'" +
                           $"&MAKE_EPHEM='YES'" +
                           $"&EPHEM_TYPE='OBSERVER'" +
                           $"&CENTER={centerParam}" +
                           $"&START_TIME='{startTime}'" +
                           $"&STOP_TIME='{stopTime}'" +
                           $"&STEP_SIZE='1m'" +
                           $"&QUANTITIES='1'" +
                           $"&CSV_FORMAT='YES'" +
                           coordExtras;
            
            builder.Query = query;
            return await _httpClient.GetStringAsync(builder.ToString(), token);
        }
        catch { return string.Empty; }
    }

    // =======================================================================
    // HELPERS DI PARSING (DATA EXTRACTION)
    // =======================================================================

    private (double Ra, double Dec)? ParseJplResponse(string response)
    {
        int startIndex = response.IndexOf("$$SOE", StringComparison.Ordinal);
        int endIndex = response.IndexOf("$$EOE", StringComparison.Ordinal);
        
        if (startIndex == -1 || endIndex == -1) return null;

        string dataBlock = response.Substring(startIndex + 5, endIndex - (startIndex + 5)).Trim();
        string[] lines = dataBlock.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        if (lines.Length == 0) return null;

        string[] parts = lines[0].Split(',');
        
        double? foundRa = null;
        double? foundDec = null;
        bool isRaInHours = false;

        for (int i = 1; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            if (string.IsNullOrEmpty(p)) continue;

            // --- UTILIZZO DEL MOTORE DI DOMINIO (AstroParser) ---
            double? val = AstroParser.ParseDegrees(p);

            if (val.HasValue)
            {
                if (foundRa == null)
                {
                    foundRa = val;
                    // Se la stringa contiene separatori, JPL la intende in ORE per la RA
                    if (p.Contains(" ") || p.Contains(":")) isRaInHours = true;
                }
                else
                {
                    foundDec = val;
                    break; 
                }
            }
        }

        if (foundRa.HasValue && foundDec.HasValue)
        {
            double finalRa = foundRa.Value;
            // Conversione Ore -> Gradi (1h = 15°)
            if (isRaInHours) finalRa *= 15.0;
            
            return (finalRa, foundDec.Value);
        }

        return null;
    }

    private string FindBestIdFromAmbiguityList(string response, int targetYear)
    {
        var regex = new Regex(@"^\s*(\d{7,9}|[A-Za-z0-9\/]+)\s+.*?(\d{4})", RegexOptions.Multiline);
        
        string bestId = "";
        int minYearDiff = int.MaxValue;

        foreach (Match match in regex.Matches(response))
        {
            string id = match.Groups[1].Value;
            if (int.TryParse(match.Groups[2].Value, out int epochYear))
            {
                int diff = Math.Abs(epochYear - targetYear);
                if (diff < minYearDiff) 
                { 
                    minYearDiff = diff; 
                    bestId = id; 
                }
            }
        }
        return bestId;
    }

    private bool IsSuccessResponse(string response) => response.Contains("$$SOE");
    
    private bool IsAmbiguousResponse(string response) => 
        response.Contains("Matching small-bodies") || 
        response.Contains("Multiple major-bodies");
}