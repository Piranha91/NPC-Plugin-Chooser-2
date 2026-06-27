// Path: NPC_Plugin_Chooser_2/BackEnd/FaceFinderClient.cs

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

/// <summary>One server-side face entry as returned by
/// <c>/api/public/mod/faces/search?modId=...</c>. Carries the vanilla NPC
/// form key (string-formatted as the API delivers it), the full-resolution
/// image URL, the mod's external Nexus link, and the server-side update
/// timestamp used for staleness comparison.</summary>
public class FaceFinderModFaceResult
{
    public required string FormKey { get; init; }
    public required string ImageUrl { get; init; }
    public string? ExternalUrl { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public class FaceFinderClient
{
    private readonly Settings _settings;
    private readonly EventLogger _eventLogger;
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
    
    public FaceFinderClient(Settings settings, EventLogger eventLogger)
    {
        _settings = settings;
        _eventLogger = eventLogger;
        
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
    
    /// <summary>Paginated walk of <c>/api/public/mods/search</c> that returns
    /// a case-insensitive name → server-id map for every mod the server knows
    /// about. Used by the FaceFinder batch download to translate the user's
    /// local mod names into mod IDs in O(1) per lookup, instead of issuing a
    /// per-mod search request. First occurrence of a duplicated name wins:
    /// alphabetically-sorted server pages put the canonical "Foo" before
    /// rename-disambiguated entries like "Foo (deprecated)".
    /// <para>Mirrors <see cref="GetAllFaceDataForNpcAsync"/>'s implicit/explicit
    /// page-1 union to defend against the known server quirk where the
    /// no-page-param response and the <c>?page=1</c> response return
    /// overlapping-but-not-identical result sets. Deduplication is by mod
    /// <c>id</c>, so the same mod surfaced under both fetches counts once.
    /// One extra HTTP round-trip per batch in exchange for not silently
    /// dropping mods that only show up in one of the two views.</para></summary>
    public async Task<Dictionary<string, int>> GetAllModsAsync()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<int>();

        int currentPage = 1;
        while (true)
        {
            var urisToFetch = new List<string>();
            if (currentPage == 1)
            {
                urisToFetch.Add($"/api/public/mods/search");
                urisToFetch.Add($"/api/public/mods/search?page=1");
            }
            else
            {
                urisToFetch.Add($"/api/public/mods/search?page={currentPage}");
            }

            bool foundDataOnThisPage = false;

            foreach (var requestUri in urisToFetch)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + requestUri));
                request.Headers.Add("X-API-Key", _apiKey);

                HttpResponseMessage? response = null;
                try
                {
                    response = await _httpClient.SendAsync(request);
                    var content = await response.Content.ReadAsStringAsync();
                    await LogInteractionAsync(request, response, content);

                    if (!response.IsSuccessStatusCode) continue;
                    var results = JsonConvert.DeserializeObject<List<dynamic>>(content, new JsonSerializerSettings
                    {
                        DateParseHandling = DateParseHandling.None
                    });

                    if (results == null || !results.Any()) continue;

                    foundDataOnThisPage = true;
                    foreach (var r in results)
                    {
                        int? id = (int?)r.id;
                        if (!id.HasValue || !seenIds.Add(id.Value)) continue;
                        string? name = r.name;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (!result.ContainsKey(name)) result.Add(name, id.Value);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FaceFinder GetAllModsAsync error on page {currentPage} ({requestUri}): {ex.Message}");
                    await LogInteractionAsync(request, response, $"EXCEPTION: {ex.Message}");
                    _eventLogger.Log($"GetAllModsAsync: FaceFinder API error on page {currentPage}: {ex.Message}");
                }
            }

            if (!foundDataOnThisPage) break;
            currentPage++;
        }
        return result;
    }

    /// <summary>
    /// v2 counterpart of <see cref="GetAllModsAsync"/> using <c>/api/public/v2/mods/search</c>,
    /// which fixes the page-1 divergence the v1 method unions around — so this is a plain
    /// <c>hasMore</c> pagination walk with no implicit/explicit page-1 double fetch. The v2
    /// envelope is <c>{ page, pageSize, hasMore, results }</c>; each result still carries
    /// <c>id</c> and <c>name</c>, so name→id mapping (first occurrence of a duplicated name wins)
    /// is unchanged. Faces/ids are deduped by <c>id</c> as a belt-and-braces measure even though
    /// the clean walk should not repeat.
    /// </summary>
    public async Task<Dictionary<string, int>> GetAllModsV2Async()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<int>();

