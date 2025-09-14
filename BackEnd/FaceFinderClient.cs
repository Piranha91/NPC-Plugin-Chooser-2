using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NPC_Plugin_Chooser_2.BackEnd;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Mutagen.Bethesda.Plugins;

public class FaceFinderClient
{
    private static readonly HttpClient _httpClient = new();
    private const string ApiBaseUrl = "https://npcfacefinder.com/";

    public async Task<string?> GetFaceImageUrlAsync(FormKey npcFormKey, string encryptedApiKey)
    {
        if (string.IsNullOrWhiteSpace(encryptedApiKey)) return null;

        string plainTextApiKey;
        try
        {
            // 1. Decode the Base64 string to get the encrypted byte array.
            byte[] encryptedBytes = Convert.FromBase64String(encryptedApiKey);

            // 2. Decrypt the data. This will only work for the user who encrypted it on the same machine.
            byte[] apiBytes = ProtectedData.Unprotect(
                encryptedBytes,
                null, // Optional entropy must match what was used for protection
                DataProtectionScope.CurrentUser);
            
            plainTextApiKey = Encoding.UTF8.GetString(apiBytes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to decrypt FaceFinder API key: {ex.Message}");
            return null; // Don't proceed if decryption fails.
        }
        
        // 3. Use the decrypted key for the request.
        var requestUri = $"/api/public/npc/faces/search?formKey={npcFormKey.ID:X6}:{npcFormKey.ModKey.FileName}";
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + requestUri));
        request.Headers.Add("X-API-Key", plainTextApiKey);

        try
        {
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);

            var firstResult = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (firstResult.TryGetProperty("images", out var images) && images.TryGetProperty("full", out var fullImage))
            {
                return fullImage.GetString();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FaceFinder API error for {npcFormKey}: {ex.Message}");
            return null;
        }
        return null;
    }
}