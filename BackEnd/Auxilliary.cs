using System.IO;
using Loqui;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class Auxilliary
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly IAssetLinkCache _assetLinkCache;
    private Dictionary<ModKey, ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>> _modLinkCaches = new();
    
    public Dictionary<ModKey, string> ModKeyPositionCache = new Dictionary<ModKey, string>();
    public Dictionary<FormKey, string> FormIDCache = new Dictionary<FormKey, string>();
    
    public Auxilliary(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
        _assetLinkCache = new AssetLinkCache(_environmentStateProvider.LinkCache);
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
    /// <returns>A tuple containing the relative mesh path and texture path (lowercase).</returns>
    public static (string MeshPath, string TexturePath) GetFaceGenSubPathStrings(FormKey npcFormKey)
    {
        // Get the plugin filename string
        string pluginFileName = npcFormKey.ModKey.FileName.String; // Use .String property

        // Get the Form ID and format it as an 8-character uppercase hex string (X8)
        string formIDHex = npcFormKey.ID.ToString("X8"); // e.g., 0001A696

        // Construct the paths
        string meshPath = $"actors\\character\\facegendata\\facegeom\\{pluginFileName}\\{formIDHex}.nif";
        string texPath = $"actors\\character\\facegendata\\facetint\\{pluginFileName}\\{formIDHex}.dds";

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

    public HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>
        DeepGetOverriddenDependencyRecords(IMajorRecordGetter majorRecordGetter, List<ModKey> relevantContextKeys)
    {
        var containedFormLinks = majorRecordGetter.EnumerateFormLinks().ToArray();
        foreach (var modKey in relevantContextKeys)
        {
            var modListing = _environmentStateProvider.LoadOrder.TryGetValue(modKey);
            if (modListing != null && modListing.Mod != null)
            {
                _modLinkCaches.TryAdd(modKey, new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(modListing.Mod, new LinkCachePreferences()));
            }
        }
        HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> dependencyContexts = new();
        foreach (var link in containedFormLinks)
        {
            CollectOverriddenDependencyRecords(link, relevantContextKeys, dependencyContexts, 2, 0);
        }
        return dependencyContexts.Distinct().ToHashSet();;
    }

    public bool GetRecordFromMod(IFormLinkGetter formLink, ModKey modKey, out IMajorRecordGetter? record)
    {
        if (_modLinkCaches.ContainsKey(modKey) && _modLinkCaches[modKey].TryResolve(formLink, out var modRecord) && modRecord is not null)
        {
            record = modRecord;
            return true;
        }
        else if (_environmentStateProvider.LoadOrder.Keys.Contains(modKey))
        {
            var listing = _environmentStateProvider.LoadOrder.TryGetValue(modKey);
            if (listing != null && listing.Mod != null)
            {
                _modLinkCaches[listing.ModKey] = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(listing.Mod, new LinkCachePreferences());
                if (_modLinkCaches[listing.ModKey].TryResolve(formLink, out modRecord) && modRecord is not null)
                {
                    record = modRecord;
                    return true;
                }
            }
        }
        record = null;
        return false;
    }
    
    public static MajorRecord GetOrAddGenericRecordAsOverride(IMajorRecordGetter recordGetter, ISkyrimMod outputMod)
    {
        dynamic group = GetPatchRecordGroup(recordGetter, outputMod);
        return OverrideMixIns.GetOrAddAsOverride(group, recordGetter);
    }

    public static IGroup GetPatchRecordGroup(IMajorRecordGetter recordGetter, ISkyrimMod outputMod)
    {
        var getterType = GetRecordGetterType(recordGetter);
        return outputMod.GetTopLevelGroup(getterType);
    }

    public static Type GetRecordGetterType(IMajorRecordGetter recordGetter)
    {
        return LoquiRegistration.GetRegister(recordGetter.GetType()).GetterType;
    }
    
    private void CollectOverriddenDependencyRecords(IFormLinkGetter formLinkGetter, List<ModKey> relevantContextKeys,
        HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> collectedRecords, int maxNestedIntervalDepth, int currentDepth, HashSet<FormKey>? searchedFormKeys = null)
    {
        if (formLinkGetter.IsNull)
        {
            return;
        }

        currentDepth++;
        if (currentDepth > maxNestedIntervalDepth) {return;}
        
        if (searchedFormKeys == null)
        {
            searchedFormKeys = new HashSet<FormKey>();
        }
        
        searchedFormKeys.Add(formLinkGetter.FormKey);

        IMajorRecordGetter? modRecord = null;
        
        // try to get the record version in the given mod plugin if possible
        foreach (var modKey in relevantContextKeys)
        {
            if (_modLinkCaches.ContainsKey(modKey) && _modLinkCaches[modKey].TryResolve(formLinkGetter, out modRecord) && modRecord != null)
            {
                var context = _modLinkCaches[modKey].ResolveContext(formLinkGetter);
                collectedRecords.Add(context);
                currentDepth = 0; // reset the interval search
                break;
            }
        }
        
        // otherwise, traverse the parent record
        if (modRecord is null)
        {
            var parentmod = formLinkGetter.FormKey.ModKey;
            if (!_modLinkCaches.ContainsKey(parentmod))
            {
                var parentListing = _environmentStateProvider.LoadOrder.TryGetValue(parentmod);
                if (parentListing != null && parentListing.Mod != null)
                {
                    _modLinkCaches[parentListing.ModKey] = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(parentListing.Mod, new LinkCachePreferences());
                }
            }

            if (_modLinkCaches.ContainsKey(parentmod))
            {
                _modLinkCaches[parentmod].TryResolve(formLinkGetter, out modRecord);
            }
        }
        
        if (modRecord != null)
        {
            var sublinks = modRecord.EnumerateFormLinks();
            foreach (var subLink in sublinks.Where(x => !searchedFormKeys.Contains(x.FormKey)).ToArray())
            {
                CollectOverriddenDependencyRecords(subLink, relevantContextKeys, collectedRecords, maxNestedIntervalDepth, currentDepth, searchedFormKeys);
            }
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

    // Define Base Game Plugins
    private static readonly HashSet<ModKey> BaseGamePlugins = new()
    {
        ModKey.FromNameAndExtension("Skyrim.esm"),
        ModKey.FromNameAndExtension("Update.esm"),
        ModKey.FromNameAndExtension("Dawnguard.esm"),
        ModKey.FromNameAndExtension("HearthFires.esm"),
        ModKey.FromNameAndExtension("Dragonborn.esm")
    };
    public bool IsBaseGamePlugin(ModKey modKey)
    {
        return BaseGamePlugins.Contains(modKey);
    }
}