        int page = 1;
        while (true)
        {
            var requestUri = $"/api/public/v2/mods/search?page={page}";
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + requestUri));
            request.Headers.Add("X-API-Key", _apiKey);

            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                await LogInteractionAsync(request, response, content);

                if (!response.IsSuccessStatusCode) break;

                var envelope = JsonConvert.DeserializeObject<JObject>(content, new JsonSerializerSettings
                {
                    DateParseHandling = DateParseHandling.None
                });

                if (envelope?["results"] is JArray results)
                {
                    foreach (var r in results)
                    {
                        int? id = r["id"]?.Value<int?>();
                        if (!id.HasValue || !seenIds.Add(id.Value)) continue;
                        string? name = r["name"]?.Value<string>();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (!result.ContainsKey(name)) result.Add(name, id.Value);
                    }
                }

                var hasMoreToken = envelope?["hasMore"] ?? envelope?["has_more"];
                bool hasMore = hasMoreToken?.Type == JTokenType.Boolean && hasMoreToken.Value<bool>();

                if (!hasMore) break;
                page++;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FaceFinder GetAllModsV2Async error on page {page} ({requestUri}): {ex.Message}");
                await LogInteractionAsync(request, response, $"EXCEPTION: {ex.Message}");
                _eventLogger.Log($"GetAllModsV2Async: FaceFinder API error on page {page}: {ex.Message}");
                break;
            }
        }
        return result;
    }

    /// <summary>Paginated walk of <c>/api/public/mod/faces/search?modId=...</c>
    /// returning every face the server has registered for the given mod.
    /// Pairs with <see cref="GetAllModsAsync"/> in the batch path: the user's
    /// local NPCs are intersected against this list, avoiding a per-NPC
    /// availability probe for the typical case where ~90% of NPCs are not on
    /// the server.
    /// <para>Like <see cref="GetAllFaceDataForNpcAsync"/>, page 1 is fetched
    /// twice (implicit + explicit) to defend against the server's
    /// no-page-param vs <c>?page=1</c> result-set divergence. Faces are
    /// deduped by their server-side <c>id</c>.</para></summary>
    public async Task<List<FaceFinderModFaceResult>> GetAllFacesForModAsync(int modId)
    {
        var results = new List<FaceFinderModFaceResult>();
        var seenIds = new HashSet<int>();

        int currentPage = 1;
        while (true)
        {
            var urisToFetch = new List<string>();
            if (currentPage == 1)
            {
                urisToFetch.Add($"/api/public/mod/faces/search?modId={modId}");
                urisToFetch.Add($"/api/public/mod/faces/search?modId={modId}&page=1");
            }
            else
            {
                urisToFetch.Add($"/api/public/mod/faces/search?modId={modId}&page={currentPage}");
            }

            bool foundDataOnThisPage = false;

            foreach (var requestUri in urisToFetch)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + requestUri));
                request.Headers.Add("X-API-Key", _apiKey);

                HttpResponseMessage? response = null;
                try
                {
                    response = await _httpClient.SendAsync(request);
                    var content = await response.Content.ReadAsStringAsync();
                    await LogInteractionAsync(request, response, content);

                    if (!response.IsSuccessStatusCode) continue;
                    var page = JsonConvert.DeserializeObject<List<dynamic>>(content, new JsonSerializerSettings
                    {
                        DateParseHandling = DateParseHandling.None
                    });

                    if (page == null || !page.Any()) continue;

                    foundDataOnThisPage = true;
                    foreach (var entry in page)
                    {
                        int? id = (int?)entry.id;
                        if (!id.HasValue || !seenIds.Add(id.Value)) continue;
                        string? formKey = entry.npc?.form_key;
                        string? imageUrl = entry.images?.full;
                        string? externalUrl = entry.mod?.external_url;
                        string? updatedAtStr = entry.updated_at;
                        if (string.IsNullOrWhiteSpace(formKey) ||
                            string.IsNullOrWhiteSpace(imageUrl) ||
                            string.IsNullOrWhiteSpace(updatedAtStr) ||
                            !DateTime.TryParse(updatedAtStr, null, DateTimeStyles.RoundtripKind, out var updatedAt))
                        {
                            continue;
                        }
                        results.Add(new FaceFinderModFaceResult
                        {
                            FormKey = formKey,
                            ImageUrl = imageUrl,
                            ExternalUrl = externalUrl,
                            UpdatedAt = updatedAt,
                        });
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FaceFinder GetAllFacesForModAsync error modId={modId} page={currentPage} ({requestUri}): {ex.Message}");
                    await LogInteractionAsync(request, response, $"EXCEPTION: {ex.Message}");
                    _eventLogger.Log($"GetAllFacesForModAsync: API error modId={modId} page={currentPage}: {ex.Message}");
                }
            }

            if (!foundDataOnThisPage) break;
            currentPage++;
        }
        return results;
    }

    /// <summary>
    /// v2 counterpart of <see cref="GetAllFacesForModAsync"/> using
    /// <c>/api/public/v2/mod/faces/search</c>. Plain <c>hasMore</c> pagination walk; the per-face
    /// JSON shape matches v1 (<c>npc.form_key</c>, <c>images.full</c>, <c>mod.external_url</c>,
    /// <c>updated_at</c>), only the envelope differs (<c>{ page, pageSize, hasMore, results }</c>).
    /// No client-side dedup — the server is expected to return each face once.
    /// </summary>
    public async Task<List<FaceFinderModFaceResult>> GetAllFacesForModV2Async(int modId)
    {
        var results = new List<FaceFinderModFaceResult>();

        int page = 1;
        while (true)
        {
            var requestUri = $"/api/public/v2/mod/faces/search?modId={modId}&page={page}";
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + requestUri));
            request.Headers.Add("X-API-Key", _apiKey);

            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                await LogInteractionAsync(request, response, content);

                if (!response.IsSuccessStatusCode) break;

                var envelope = JsonConvert.DeserializeObject<JObject>(content, new JsonSerializerSettings
                {
                    DateParseHandling = DateParseHandling.None
                });

                if (envelope?["results"] is JArray page1)
                {
                    foreach (var entry in page1)
                    {
                        string? formKey = entry["npc"]?["form_key"]?.Value<string>();
                        string? imageUrl = entry["images"]?["full"]?.Value<string>();
                        string? externalUrl = entry["mod"]?["external_url"]?.Value<string>();
                        string? updatedAtStr = entry["updated_at"]?.Value<string>();
                        if (string.IsNullOrWhiteSpace(formKey) ||
                            string.IsNullOrWhiteSpace(imageUrl) ||
                            string.IsNullOrWhiteSpace(updatedAtStr) ||
                            !DateTime.TryParse(updatedAtStr, null, DateTimeStyles.RoundtripKind, out var updatedAt))
                        {
                            continue;
                        }
                        results.Add(new FaceFinderModFaceResult
                        {
                            FormKey = formKey,
                            ImageUrl = imageUrl,
                            ExternalUrl = externalUrl,
                            UpdatedAt = updatedAt,
                        });
                    }
                }

                var hasMoreToken = envelope?["hasMore"] ?? envelope?["has_more"];
                bool hasMore = hasMoreToken?.Type == JTokenType.Boolean && hasMoreToken.Value<bool>();

                if (!hasMore) break;
                page++;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FaceFinder GetAllFacesForModV2Async error modId={modId} page={page} ({requestUri}): {ex.Message}");
                await LogInteractionAsync(request, response, $"EXCEPTION: {ex.Message}");
                _eventLogger.Log($"GetAllFacesForModV2Async: API error modId={modId} page={page}: {ex.Message}");
                break;
            }
        }
        return results;
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
                var results = JsonConvert.DeserializeObject<List<dynamic>>(content, new JsonSerializerSettings 
                { 
                    DateParseHandling = DateParseHandling.None 
                });

                if (results == null || !results.Any()) break; // No more pages

                allModNames.AddRange(results.Select(r => (string)r.name));
                currentPage++;
            }
            catch (Exception ex) {
                Debug.WriteLine($"FaceFinder API error getting mod list on page {currentPage}: {ex.Message}");
                await LogInteractionAsync(request, response: null, responseContent: $"FaceFinder API error getting mod list on page {currentPage}: {ex.Message}");
                _eventLogger.Log($"GetAllModNames: FaceFinder API error getting mod list on page {currentPage}: {ex.Message}");
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
        string content = string.Empty;

        try
        {
            var response = await _httpClient.SendAsync(request);

            content = await response.Content.ReadAsStringAsync();

            var results = JsonConvert.DeserializeObject<List<dynamic>>(content, new JsonSerializerSettings 
            { 
                DateParseHandling = DateParseHandling.None 
            });
            
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
                string msg = "One of the following fields are missing or invalid:\n";
                msg += "imageUrl: " + imageUrl + "\n";
                msg += "updatedAt: " + updatedAtStr + "\n";
                await LogInteractionAsync(request, response, msg);
                _eventLogger.Log($"FaceFinder Clinet: {npcFormKey}: {modNameToFind}:" + Environment.NewLine + msg);
                
                return null;
            }

            return new FaceFinderResult { ImageUrl = imageUrl, UpdatedAt = updatedAt, ExternalUrl = externalUrl };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FaceFinder API error for {npcFormKey}: {ex.Message}");
            await LogInteractionAsync(request, null, $"EXCEPTION: {ex.Message}");
            _eventLogger.Log($"FaceFinder API error for {npcFormKey}: {ex.Message}" + Environment.NewLine + Environment.NewLine + "Content" + Environment.NewLine + content);
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
                    var results = JsonConvert.DeserializeObject<List<dynamic>>(content, new JsonSerializerSettings 
                    { 
                        DateParseHandling = DateParseHandling.None 
                    });

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
                                await LogInteractionAsync(request, response, $"Invalid mod name: {modName}, image url: {imageUrl}, or updatedAt: {updatedAtStr}");
                                _eventLogger.Log($"{npcFormKey}: Invalid mod name: {modName}, image url: {imageUrl}, or updatedAt: {updatedAtStr}");
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
                    _eventLogger.Log($"FaceFinder API mod retrieval error for {npcFormKey} on page {currentPage}: {ex.Message}");
                }
            }

            // If NEITHER request returned data for this page, we are done
            if (!foundDataOnThisPage) break;

            currentPage++;
        }

        _eventLogger.Log($"FaceFinder Client: Returned {distinctFaces.Count()} appearance mods for {npcFormKey}");
        return distinctFaces.Values.ToList();
    }

    /// <summary>
    /// v2 counterpart of <see cref="GetAllFaceDataForNpcAsync"/> that walks the server's
    /// <c>/api/public/v2/npc/faces/search</c> endpoint. Per the FaceFinder maintainer, v2
    /// fixes the page-1 divergence the v1 method works around, so this is a plain pagination
    /// loop driven by the response envelope's <c>hasMore</c> flag — no implicit/explicit
    /// page-1 union, and no client-side dedup (the server is expected to return each face once).
    /// <para>The per-face JSON shape is assumed identical to v1's (<c>mod.name</c>,
    /// <c>images.full</c>, <c>updated_at</c>); only the envelope differs — v2 wraps the array in
    /// <c>{ "results": [...], "hasMore": bool }</c>. If the v2 face shape turns out to differ,
    /// only the field reads below need adjusting.</para>
    /// <para>Kept side-by-side with the v1 method (rather than replacing it) so the two can be
    /// compared head-to-head before the amalgamation hack is retired — see
    /// <c>FaceFinderV2EquivalenceTests</c>.</para>
    /// </summary>
    public async Task<List<FaceFinderNpcResult>> GetAllFaceDataForNpcV2Async(FormKey npcFormKey)
    {
        var faces = new List<FaceFinderNpcResult>();

        // Same key encoding as v1 (X8 hex + lowercased plugin filename) so both methods
        // ask the server about the exact same NPC.
        var formKeyValue = $"{npcFormKey.ID:X8}:{npcFormKey.ModKey.FileName.String.ToLowerInvariant()}";
        var encodedFormKey = WebUtility.UrlEncode(formKeyValue);

        int page = 1;
        while (true)
        {
            var requestUri = $"/api/public/v2/npc/faces/search?formKey={encodedFormKey}&page={page}";
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + requestUri));
            request.Headers.Add("X-API-Key", _apiKey);

            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                await LogInteractionAsync(request, response, content);

                if (!response.IsSuccessStatusCode) break;

                // Parse without auto-converting date strings, so updated_at stays a raw
                // string for the same RoundtripKind parse v1 uses.
                var envelope = JsonConvert.DeserializeObject<JObject>(content, new JsonSerializerSettings
                {
                    DateParseHandling = DateParseHandling.None
                });

                if (envelope?["results"] is JArray results)
                {
                    foreach (var entry in results)
                    {
                        string? modName = entry["mod"]?["name"]?.Value<string>();
                        string? imageUrl = entry["images"]?["full"]?.Value<string>();
                        string? updatedAtStr = entry["updated_at"]?.Value<string>();

                        if (string.IsNullOrWhiteSpace(modName) ||
                            string.IsNullOrWhiteSpace(imageUrl) ||
                            string.IsNullOrWhiteSpace(updatedAtStr) ||
                            !DateTime.TryParse(updatedAtStr, null, DateTimeStyles.RoundtripKind, out var updatedAt))
                        {
                            await LogInteractionAsync(request, response, $"(v2) Invalid mod name: {modName}, image url: {imageUrl}, or updatedAt: {updatedAtStr}");
                            _eventLogger.Log($"{npcFormKey}: (v2) Invalid mod name: {modName}, image url: {imageUrl}, or updatedAt: {updatedAtStr}");
                            continue;
                        }

                        faces.Add(new FaceFinderNpcResult
                        {
                            ModName = modName,
                            ImageUrl = imageUrl,
                            UpdatedAt = updatedAt
                        });
                    }
                }

                // Accept either casing for the paging flag; the v1 endpoints use snake_case
                // field names, so a v2 "has_more" would not be surprising.
                var hasMoreToken = envelope?["hasMore"] ?? envelope?["has_more"];
                bool hasMore = hasMoreToken?.Type == JTokenType.Boolean && hasMoreToken.Value<bool>();

                if (!hasMore) break;
                page++;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FaceFinder v2 API error on page {page}: {ex.Message}");
                await LogInteractionAsync(request, response, $"EXCEPTION: {ex.Message}");
                _eventLogger.Log($"FaceFinder v2 API mod retrieval error for {npcFormKey} on page {page}: {ex.Message}");
                break;
            }
        }

        _eventLogger.Log($"FaceFinder Client (v2): Returned {faces.Count} appearance mods for {npcFormKey}");
        return faces;
    }

    /// <summary>
    /// Trigger file (next to the exe) that forces the per-NPC face search back onto the v1
    /// "amalgamation" path. When absent — the normal case — the v2 endpoint is used. Mirrors
    /// the other file-keyed behaviour switches in the app (e.g. <c>LogBsaDiag.txt</c>,
    /// <c>LogStartup.txt</c>, <c>LogRenderTimings.txt</c>).
    /// </summary>
    public const string SearchFallbackTriggerFileName = "FaceFinderSearchFallback.txt";

    private static bool UseSearchFallback() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, SearchFallbackTriggerFileName));

    /// <summary>
    /// Entry point for the per-NPC face search. Uses the v2 endpoint
    /// (<see cref="GetAllFaceDataForNpcV2Async"/>) by default, since it was verified equivalent
    /// to — and supersedes — the v1 amalgamation hack now that the server's page-1 divergence is
    /// fixed. Drops back to v1 (<see cref="GetAllFaceDataForNpcAsync"/>) only when
    /// <c>FaceFinderSearchFallback.txt</c> is present next to the exe, so the old behaviour can
    /// be restored in the field without a UI toggle or a rebuild should the v2 endpoint regress.
    /// The file is re-checked per call, so the fallback takes effect without restarting the app.
    /// Both underlying methods remain callable directly.
    /// </summary>
    public Task<List<FaceFinderNpcResult>> SearchFacesForNpcAsync(FormKey npcFormKey) =>
        UseSearchFallback()
            ? GetAllFaceDataForNpcAsync(npcFormKey)
            : GetAllFaceDataForNpcV2Async(npcFormKey);

    /// <summary>
    /// Entry point for the full mod-name→id map. Uses the v2 endpoint
    /// (<see cref="GetAllModsV2Async"/>) by default; the same <c>FaceFinderSearchFallback.txt</c>
    /// trigger that reverts the per-NPC search also reverts this to the v1 amalgamation hack
    /// (<see cref="GetAllModsAsync"/>). Both underlying methods remain callable directly.
    /// </summary>
    public Task<Dictionary<string, int>> SearchAllModsAsync() =>
        UseSearchFallback()
            ? GetAllModsAsync()
            : GetAllModsV2Async();

    /// <summary>
    /// Entry point for "every face registered for a mod". Uses the v2 endpoint
    /// (<see cref="GetAllFacesForModV2Async"/>) by default; reverts to v1
    /// (<see cref="GetAllFacesForModAsync"/>) when <c>FaceFinderSearchFallback.txt</c> is present
    /// next to the exe. Both underlying methods remain callable directly.
    /// </summary>
    public Task<List<FaceFinderModFaceResult>> SearchFacesForModAsync(int modId) =>
        UseSearchFallback()
            ? GetAllFacesForModAsync(modId)
            : GetAllFacesForModV2Async(modId);

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