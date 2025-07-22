using System.IO;
using Loqui;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using System.Security.Cryptography;

#if NET8_0_OR_GREATER
using System.IO.Hashing;
#endif

namespace NPC_Plugin_Chooser_2.BackEnd;

public class Auxilliary
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly IAssetLinkCache _assetLinkCache;
    
    public Dictionary<ModKey, string> ModKeyPositionCache = new Dictionary<ModKey, string>();
    public Dictionary<FormKey, string> FormIDCache = new Dictionary<FormKey, string>();
    
    public Auxilliary(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
        _assetLinkCache = new AssetLinkCache(_environmentStateProvider.LinkCache);
    }

    public static string GetLogString(IMajorRecordGetter majorRecordGetter, bool fullString = false)
    {
        if (majorRecordGetter.EditorID != null)
        {
            if (fullString)
            {
                return majorRecordGetter.EditorID + " | " + majorRecordGetter.FormKey.ToString();
            }
            else
            {
                return majorRecordGetter.EditorID;
            }
        }
        else
        {
            return majorRecordGetter.FormKey.ToString();
        }
    }

    public static string GetNpcLogString(INpcGetter npcGetter, bool fullString = false)
    {
        string logString = "";
        if (npcGetter.Name != null && npcGetter.Name.String != null)
        {
            logString += npcGetter.Name.String;
            if (fullString)
            {
                logString += " | " + GetLogString(npcGetter, true);
            }
        }
        else
        {
            logString += GetLogString(npcGetter, fullString);
        }
        return logString;
    }

    public List<ModKey> GetModKeysInDirectory(string modFolderPath, List<string>? warnings, bool onlyEnabled)
    {
        List<ModKey> foundEnabledKeysInFolder = new();
        string modFolderName = Path.GetFileName(modFolderPath);
        try
        {
            var enabledKeys = _environmentStateProvider.EnvironmentIsValid ? _environmentStateProvider.LoadOrder.Keys.ToHashSet() : new HashSet<ModKey>();

            foreach (var filePath in Directory.EnumerateFiles(modFolderPath, "*.es*", SearchOption.TopDirectoryOnly))
            {
                string fileNameWithExt = Path.GetFileName(filePath);
                if (fileNameWithExt.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || fileNameWithExt.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || fileNameWithExt.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        ModKey parsedKey = ModKey.FromFileName(fileNameWithExt);
                        if (!onlyEnabled || enabledKeys.Contains(parsedKey))
                        {
                            foundEnabledKeysInFolder.Add(parsedKey);
                        }
                    }
                    catch (Exception parseEx) { warnings.Add($"Could not parse plugin '{fileNameWithExt}' in '{modFolderName}': {parseEx.Message}"); }
                }
            }
        }
        catch (Exception fileScanEx) { warnings.Add($"Error scanning Mod folder '{modFolderName}': {fileScanEx.Message}"); }
        
        return foundEnabledKeysInFolder;
    }
    
    public string FormKeyToFormIDString(FormKey formKey)
    {
        if (FormIDCache.ContainsKey(formKey))
        {
            return FormIDCache[formKey];
        }
        if (TryFormKeyToFormIDString(formKey, out string formIDstr))
        {
            FormIDCache[formKey] = formIDstr;
            return formIDstr;
        }
        return String.Empty;
    }

    /// <summary>
    /// Gets the relative file paths for FaceGen NIF and DDS files,
    /// ensuring the FormID component is an 8-character, zero-padded hex string.
    /// </summary>
    /// <param name="npcFormKey">The FormKey of the NPC.</param>
    /// <param name="regularized">Toggle to regularize file path relative to data folder.</param>
    /// <returns>A tuple containing the relative mesh path and texture path (lowercase).</returns>
    public static (string MeshPath, string TexturePath) GetFaceGenSubPathStrings(FormKey npcFormKey, bool regularized = false)
    {
        // Get the plugin filename string
        string pluginFileName = npcFormKey.ModKey.FileName.String; // Use .String property

        // Get the Form ID and format it as an 8-character uppercase hex string (X8)
        string formIDHex = npcFormKey.ID.ToString("X8"); // e.g., 0001A696

        // Construct the paths
        string meshPath = $"actors\\character\\facegendata\\facegeom\\{pluginFileName}\\{formIDHex}.nif";
        string texPath = $"actors\\character\\facegendata\\facetint\\{pluginFileName}\\{formIDHex}.dds";

        if (regularized)
        {
            if (TryRegularizePath(meshPath, out var regularizedMeshPath))
            {
                meshPath = regularizedMeshPath;
            }

            if (TryRegularizePath(texPath, out var regularizedTexPath))
            {
                texPath = regularizedTexPath;
            }
        }

        // Return lowercase paths for case-insensitive comparisons later
        return (meshPath.ToLowerInvariant(), texPath.ToLowerInvariant());
    }

    public bool TryFormKeyToFormIDString(FormKey formKey, out string formIDstr)
    {
        formIDstr = string.Empty;

        if (formKey.ModKey.FileName == _environmentStateProvider.OutputPluginName + ".esp")
        {
            formIDstr = _environmentStateProvider.LoadOrder.ListedOrder.Count().ToString("X"); // format FormID assuming the generated patch will be last in the load order
        }
        else
        {
            if (ModKeyPositionCache.ContainsKey(formKey.ModKey))
            {
                formIDstr = ModKeyPositionCache[formKey.ModKey];
            }
            else
            {
                for (int i = 0; i < _environmentStateProvider.LoadOrder.ListedOrder.Count(); i++)
                {
                    var currentListing = _environmentStateProvider.LoadOrder.ListedOrder.ElementAt(i);
                    if (currentListing.ModKey.Equals(formKey.ModKey))
                    {
                        formIDstr = i.ToString("X"); // https://www.delftstack.com/howto/csharp/integer-to-hexadecimal-in-csharp/
                        ModKeyPositionCache[formKey.ModKey] = formIDstr;
                        break;
                    }
                }
            }
        }
        if (!formIDstr.Any())
        {
            return false;
        }

        if (formIDstr.Length == 1)
        {
            formIDstr = "0" + formIDstr;
        }

        formIDstr += formKey.IDString();
        return true;
    }
    
    public enum PathType
    {
        File,
        Directory
    }
    public static dynamic CreateDirectoryIfNeeded(string path, PathType type)
    {
        if (type == PathType.File)
        {
            FileInfo file = new FileInfo(path);
            file.Directory.Create(); // If the directory already exists, this method does nothing.
            return file;
        }
        else
        {
            DirectoryInfo directory = new DirectoryInfo(path);
            directory.Create();
            return directory;
        }
    }

    public static string AddTopFolderByExtension(string path)
    {
        if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("textures", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine("Textures", path);
        }
        
        if ((path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".tri", StringComparison.OrdinalIgnoreCase)) &&
            !path.StartsWith("meshes", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine("Meshes", path);
        }
        
        return path;
    }
    
    /// <summary>
    /// Attempts to regularise <paramref name="inputPath"/> so that the result is:
    ///     textures\arbitrary\file.dds     – or –
    ///     meshes\arbitrary\file.nif
    /// The method accepts                             
    ///   • absolute paths that contain “…\data\<type>\…”
    ///   • relative paths that already start with <type>\
    ///   • bare “arbitrary\file.ext”, inferring <type> from the extension.
    /// </summary>
    public static bool TryRegularizePath(string? inputPath, out string regularizedPath)
    {
        regularizedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(inputPath))
            return false;

        // Normalise path separators.
        var path = inputPath.Replace('/', '\\').Trim();

        // Determine the expected type folder from the extension.
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var expectedType = ext switch
        {
            ".dds" => "textures",
            ".nif" => "meshes",
            _      => null
        };
        if (expectedType is null) return false;   // unsupported extension

        // Split into components.
        var segments = path
            .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // Try to find “…\data\<type>\…”
        var dataIdx = segments
            .FindIndex(s => s.Equals("data", StringComparison.OrdinalIgnoreCase));

        if (dataIdx >= 0 && dataIdx + 1 < segments.Count)
        {
            // Absolute path including “…\data\<type>\…”
            var typeSegment = segments[dataIdx + 1];
            if (!typeSegment.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
                return false;                     // data\[type] conflicts with extension

            regularizedPath = string.Join("\\", segments.Skip(dataIdx + 1));
            return true;
        }

        // Relative path already starts with a type folder?
        if (segments[0].Equals(expectedType, StringComparison.OrdinalIgnoreCase))
        {
            regularizedPath = string.Join("\\", segments);
            return true;
        }

        // Bare “arbitrary\file.ext” – prepend inferred type.
        regularizedPath = $"{expectedType}\\{string.Join("\\", segments)}";
        return true;
    }

    public static bool TryDuplicateGenericRecordAsNew(IMajorRecordGetter recordGetter, ISkyrimMod outputMod, out dynamic? duplicateRecord, out string exceptionString)
    {
        if(TryGetPatchRecordGroup(recordGetter, outputMod, out var group, out exceptionString) && group != null)
        {
            duplicateRecord = IGroupMixIns.DuplicateInAsNewRecord(group, recordGetter);
            return true;
        }

        duplicateRecord = null;
        return false;
    }
    
    public static bool TryGetOrAddGenericRecordAsOverride(IMajorRecordGetter recordGetter, ISkyrimMod outputMod, out MajorRecord? duplicateRecord, out string exceptionString)
    {
        using var _ = ContextualPerformanceTracer.Trace("Auxilliary.TryGetOrAddGenericRecordAsOverride");
        if(TryGetPatchRecordGroup(recordGetter, outputMod, out var group, out exceptionString) && group != null)
        {
            duplicateRecord = OverrideMixIns.GetOrAddAsOverride(group, recordGetter);
            return true;
        }
        duplicateRecord = null;
        return false;
    }

    public static bool TryGetPatchRecordGroup(IMajorRecordGetter recordGetter, ISkyrimMod outputMod, out dynamic? group, out string exceptionString)
    {
        exceptionString = string.Empty;
        var getterType = GetRecordGetterType(recordGetter);
        try
        {
            group = outputMod.GetTopLevelGroup(getterType);
            return true;
        }
        catch (Exception e)
        {
            group = null;
            exceptionString = e.Message;
            return false;
        } 
    }

    public static Type? GetRecordGetterType(IMajorRecordGetter recordGetter)
    {
        try
        {
            return LoquiRegistration.GetRegister(recordGetter.GetType()).GetterType;
        }
        catch (Exception e)
        {
            return null;
        }
        
    }

    public static Type? GetLoquiType(Type type)
    {
        try
        {
            return LoquiRegistration.GetRegister(type).GetterType;
        }
        catch
        {
            return null;
        }
    }

    public void CollectShallowAssetLinks(IEnumerable<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> recordContexts, List<IAssetLinkGetter> assetLinks)
    {
        foreach (var context in recordContexts)
        {
            var recordAssetLinks = ShallowGetAssetLinks(context.Record);
            assetLinks.AddRange(recordAssetLinks.Where(x => !assetLinks.Contains(x)));
        }
    }
    
    public void CollectShallowAssetLinks(IEnumerable<IMajorRecordGetter> recordGetters, List<IAssetLinkGetter> assetLinks)
    {
        using var _ = ContextualPerformanceTracer.Trace("Aux.CollectShallowAssetLinks");
        foreach (var recordGetter in recordGetters)
        {
            var recordAssetLinks = ShallowGetAssetLinks(recordGetter);
            assetLinks.AddRange(recordAssetLinks.Where(x => !assetLinks.Contains(x)));
        }
    }
    public List<IAssetLinkGetter> ShallowGetAssetLinks(IMajorRecordGetter recordGetter)
    {
        return recordGetter.EnumerateAssetLinks(AssetLinkQuery.Listed, _assetLinkCache, null)
            .ToList();
    }
    public List<IAssetLinkGetter> DeepGetAssetLinks(IMajorRecordGetter recordGetter, List<ModKey> relevantContextKeys)
    {
        var assetLinks = recordGetter.EnumerateAssetLinks(AssetLinkQuery.Listed, _assetLinkCache, null)
            .ToList();
        foreach (var formLink in recordGetter.EnumerateFormLinks())
        {
            CollectDeepAssetLinks(formLink, assetLinks, relevantContextKeys, _assetLinkCache);
        }

        return assetLinks;
    }
    
    private void CollectDeepAssetLinks(IFormLinkGetter formLinkGetter, List<IAssetLinkGetter> assetLinkGetters, List<ModKey> relevantContextKeys, IAssetLinkCache assetLinkCache, HashSet<FormKey>? searchedFormKeys = null)
    {
        if (searchedFormKeys == null)
        {
            searchedFormKeys = new HashSet<FormKey>();
        }
        searchedFormKeys.Add(formLinkGetter.FormKey);
        var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts(formLinkGetter);
        foreach (var context in contexts)
        {
            if (relevantContextKeys.Contains(context.ModKey))
            {
                assetLinkGetters.AddRange(
                    context.Record.EnumerateAssetLinks(AssetLinkQuery.Listed, assetLinkCache, null));
            }

            var sublinks = context.Record.EnumerateFormLinks();
            foreach (var subLink in sublinks.Where(x => !searchedFormKeys.Contains(x.FormKey)))
            {
                CollectDeepAssetLinks(subLink, assetLinkGetters, relevantContextKeys, assetLinkCache, searchedFormKeys);
            }
        }
    }
    
    private const int BufferSize = 4 * 1024 * 1024;   // 4 MB blocks

    /* -----------------------------------------------------------------------
     * 1.  Pre-compute identifiers for a file
     * -------------------------------------------------------------------- */
    public static (int Length, string CheapHash) GetCheapFileEqualityIdentifiers(string filePath)
    {
        if (filePath is null) throw new ArgumentNullException(nameof(filePath));

        var info = new FileInfo(filePath);
        if (!info.Exists) throw new FileNotFoundException("File not found.", filePath);

        int length = unchecked((int)info.Length);             // cast keeps original API
        string cheapHash = ComputeXxHash128Hex(info);

        return (length, cheapHash);
    }

    /* -----------------------------------------------------------------------
     * 2.  Compare another file against the pre-computed identifiers
     * -------------------------------------------------------------------- */
    public static bool FastFilesAreIdentical(string candidateFilePath,
                                            int    targetFileLength,
                                            string targetFileCheapHash)
    {
        if (candidateFilePath is null)      throw new ArgumentNullException(nameof(candidateFilePath));
        if (targetFileCheapHash is null)    throw new ArgumentNullException(nameof(targetFileCheapHash));

        var info = new FileInfo(candidateFilePath);
        if (!info.Exists) return false;

        // Early-out: different size ⇒ definitely different file
        if (unchecked((int)info.Length) != targetFileLength)
            return false;

        // Sizes match – compute the same cheap hash and compare
        string candidateHash = ComputeXxHash128Hex(info);

        return candidateHash.Equals(targetFileCheapHash, StringComparison.OrdinalIgnoreCase);
    }

    /* -----------------------------------------------------------------------
     * 3.  Private helper to compute XXH128 as an uppercase hex string
     * -------------------------------------------------------------------- */
    private static string ComputeXxHash128Hex(FileInfo info)
    {
        Span<byte> digest = stackalloc byte[16];   // 128 bits = 16 bytes
        var hasher = new XxHash128();

        using var stream = info.OpenRead();
        byte[] buffer = new byte[BufferSize];

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.Append(buffer.AsSpan(0, read));
        }

        hasher.GetHashAndReset(digest);
        return Convert.ToHexString(digest);        // e.g. "A1B2C3D4E5F6..."
    }
}