using System;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.RegularExpressions;
using KomaLab.Models;

namespace KomaLab.Services.Astrometry;

public class JplHorizonsService
{
    private static readonly HttpClient _httpClient = new HttpClient();

    public async Task<(double Ra, double Dec)?> GetEphemerisAsync(
        string objectName, 
        DateTime observationTime, 
        GeographicLocation? observerLocation = null)
    {
        // 1. TENTATIVO PRINCIPALE (Nome completo)
        Debug.WriteLine($"[JPL] Tentativo 1: Ricerca per '{objectName}'");
        string response = await CallJplApi(objectName, observationTime, observerLocation);

        // Se abbiamo i dati subito, perfetto.
        if (response.Contains("$$SOE")) return ParseJplResponse(response);

        // 2. FALLBACK: "No matches found" con nome complesso?
        // Se il nome contiene "/" (es. "67P/Churyumov..."), proviamo a cercare solo la prima parte ("67P")
        if (response.Contains("No matches found") && objectName.Contains("/"))
        {
            string shortName = objectName.Split('/')[0].Trim();
            Debug.WriteLine($"[JPL] 'No matches' col nome completo. Tentativo 2: Ricerca semplificata per '{shortName}'");
            
            response = await CallJplApi(shortName, observationTime, observerLocation);
            if (response.Contains("$$SOE")) return ParseJplResponse(response);
        }

        // 3. GESTIONE AMBIGUITÀ (Lista di risultati)
        // Se a questo punto abbiamo una lista di "Multiple matches" (dal tentativo 1 o 2)
        if (response.Contains("Matching small-bodies") || response.Contains("Multiple major-bodies"))
        {
            Debug.WriteLine("[JPL] Rilevata ambiguità. Analisi tabella epoche...");
            
            string bestId = FindBestIdFromAmbiguityList(response, observationTime.Year);
            
            if (!string.IsNullOrEmpty(bestId))
            {
                Debug.WriteLine($"[JPL] Trovato ID ottimale per l'anno {observationTime.Year}: {bestId}. Tentativo 3 (Finale)...");
                
                if (!bestId.EndsWith(";")) bestId += ";";
                
                response = await CallJplApi(bestId, observationTime, observerLocation);
                
                // --- DEBUG: STAMPIAMO LA RISPOSTA DEL TENTATIVO 3 ---
                Debug.WriteLine("---------- JPL RESPONSE (Attempt 3) ----------");
                if (string.IsNullOrEmpty(response))
                {
                    Debug.WriteLine("RISPOSTA VUOTA O ERRORE DI CONNESSIONE.");
                }
                else
                {
                    // Stampiamo l'inizio per vedere se ci sono errori scritti dalla NASA
                    Debug.WriteLine(response.Substring(0, Math.Min(1000, response.Length)));
                    
                    // Stampiamo la zona dati se esiste
                    int soe = response.IndexOf("$$SOE");
                    if (soe != -1)
                    {
                        int end = response.IndexOf("$$EOE");
                        int len = (end != -1) ? (end - soe) + 5 : 100;
                        Debug.WriteLine("... [DATA BLOCK] ...");
                        Debug.WriteLine(response.Substring(soe, len));
                    }
                    else
                    {
                        Debug.WriteLine("!!! MARKER $$SOE NON TROVATI !!!");
                    }
                }
                Debug.WriteLine("----------------------------------------------");
                // -----------------------------------------------------------

                return ParseJplResponse(response);
            }
        }

        Debug.WriteLine("[JPL] Fallimento. Nessun dato estratto.");
        return null;
    }

