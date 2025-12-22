// Path: NPC_Plugin_Chooser_2/BackEnd/FaceFinderClient.cs

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Net;
using Newtonsoft.Json;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

using System.Net.Http;
using System.Threading.Tasks;
using Mutagen.Bethesda.Plugins;

// Represents the data returned from a successful API call
public class FaceFinderResult
{
    public string? ImageUrl { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? ExternalUrl { get; init; }
}

public class FaceFinderNpcResult
{
    public required string ModName { get; init; }
    public required string ImageUrl { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public class FaceFinderClient
{
    private readonly Settings _settings;
    private readonly string _apiKey = GetAPIKey();
    private static readonly HttpClient _httpClient = new();
    private const string ApiBaseUrl = "https://npcfacefinder.com";
    public const string MetadataFileExtension = ".ffmeta.json";
    private const string LogFileName = "FaceFinderLog.txt";
    private static bool _isLogCleared = false;
    private static readonly object _logLock = new();

    private const byte _xorKey = 0x55;

    private static readonly byte[] _obfuscatedBytes =
    {
        0x1B, 0x05, 0x16, 0x13, 0x13, 0x78, 0x0F, 0x2C, 0x12, 0x0D, 0x67, 0x0F, 0x10, 0x31, 0x3B, 0x20, 0x3E, 0x30,
        0x26, 0x63, 0x31, 0x20, 0x6C, 0x63, 0x1D, 0x2D, 0x06, 0x3B, 0x6C, 0x05, 0x11, 0x2C, 0x24, 0x3E, 0x63, 0x62,
        0x36, 0x31, 0x63, 0x26, 0x24, 0x31, 0x3D, 0x34, 0x33, 0x3C, 0x00, 0x3B, 0x25, 0x13, 0x05, 0x21, 0x34, 0x07,
        0x0D, 0x07, 0x1A, 0x3C, 0x1B, 0x19, 0x1D, 0x66, 0x1A, 0x00, 0x1E, 0x16, 0x24, 0x6D, 0x2F, 0x02, 0x2D, 0x01,
        0x20, 0x07, 0x23, 0x36, 0x3D, 0x30, 0x19, 0x6C, 0x6D, 0x22, 0x3E, 0x6D, 0x12, 0x24, 0x25, 0x3B, 0x1A, 0x20,
        0x06, 0x25, 0x3B, 0x13, 0x0D, 0x24, 0x6C, 0x11, 0x03, 0x0C, 0x37, 0x14, 0x23, 0x3B, 0x13, 0x24, 0x34, 0x1C,
        0x36, 0x61, 0x22, 0x67, 0x14, 0x17, 0x67, 0x01, 0x6D, 0x32, 0x36, 0x21, 0x1E, 0x24, 0x30, 0x06, 0x1F, 0x31,
        0x60, 0x27, 0x34, 0x3E, 0x0D, 0x31, 0x22, 0x10,
    };
    
    public FaceFinderClient(Settings settings)
    {
        _settings = settings;
        
        // Clear log on startup (only once per session)
        if (!_isLogCleared)
        {
            try 
            {
                if (File.Exists(LogFileName)) File.Delete(LogFileName);
            }
            catch (Exception ex) 
            { 
                Debug.WriteLine($"Failed to clear log file: {ex.Message}"); 
            }
            finally
            {
                _isLogCleared = true;
            }
        }
    }
    
    private async Task LogInteractionAsync(HttpRequestMessage request, HttpResponseMessage? response = null, string? responseContent = null)
    {
        if (!_settings.LogFaceFinderRequests) return;

        await Task.Run(() =>
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("========================================");
                sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Request: {request.Method} {request.RequestUri}");
                
                // Log Headers (Redacting API Key)
                foreach (var header in request.Headers)
                {
                    string value = header.Key == "X-API-Key" ? "{REDACTED_API}" : string.Join(", ", header.Value);
                    sb.AppendLine($"Header: {header.Key} = {value}");
                }

                if (response != null)
                {
                    sb.AppendLine("----------------------------------------");
                    sb.AppendLine($"Response Status: {(int)response.StatusCode} {response.ReasonPhrase}");
                    if (!string.IsNullOrWhiteSpace(responseContent))
                    {
                        // Truncate overly long responses if necessary, though full JSON is usually helpful for debugging
                        sb.AppendLine("Response Body:");
                        sb.AppendLine(responseContent);
                    }
                }
                else
                {
                    sb.AppendLine("----------------------------------------");
                    sb.AppendLine("Response: [NULL/FAILED]");
                }
                
                sb.AppendLine("========================================");
                sb.AppendLine();

                lock (_logLock)
                {
                    File.AppendAllText(LogFileName, sb.ToString());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Logging failed: {ex.Message}");
            }
        });
    }

    // Internal class for serializing metadata to a sidecar file
    public class FaceFinderMetadata
    {
        public string Source { get; set; } = "FaceFinder";
        public DateTime UpdatedAt { get; set; }
        public string? ExternalUrl { get; init; }
    }
    
    public async Task<List<string>> GetAllModNamesAsync()
    {
        var allModNames = new List<string>();
        
        int currentPage = 1;
        while (true)
        {
            var requestUri = $"/api/public/mods/search?page={currentPage}";
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + requestUri));
            request.Headers.Add("X-API-Key", _apiKey);

            try
            {
                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                await LogInteractionAsync(request, response, content);

                if (!response.IsSuccessStatusCode) break;
                var results = JsonConvert.DeserializeObject<List<dynamic>>(content);

                if (results == null || !results.Any()) break; // No more pages

                allModNames.AddRange(results.Select(r => (string)r.name));
                currentPage++;
            }
            catch (Exception ex) {
                Debug.WriteLine($"FaceFinder API error getting mod list on page {currentPage}: {ex.Message}");
                break;
            }
        }
        return allModNames.OrderBy(name => name).ToList();
    }

