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
    private static readonly HttpClient _httpClient = new();
    private const string ApiBaseUrl = "https://npcfacefinder.com/";
    public const string MetadataFileExtension = ".ffmeta.json";
    
    public FaceFinderClient(Settings settings)
    {
        _settings = settings;
    }

    // Internal class for serializing metadata to a sidecar file
    public class FaceFinderMetadata
    {
        public string Source { get; set; } = "FaceFinder";
        public DateTime UpdatedAt { get; set; }
        public string? ExternalUrl { get; init; }
    }
    
    public async Task<List<string>> GetAllModNamesAsync(string encryptedApiKey)
    {
        var allModNames = new List<string>();
        if (string.IsNullOrWhiteSpace(encryptedApiKey)) return allModNames;

        string plainTextApiKey;
        try
        {
            byte[] encryptedBytes = Convert.FromBase64String(encryptedApiKey);
            byte[] apiBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            plainTextApiKey = Encoding.UTF8.GetString(apiBytes);
        }
        catch (Exception ex) {
            Debug.WriteLine($"Failed to decrypt FaceFinder API key for mod list: {ex.Message}");
            return allModNames;
        }

        int currentPage = 1;
        while (true)
        {
            var requestUri = $"/api/public/mods/search?page={currentPage}";
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + requestUri));
            request.Headers.Add("X-API-Key", plainTextApiKey);

            try
            {
                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode) break;

                var content = await response.Content.ReadAsStringAsync();
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

    public async Task<FaceFinderResult?> GetFaceDataAsync(FormKey npcFormKey, string modNameToFind, string encryptedApiKey)
    {
        if (string.IsNullOrWhiteSpace(encryptedApiKey)) return null;

        string plainTextApiKey;
        try
        {
            byte[] encryptedBytes = Convert.FromBase64String(encryptedApiKey);
            byte[] apiBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            plainTextApiKey = Encoding.UTF8.GetString(apiBytes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to decrypt FaceFinder API key: {ex.Message}");
            return null;
        }
        
        string faceFinderModName = modNameToFind;
        if (_settings.FaceFinderModNameMappings.TryGetValue(modNameToFind, out var mappedNames) && mappedNames.LastOrDefault() is { } lastMappedName)
        {
            faceFinderModName = lastMappedName;
            Debug.WriteLine($"Remapped local mod '{modNameToFind}' to FaceFinder mod '{faceFinderModName}'");
        }
        
        var formKeyValue = $"{npcFormKey.ID:X8}:{npcFormKey.ModKey.FileName}";
        var encodedFormKey = WebUtility.UrlEncode(formKeyValue);
        var encodedModName = WebUtility.UrlEncode(faceFinderModName); 
        var requestUri = $"/api/public/npc/faces/search?formKey={encodedFormKey}&search={encodedModName}";
        
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + requestUri));
        request.Headers.Add("X-API-Key", plainTextApiKey);

        try
        {
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            
            // Using Newtonsoft.Json to easily parse the structure
            var results = JsonConvert.DeserializeObject<List<dynamic>>(content);
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
            return null;
        }
    }

    public async Task<bool> IsCacheStaleAsync(string imagePath, FormKey npcFormKey, string modNameToFind, string encryptedApiKey)
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

            var latestData = await GetFaceDataAsync(npcFormKey, faceFinderModName, encryptedApiKey);
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
    
    public async Task<List<FaceFinderNpcResult>> GetAllFaceDataForNpcAsync(FormKey npcFormKey, string encryptedApiKey)
    {
        var allFaces = new List<FaceFinderNpcResult>();
        if (string.IsNullOrWhiteSpace(encryptedApiKey)) return allFaces;

        string plainTextApiKey;
        try
        {
            byte[] encryptedBytes = Convert.FromBase64String(encryptedApiKey);
            byte[] apiBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            plainTextApiKey = Encoding.UTF8.GetString(apiBytes);
        }
        catch (Exception ex) {
            Debug.WriteLine($"Failed to decrypt FaceFinder API key for NPC face list: {ex.Message}");
            return allFaces;
        }

        var formKeyValue = $"{npcFormKey.ID:X8}:{npcFormKey.ModKey.FileName}";
        var encodedFormKey = WebUtility.UrlEncode(formKeyValue);
        int currentPage = 1;

        while (true)
        {
            var requestUri = $"/api/public/npc/faces/search?formKey={encodedFormKey}&page={currentPage}";
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + requestUri));
            request.Headers.Add("X-API-Key", plainTextApiKey);

            try
            {
                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode) break;

                var content = await response.Content.ReadAsStringAsync();
                var results = JsonConvert.DeserializeObject<List<dynamic>>(content);

                if (results == null || !results.Any()) break; // No more pages.

                foreach (var result in results)
                {
                    string? modName = result.mod?.name;
                    string? imageUrl = result.images?.full;
                    string? updatedAtStr = result.updated_at;

                    if (string.IsNullOrWhiteSpace(modName) || string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(updatedAtStr) ||
                        !DateTime.TryParse(updatedAtStr, null, DateTimeStyles.RoundtripKind, out var updatedAt))
                    {
                        continue;
                    }

                    allFaces.Add(new FaceFinderNpcResult { ModName = modName, ImageUrl = imageUrl, UpdatedAt = updatedAt });
                }
                currentPage++;
            }
            catch (Exception ex) {
                Debug.WriteLine($"FaceFinder API error getting face list for {npcFormKey} on page {currentPage}: {ex.Message}");
                break;
            }
        }
        return allFaces;
    }
}