    private async Task<string> CallJplApi(string command, DateTime time, GeographicLocation? loc)
    {
        try
        {
            string startTime = time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            string stopTime = time.AddMinutes(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            string centerParam = "'500'"; 
            string uriExtras = "";

            if (loc != null)
            {
                centerParam = "'coord@399'";
                // Usiamo CultureInfo.InvariantCulture per assicurare il PUNTO decimale, non la virgola
                string lon = loc.Longitude.ToString(CultureInfo.InvariantCulture);
                string lat = loc.Latitude.ToString(CultureInfo.InvariantCulture);
                string alt = loc.AltitudeKm.ToString(CultureInfo.InvariantCulture);
                
                // Debug: Controlliamo come stiamo formattando le coordinate
                Debug.WriteLine($"[JPL DEBUG] Setting Topocentric: Lon={lon}, Lat={lat}, Alt={alt}");

                uriExtras = $"&COORD_TYPE='GEODETIC'&SITE_COORD='{lon},{lat},{alt}'";
            }

            var builder = new UriBuilder("https://ssd.jpl.nasa.gov/api/horizons.api");
            // Aggiungiamo CSV_FORMAT='YES' esplicitamente
            string query = $"format=text&COMMAND='{Uri.EscapeDataString(command)}'&OBJ_DATA='NO'&MAKE_EPHEM='YES'&EPHEM_TYPE='OBSERVER'&CENTER={centerParam}&START_TIME='{startTime}'&STOP_TIME='{stopTime}'&STEP_SIZE='1m'&QUANTITIES='1'&CSV_FORMAT='YES'{uriExtras}";
            
            builder.Query = query;
            
            // Debug: Stampiamo l'URL esatto generato
            Debug.WriteLine($"[JPL URL] {builder.ToString()}");

            return await _httpClient.GetStringAsync(builder.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[JPL] Errore HTTP: {ex.Message}");
            return "";
        }
    }

    // ... (FindBestIdFromAmbiguityList con REGEX, quello che ti ho dato poco fa) ...
    private string FindBestIdFromAmbiguityList(string response, int targetYear)
    {
        var regex = new Regex(@"^\s*(\d{7,9})\s+(\d{4})\s+", RegexOptions.Multiline);
        string bestId = "";
        int minYearDiff = int.MaxValue;

        foreach (Match match in regex.Matches(response))
        {
            string id = match.Groups[1].Value;
            if (int.TryParse(match.Groups[2].Value, out int epochYear))
            {
                int diff = Math.Abs(epochYear - targetYear);
                if (diff < minYearDiff) { minYearDiff = diff; bestId = id; }
            }
        }
        return bestId;
    }

    // ... (ParseJplResponse intelligente con fix per ore/gradi) ...
    private (double Ra, double Dec)? ParseJplResponse(string response)
    {
        int startIndex = response.IndexOf("$$SOE");
        int endIndex = response.IndexOf("$$EOE");
        if (startIndex == -1 || endIndex == -1) return null;

        string dataBlock = response.Substring(startIndex + 5, endIndex - (startIndex + 5)).Trim();
        string[] lines = dataBlock.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        // Esempio riga: 2009-Feb-18 19:01,A, , 01 06 39.27, +07 29 11.1,
        string[] parts = lines[0].Split(',');

        double? foundRa = null;
        double? foundDec = null;
        string rawRaString = "";

        // Iteriamo le colonne partendo dalla 1 (saltiamo la data)
        for (int i = 1; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            if (string.IsNullOrEmpty(p)) continue;

            // Tentiamo di parsare la colonna come coordinata
            double? val = FitsMetadataReader.ParseCoordinateString(p);

            // Se il parsing ha successo, è una coordinata valida
            if (val.HasValue)
            {
                if (foundRa == null)
                {
                    foundRa = val;
                    rawRaString = p; // Salviamo la stringa originale per il check ore/gradi
                }
                else
                {
                    foundDec = val;
                    break; // Abbiamo trovato entrambe, usciamo
                }
            }
        }

        if (foundRa.HasValue && foundDec.HasValue)
        {
            double finalRa = foundRa.Value;
            // Se la stringa originale conteneva spazi o due punti, era in formato ORE.
            // Conversione: Ore * 15 = Gradi.
            if (rawRaString.Contains(" ") || rawRaString.Contains(":")) 
            {
                finalRa *= 15.0;
            }
            return (finalRa, foundDec.Value);
        }

        return null;
    }
}