    public async Task<FaceFinderResult?> GetFaceDataAsync(FormKey npcFormKey, string modNameToFind)
    {
        string faceFinderModName = modNameToFind;
        if (_settings.FaceFinderModNameMappings.TryGetValue(modNameToFind, out var mappedNames) &&
            mappedNames.LastOrDefault() is { } lastMappedName)
        {
            faceFinderModName = lastMappedName;
            Debug.WriteLine($"Remapped local mod '{modNameToFind}' to FaceFinder mod '{faceFinderModName}'");
        }

        // -------------------------------------------------------------------------
        // FIX APPLIED HERE:
        // 1. Use 'X8' for Uppercase Hex (Required for IDs like 0001325F)
        // 2. Only apply .ToLowerInvariant() to the FileName (Required for DB match)
        // -------------------------------------------------------------------------
        var formKeyValue = $"{npcFormKey.ID:X8}:{npcFormKey.ModKey.FileName.String.ToLowerInvariant()}";

        var encodedFormKey = WebUtility.UrlEncode(formKeyValue);
        var encodedModName = WebUtility.UrlEncode(faceFinderModName);

        // Note: We do NOT need the "page=1" hack here because we aren't sending 
        // the page parameter at all. The default server behavior (Implicit Page 1) 
        // works correctly as long as the FormKey casing is correct.
        var requestUri = $"/api/public/npc/faces/search?formKey={encodedFormKey}&search={encodedModName}";

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + requestUri));
        request.Headers.Add("X-API-Key", _apiKey);

        try
        {
            var response = await _httpClient.SendAsync(request);

            var content = await response.Content.ReadAsStringAsync();

            var results = JsonConvert.DeserializeObject<List<dynamic>>(content);
            
            await LogInteractionAsync(request, response, content);

            if (!response.IsSuccessStatusCode) return null;
            
            var firstResult = results?.FirstOrDefault();

            if (firstResult == null) return null;

            string? imageUrl = firstResult.images?.full;
            string? updatedAtStr = firstResult.updated_at;
            string? externalUrl = firstResult.mod?.external_url;

            if (string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(updatedAtStr) ||
                !DateTime.TryParse(updatedAtStr, null, DateTimeStyles.RoundtripKind, out var updatedAt))
            {
                return null;
            }

            return new FaceFinderResult { ImageUrl = imageUrl, UpdatedAt = updatedAt, ExternalUrl = externalUrl };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FaceFinder API error for {npcFormKey}: {ex.Message}");
            await LogInteractionAsync(request, null, $"EXCEPTION: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> IsCacheStaleAsync(string imagePath, FormKey npcFormKey, string modNameToFind)
    {
        var metadataPath = imagePath + MetadataFileExtension;

        if (!File.Exists(metadataPath))
        {
            // CASE 1: Image exists but metadata is missing.
            // This is a manually downloaded image. It is NOT stale and must be preserved.
            return false;
        }

        // CASE 3: Both image and metadata exist. Check for updates from the API.
        try
        {
            var metadataJson = await File.ReadAllTextAsync(metadataPath);
            var metadata = JsonConvert.DeserializeObject<FaceFinderMetadata>(metadataJson);

            if (metadata == null || metadata.Source != "FaceFinder")
            {
                // The metadata is invalid or from another source. Treat as a manual file.
                return false;
            }
            
            string faceFinderModName = modNameToFind;
            if (_settings.FaceFinderModNameMappings.TryGetValue(modNameToFind, out var mappedNames) && mappedNames.LastOrDefault() is { } lastMappedName)
            {
                faceFinderModName = lastMappedName;
            }

            var latestData = await GetFaceDataAsync(npcFormKey, faceFinderModName);
            if (latestData == null)
            {
                // If the API fails, we can't check for an update.
                // Assume the cached version is fine to avoid overwriting it.
                return false;
            }

            // Return true (stale) only if the server has a more recent version.
            return latestData.UpdatedAt > metadata.UpdatedAt;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error checking cache staleness for {imagePath}: {ex.Message}");
            // If there's an error reading the metadata, it might be corrupt.
            // Returning true allows the system to try and re-download a valid copy.
            return true;
        }
    }

    public async Task WriteMetadataAsync(string imagePath, FaceFinderResult result)
    {
        var metadataPath = imagePath + MetadataFileExtension;
        var metadata = new FaceFinderMetadata { UpdatedAt = result.UpdatedAt, ExternalUrl = result.ExternalUrl };
        
        try
        {
            var metadataJson = JsonConvert.SerializeObject(metadata, Formatting.Indented);
            await File.WriteAllTextAsync(metadataPath, metadataJson);
            _settings.CachedFaceFinderPaths.Add(imagePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write FaceFinder metadata for {imagePath}: {ex.Message}");
        }
    }
    
    public async Task<FaceFinderMetadata?> ReadMetadataAsync(string imagePath)
    {
        var metadataPath = imagePath + MetadataFileExtension;
        if (!File.Exists(metadataPath)) return null;

        try
        {
            var metadataJson = await File.ReadAllTextAsync(metadataPath);
            return JsonConvert.DeserializeObject<FaceFinderMetadata>(metadataJson);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error reading metadata file {metadataPath}: {ex.Message}");
            return null;
        }
    }

    public async Task<List<FaceFinderNpcResult>> GetAllFaceDataForNpcAsync(FormKey npcFormKey)
    {
        // Use a Dictionary to automatically handle deduplication by ID
        var distinctFaces = new Dictionary<int, FaceFinderNpcResult>();

        // Prepare the base Key
        var formKeyValue = $"{npcFormKey.ID:X8}:{npcFormKey.ModKey.FileName.String.ToLowerInvariant()}";
        var encodedFormKey = WebUtility.UrlEncode(formKeyValue);

        int currentPage = 1;

        while (true)
        {
            // We might need to make TWO requests for the first page to cover the server bug
            var urisToFetch = new List<string>();

            if (currentPage == 1)
            {
                // STRATEGY: Fetch BOTH behaviors for Page 1
                // 1. Implicit (No page param) -> Gets "Fuzzy" matches (The list of 26)
                urisToFetch.Add($"/api/public/npc/faces/search?formKey={encodedFormKey}");

                // 2. Explicit (With page param) -> Gets "Strict" matches (The list of 6)
                urisToFetch.Add($"/api/public/npc/faces/search?formKey={encodedFormKey}&page=1");
            }
            else
            {
                // For Page 2+, we have no choice but to use the parameter
                urisToFetch.Add($"/api/public/npc/faces/search?formKey={encodedFormKey}&page={currentPage}");
            }

            bool foundDataOnThisPage = false;

            foreach (var requestUri in urisToFetch)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + requestUri));
                request.Headers.Add("X-API-Key", _apiKey);

                try
                {
                    var response = await _httpClient.SendAsync(request);

                    var content = await response.Content.ReadAsStringAsync();
                    
                    await LogInteractionAsync(request, response, content);
                    
                    if (!response.IsSuccessStatusCode) continue; // Skip failed requests, try the next one
                    var results = JsonConvert.DeserializeObject<List<dynamic>>(content);

                    if (results != null && results.Any())
                    {
                        foundDataOnThisPage = true;

                        foreach (var result in results)
                        {
                            // Safely cast the ID to int for deduplication
                            int id = (int)result.id;

                            // If we already have this ID, skip it
                            if (distinctFaces.ContainsKey(id)) continue;

                            string? modName = result.mod?.name;
                            string? imageUrl = result.images?.full;
                            string? updatedAtStr = result.updated_at;

                            if (string.IsNullOrWhiteSpace(modName) ||
                                string.IsNullOrWhiteSpace(imageUrl) ||
                                string.IsNullOrWhiteSpace(updatedAtStr) ||
                                !DateTime.TryParse(updatedAtStr, null, DateTimeStyles.RoundtripKind, out var updatedAt))
                            {
                                continue;
                            }

                            distinctFaces.Add(id, new FaceFinderNpcResult
                            {
                                ModName = modName,
                                ImageUrl = imageUrl,
                                UpdatedAt = updatedAt
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FaceFinder API error on page {currentPage}: {ex.Message}");
                    await LogInteractionAsync(request, null, $"EXCEPTION: {ex.Message}");
                }
            }

            // If NEITHER request returned data for this page, we are done
            if (!foundDataOnThisPage) break;

            currentPage++;
        }

        return distinctFaces.Values.ToList();
    }

    private static string GetAPIKey()
    {
        byte[] decoded = new byte[_obfuscatedBytes.Length];
        
        for (int i = 0; i < _obfuscatedBytes.Length; i++)
        {
            decoded[i] = (byte)(_obfuscatedBytes[i] ^ _xorKey);
        }

        return Encoding.UTF8.GetString(decoded);
    }
}