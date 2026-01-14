using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry;
using KomaLab.Services.Fits.Parsers; // Aggiunto per usare GeographicParser

namespace KomaLab.Services.Astrometry;

// ---------------------------------------------------------------------------
// FILE: JplHorizonsService.cs
// RUOLO: Servizio Esterno (API Client)
// DESCRIZIONE:
// Interroga le effemeridi NASA JPL Horizons per ottenere coordinate RA/DEC
// di comete/asteroidi in un dato istante e per una data posizione osservatore.
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
        // 1. TENTATIVO PRINCIPALE
        string response = await CallJplApi(objectName, observationTime, observerLocation, token);

        if (IsSuccessResponse(response)) 
            return ParseJplResponse(response);

        // 2. FALLBACK PER NOMI COMPLESSI (es. "C/2023 A3 (Tsuchinshan-ATLAS)")
        // JPL spesso fallisce se il nome contiene spazi o parentesi, preferisce il codice breve.
        if (response.Contains("No matches found") && objectName.Contains("/"))
        {
            string shortName = objectName.Split('/')[0].Trim();
            
            response = await CallJplApi(shortName, observationTime, observerLocation, token);
            if (IsSuccessResponse(response)) 
                return ParseJplResponse(response);
        }

        // 3. GESTIONE AMBIGUITÀ (Lista di candidati)
        if (IsAmbiguousResponse(response))
        {
            // Cerca l'ID che ha l'epoca più vicina all'anno di osservazione corrente
            string bestId = FindBestIdFromAmbiguityList(response, observationTime.Year);
            
            if (!string.IsNullOrEmpty(bestId))
            {
                // JPL richiede spesso il punto e virgola per gli ID di small-bodies
                if (!bestId.EndsWith(";")) bestId += ";";
                
                response = await CallJplApi(bestId, observationTime, observerLocation, token);
                
                if (IsSuccessResponse(response))
                    return ParseJplResponse(response);
            }
        }

        return null;
    }

    private async Task<string> CallJplApi(
        string command, 
        DateTime time, 
        GeographicLocation? loc, 
        CancellationToken token)
    {
        try
        {
            string startTime = time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            // Richiediamo 1 minuto di dati, ci basta il primo punto
            string stopTime = time.AddMinutes(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            
            // Default: Geocentrico (500)
            string centerParam = "'500'"; 
            string coordExtras = "";

            if (loc != null)
            {
                centerParam = "'coord@399'"; // 399 = Terra
                string lon = loc.Longitude.ToString(CultureInfo.InvariantCulture);
                string lat = loc.Latitude.ToString(CultureInfo.InvariantCulture);
                string alt = loc.AltitudeKm.ToString(CultureInfo.InvariantCulture);
                
                // JPL usa formato: 'lon,lat,alt'
                coordExtras = $"&COORD_TYPE='GEODETIC'&SITE_COORD='{lon},{lat},{alt}'";
            }

            var builder = new UriBuilder(BaseUrl);
            
            // Costruzione query string esplicita
            string query = $"format=text" +
                           $"&COMMAND='{Uri.EscapeDataString(command)}'" +
                           $"&OBJ_DATA='NO'" +
                           $"&MAKE_EPHEM='YES'" +
                           $"&EPHEM_TYPE='OBSERVER'" +
                           $"&CENTER={centerParam}" +
                           $"&START_TIME='{startTime}'" +
                           $"&STOP_TIME='{stopTime}'" +
                           $"&STEP_SIZE='1m'" +
                           $"&QUANTITIES='1'" + // 1 = Astrometric RA/DEC
                           $"&CSV_FORMAT='YES'" +
                           coordExtras;
            
            builder.Query = query;

            return await _httpClient.GetStringAsync(builder.ToString(), token);
        }
        catch (Exception) 
        { 
            // In produzione: Loggare l'eccezione
            return ""; 
        }
    }

    private string FindBestIdFromAmbiguityList(string response, int targetYear)
    {
        // Regex euristica per parsare la tabella di disambiguazione JPL
        var regex = new Regex(@"^\s*(\d{7,9})\s+(\d{4})\s+", RegexOptions.Multiline);
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

    private (double Ra, double Dec)? ParseJplResponse(string response)
    {
        // Estrazione blocco dati CSV tra i marker
        int startIndex = response.IndexOf("$$SOE", StringComparison.Ordinal);
        int endIndex = response.IndexOf("$$EOE", StringComparison.Ordinal);
        
        if (startIndex == -1 || endIndex == -1) return null;

        string dataBlock = response.Substring(startIndex + 5, endIndex - (startIndex + 5)).Trim();
        string[] lines = dataBlock.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        if (lines.Length == 0) return null;

        // Formato CSV JPL standard: Date, RA, DEC, ...
        string[] parts = lines[0].Split(',');
        double? foundRa = null;
        double? foundDec = null;
        bool isRaInHours = false;

        // Scansione colonne (saltiamo la data a indice 0)
        for (int i = 1; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            if (string.IsNullOrEmpty(p)) continue;

            // --- FIX: Uso del parser statico centralizzato ---
            // Sostituisce la vecchia chiamata extension p.ParseAsCoordinate()
            double? val = GeographicParser.ParseCoordinateString(p);
            // ------------------------------------------------

            if (val.HasValue)
            {
                if (foundRa == null)
                {
                    foundRa = val;
                    // Euristica: se contiene spazi (12 30 45) o due punti (12:30:45),
                    // JPL sta restituendo formato sessagesimale, che per RA è quasi sempre in ORE.
                    if (p.Contains(" ") || p.Contains(":")) isRaInHours = true;
                }
                else
                {
                    foundDec = val;
                    break; // Trovati entrambi (RA e DEC), usciamo
                }
            }
        }

        if (foundRa.HasValue && foundDec.HasValue)
        {
            double finalRa = foundRa.Value;
            // Conversione Ore -> Gradi (1h = 15°) se necessario
            if (isRaInHours) finalRa *= 15.0;
            return (finalRa, foundDec.Value);
        }

        return null;
    }

    private bool IsSuccessResponse(string response) => response.Contains("$$SOE");
    
    private bool IsAmbiguousResponse(string response) => 
        response.Contains("Matching small-bodies") || 
        response.Contains("Multiple major-bodies");
}