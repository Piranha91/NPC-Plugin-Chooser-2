using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Loqui;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Strings;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;

#if NET8_0_OR_GREATER
using System.IO.Hashing;
#endif

namespace NPC_Plugin_Chooser_2.BackEnd;

public class Auxilliary : IDisposable
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private IAssetLinkCache _assetLinkCache;
    
    private readonly CompositeDisposable _disposables = new();
    
    // caches to speed up building
    public Dictionary<FormKey, string> FormIDCache = new();
    private ConcurrentDictionary<FormKey, RaceEvaluation> _raceValidityCache = new();
    private string _raceValidityCacheFileName = "RaceEvalCache.json";

    public static HashSet<string> ValidPluginExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".esp",
        ".esm",
        ".esl"
    };
    
    private enum RaceEvaluation
    {
        Valid,
        InvalidNull,
        InvalidNotInLoadOrder,
        InvalidNullKeywords,
        InvalidNotNpc
    }
    
    public Auxilliary(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
        
        _environmentStateProvider.OnEnvironmentUpdated
            .ObserveOn(RxApp.MainThreadScheduler) // Ensure re-initialization happens on the UI thread if needed
            .Subscribe(_ =>
            {
                if (_environmentStateProvider is null || _environmentStateProvider?.LinkCache is null)
                {
                    Debug.WriteLine("Aux: Environment state is not initialized");
                    return;
                }
                _assetLinkCache = new AssetLinkCache(_environmentStateProvider.LinkCache);
            })
            .DisposeWith(_disposables); // Add the subscription to the container for easy cleanup
    }
    
    public void Dispose()
    {
        // Clean up all subscriptions when this object is disposed
        _disposables.Dispose();
    }

    public void ReinitializeModDependentProperties()
    {
        FormIDCache.Clear();
        _raceValidityCache.Clear();
        LoadRaceCache();
    }

    public static bool TryGetName(ITranslatedNamedGetter namedGetter, Language? language, bool fixGarbled, out string name)
    {
        name = string.Empty;

        if (namedGetter.Name == null)
        {
            return false;
        }

        if (language != null && namedGetter.Name.TryLookup(language.Value, out var localizedName))
        {
            name = localizedName;
            if (fixGarbled)
            {
                name = FixMojibake(name);
            }
            return true;
        }
        else if (namedGetter.Name.String != null)
        {
            name = namedGetter.Name.String;
            if (fixGarbled)
            {
                name = FixMojibake(name);
            }
            return true;
        }
        return false;
    }
    
    private static readonly Lazy<Encoding> _windows1252 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    });

    /// <summary>
    /// Attempts to fix mojibake (UTF-8 bytes misinterpreted as Windows-1252).
    /// Only applies the fix if the input actually appears to be garbled.
    /// Returns the original string unchanged if it already contains valid
    /// non-Latin script characters (CJK, Cyrillic, etc.).
    /// </summary>
    public static string FixMojibake(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        try
        {
            // STEP 1: If the string already contains valid non-Latin characters
            //         (CJK, Cyrillic, Korean, Thai, Arabic, Hebrew, etc.),
            //         it was decoded correctly — don't touch it.
            if (ContainsNonLatinScript(input))
                return input;

            // STEP 2: Check if the string contains character patterns typical of
            //         UTF-8 → Windows-1252 mojibake. If it doesn't look garbled,
            //         leave it alone.
            if (!LooksLikeMojibake(input))
                return input;

            // STEP 3: Attempt the round-trip: Windows-1252 → bytes → UTF-8
            byte[] rawBytes = _windows1252.Value.GetBytes(input);
            string candidate = Encoding.UTF8.GetString(rawBytes);

            // STEP 4: Validate the result — it should contain meaningful characters
            //         and not have the UTF-8 replacement character (U+FFFD) which
            //         indicates the bytes weren't valid UTF-8 after all.
            if (candidate.Contains('\uFFFD'))
                return input; // Conversion produced errors — not actually mojibake

            // STEP 5: Sanity check — the result should have at least some 
            //         non-ASCII content (the whole point of the fix).
            if (ContainsNonLatinScript(candidate) || IsReasonableText(candidate))
                return candidate;

            // If we get here, the conversion didn't produce clearly better text
            return input;
        }
        catch (Exception)
        {
            return input;
        }
    }

    /// <summary>
    /// Returns true if the string contains characters from non-Latin scripts,
    /// indicating it was already decoded correctly.
    /// Covers: CJK Unified, Hiragana, Katakana, Hangul, Cyrillic, Arabic,
    ///         Hebrew, Thai, Devanagari, and other major scripts.
    /// </summary>
    private static bool ContainsNonLatinScript(string s)
    {
        foreach (char c in s)
        {
            // CJK Unified Ideographs (Chinese/Japanese Kanji)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
            // CJK Extension A
            if (c >= 0x3400 && c <= 0x4DBF) return true;
            // Hiragana
            if (c >= 0x3040 && c <= 0x309F) return true;
            // Katakana
            if (c >= 0x30A0 && c <= 0x30FF) return true;
            // Hangul Syllables (Korean)
            if (c >= 0xAC00 && c <= 0xD7AF) return true;
            // Cyrillic
            if (c >= 0x0400 && c <= 0x04FF) return true;
            // Arabic
            if (c >= 0x0600 && c <= 0x06FF) return true;
            // Hebrew
            if (c >= 0x0590 && c <= 0x05FF) return true;
            // Thai
            if (c >= 0x0E00 && c <= 0x0E7F) return true;
            // Devanagari
            if (c >= 0x0900 && c <= 0x097F) return true;
            // CJK Compatibility Ideographs
            if (c >= 0xF900 && c <= 0xFAFF) return true;
            // Halfwidth/Fullwidth Katakana & CJK
            if (c >= 0xFF65 && c <= 0xFFDC) return true;
            // CJK Symbols and Punctuation
            if (c >= 0x3000 && c <= 0x303F) return true;
        }
        return false;
    }

    /// <summary>
    /// Heuristic: returns true if the string contains character sequences
    /// characteristic of UTF-8 multibyte sequences misread as Windows-1252.
    ///
    /// UTF-8 encodes non-ASCII as 2-4 byte sequences starting with specific
    /// lead bytes. When misread as Windows-1252, these produce distinctive 
    /// patterns:
    ///   - 2-byte: lead Ã (0xC3) followed by a character in 0x80-0xBF range
    ///   - 3-byte (CJK/Cyrillic): lead Ã£/Ã¤/Ã¥/Ã¨/Ã© (0xE3-0xE9 mapped)
    ///             followed by two chars in the 0x80-0xBF range
    ///   - General: high density of characters in 0x80-0xBF range (continuation
    ///             bytes) which are unusual in legitimate Windows-1252 text
    /// </summary>
    private static bool LooksLikeMojibake(string s)
    {
        if (s.Length < 2)
            return false;

        int suspiciousPatterns = 0;
        int highByteCount = 0;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            // Count characters in the 0x80-0xBF range (UTF-8 continuation bytes
            // when misread as Windows-1252 produce chars like €,‚,ƒ,„,…,†,‡,ˆ,‰,
            // Š,‹,Œ,Ž,',' etc.)
            if (c >= 0x0080 && c <= 0x00BF)
                highByteCount++;

            // Pattern: Ã (0xC3) followed by a Latin character — classic 2-byte mojibake
            // This catches accented characters like é→Ã©, ü→Ã¼, ñ→Ã±, etc.
            if (c == 0x00C3 && i + 1 < s.Length)
            {
                char next = s[i + 1];
                if (next >= 0x0080 && next <= 0x00BF)
                    suspiciousPatterns++;
            }

            // Pattern: Ã¢/Ã£/Ã¤/Ã¥ (0xC2-C5 range) + two continuation-like chars
            // This catches 3-byte UTF-8 sequences (CJK, etc.)
            if (c >= 0x00C2 && c <= 0x00C5 && i + 2 < s.Length)
            {
                char next1 = s[i + 1];
                char next2 = s[i + 2];
                if (next1 >= 0x0080 && next1 <= 0x00BF &&
                    next2 >= 0x0080 && next2 <= 0x00BF)
                    suspiciousPatterns++;
            }

            // Pattern: Ã¨/Ã© (0xE8/0xE9 lead byte range for 3-byte CJK)  
            // mapped through Windows-1252 these become è/é followed by 
            // continuation bytes
            if ((c == 0x00E8 || c == 0x00E9 || c == 0x00E3 || c == 0x00E4 || c == 0x00E5) 
                && i + 2 < s.Length)
            {
                char next1 = s[i + 1];
                char next2 = s[i + 2];
                if (next1 >= 0x0080 && next1 <= 0x00BF &&
                    next2 >= 0x0080 && next2 <= 0x00BF)
                    suspiciousPatterns++;
            }
        }

        // If we found suspicious patterns, it's likely mojibake
        if (suspiciousPatterns > 0)
            return true;

        // High density of 0x80-0xBF range characters is suspicious
        // (these are rare in legitimate Western text)
        if (s.Length >= 3 && (double)highByteCount / s.Length > 0.3)
            return true;

        return false;
    }

    /// <summary>
    /// Basic check that the converted text looks reasonable
    /// (not all replacement characters or control characters).
    /// </summary>
    private static bool IsReasonableText(string s)
    {
        int printableCount = 0;
        foreach (char c in s)
        {
            if (!char.IsControl(c) && c != '\uFFFD')
                printableCount++;
        }
        return printableCount > s.Length * 0.5;
    }
    
    public static string GetLogString(IMajorRecordGetter majorRecordGetter, Language? language, bool fullString = false)
    {
        StringBuilder logBuilder = new();
        if (majorRecordGetter is ITranslatedNamedGetter namedGetter)
        {
            if (namedGetter.Name != null && namedGetter.Name.String != null)
            {
                if (language != null && namedGetter.Name.TryLookup(language.Value, out var localizedName))
                {
                    logBuilder.Append(localizedName);
                }
                else
                {
                    logBuilder.Append(namedGetter.Name.String);
                }
            }

            if (fullString)
            {
                logBuilder.Append(" | ");
            }
        }

        if (logBuilder.Length == 0 || fullString)
        {
            if (majorRecordGetter.EditorID != null)
            {
                logBuilder.Append(majorRecordGetter.EditorID + " | ");
            }

            if (logBuilder.Length == 0 || fullString)
            {
                logBuilder.Append(majorRecordGetter.FormKey.ToString());
            }
        }
        
        return logBuilder.ToString();
    }

    public bool IsValidAppearanceRace(FormKey raceFormKey, INpcGetter npcGetter, Language? language, out string rejectionMessage, IRaceGetter? sourcePluginRace = null)
    {
        bool isCached = false;
        bool isValid = true;
        rejectionMessage = "";
        RaceEvaluation raceEvaluation;

        using (ContextualPerformanceTracer.Trace("IVAR.CacheCheck1"))
        {
            isCached = _raceValidityCache.TryGetValue(raceFormKey, out raceEvaluation);
            // Try Cache first
            if (isCached && raceEvaluation == RaceEvaluation.Valid)
            {
                return true;
            }
        }

        bool isTemplate = false;
        using (ContextualPerformanceTracer.Trace("IVAR.TemplateCheck"))
        {
            isTemplate = npcGetter.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag
                .Traits);

            if (isTemplate)
            {
                return true; // return true without cacheing; this NPC's race is irrelevant
            }
        }
        
        if (!isCached)
        {
            // new race, had not yet been cached
            IRaceGetter? raceGetter = null;
            string identifier = raceFormKey.ToString();
            using (ContextualPerformanceTracer.Trace("IVAR.NewRace.Resolution"))
            {
                bool raceResolved = false;

                if (sourcePluginRace != null)
                {
                    raceGetter = sourcePluginRace;
                }
                else if (raceFormKey.IsNull)
                {
                    raceEvaluation = RaceEvaluation.InvalidNull;
                }
                else if (!_environmentStateProvider.LinkCache.TryResolve<IRaceGetter>(raceFormKey,
                             out raceGetter) || raceGetter is null)
                {
                    raceEvaluation = RaceEvaluation.InvalidNotInLoadOrder;
                }
            }

            if (raceGetter is not null)
            {
                using (ContextualPerformanceTracer.Trace("IVAR.NewRace.Evaluation"))
                {
                    if (raceGetter.Keywords == null)
                    {
                        raceEvaluation = RaceEvaluation.InvalidNullKeywords;
                        identifier = GetLogString(raceGetter, language, true);
                    }
                    else if (!isTemplate && !raceGetter.Keywords.Contains(Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim
                                 .Keyword
                                 .ActorTypeNPC))
                    {
                        raceEvaluation = RaceEvaluation.InvalidNotNpc;
                        identifier = GetLogString(raceGetter, language, true);
                    }
                    else
                    {
                        raceEvaluation = RaceEvaluation.Valid;
                        identifier = GetLogString(raceGetter, language, true);
                    }

                    // now cache the newly evaluated race
                    using (ContextualPerformanceTracer.Trace("IVAR.AddToCache"))
                    {
                        _raceValidityCache.TryAdd(raceFormKey, raceEvaluation);
                        Debug.WriteLine(
                            $"Evaluating validity for new race: {identifier} with result: {raceEvaluation}");
                    }
                }
            }
        }
        
        using (ContextualPerformanceTracer.Trace("IVAR.Decision"))
        {
            if (raceEvaluation == RaceEvaluation.InvalidNull)
            {
                rejectionMessage = "its race is null.";
                return false;
            }
            if (raceEvaluation == RaceEvaluation.InvalidNotInLoadOrder)
            {
                rejectionMessage = "its race is not in the current load order.";
                return false;
            }
            if (raceEvaluation == RaceEvaluation.InvalidNullKeywords)
            {
                rejectionMessage = "its race is missing Keywords.";
                return false;
            }

            if (raceEvaluation == RaceEvaluation.InvalidNotNpc)
            {
                rejectionMessage = "its race is missing the ActorTypeNPC keyword.";
                return false;
            }
        }
        
        return true;
    }

    public void SaveRaceCache()
    {
        string cachePath = Path.Combine(AppContext.BaseDirectory, _raceValidityCacheFileName);

        var filteredCache = _raceValidityCache.Where(x => !x.Key.IsNull); // null formkeys don't serialize correctly
        
        JSONhandler<ConcurrentDictionary<FormKey, RaceEvaluation>>.SaveJSONFile(new ConcurrentDictionary<FormKey, RaceEvaluation>(filteredCache), cachePath, out bool success, out string exceptionMessage );
        if (!success)
        {
            Debug.WriteLine("Exception while saving race cache." + Environment.NewLine + exceptionMessage);
        }
    }

    public void LoadRaceCache()
    {
        string cachePath = Path.Combine(AppContext.BaseDirectory, _raceValidityCacheFileName);
        if (File.Exists(cachePath))
        {
            var rawCache = JSONhandler<ConcurrentDictionary<FormKey, RaceEvaluation>>.LoadJSONFile(cachePath, out bool success, out string exceptionMessage );
            if (!success || rawCache == null)
            {
                _raceValidityCache = new();
                Debug.WriteLine("Exception while loading race cache." + Environment.NewLine + exceptionMessage);
            }
            else
            {
                var filteredCache = rawCache.Where(x => x.Value != RaceEvaluation.InvalidNotInLoadOrder); // try re-evaluating these races in case they appear in the load order
                _raceValidityCache = new ConcurrentDictionary<FormKey, RaceEvaluation>(filteredCache);
                _raceValidityCache.TryAdd(new FormKey(), RaceEvaluation.InvalidNull);
            }
        }
    }

    public List<ModKey> GetModKeysInDirectory(string modFolderPath, List<string>? warnings, bool onlyEnabled)
    {
        List<ModKey> foundEnabledKeysInFolder = new();
        string modFolderName = Path.GetFileName(modFolderPath);
        try
        {
            var enabledKeys = _environmentStateProvider.Status == EnvironmentStateProvider.EnvironmentStatus.Valid ? _environmentStateProvider.LoadOrder.Keys.ToHashSet() : new HashSet<ModKey>();

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
        if (FormIDCache.TryGetValue(formKey, out var cachedId))
        {
            return cachedId;
        }
        
        if (TryFormKeyToFormIDString(formKey, out string formIDstr))
        {
            FormIDCache[formKey] = formIDstr;
            return formIDstr;
        }
        return string.Empty;
    }
    
    public static bool IsFemale(INpcGetter npc)
    {
        return npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
    }

    public static Gender GetGender(INpcGetter npc)
    {
        if (IsFemale(npc))
        {
            return Gender.Female;
        }
        else
        {
            return Gender.Male;
        }
    }

    public static bool HasTraitsFlag(INpcGetter npc)
    {
        return npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits);
    }
    
    /// <summary>
    /// Lazily enumerates all major records in a mod without loading them all into memory at once.
    /// Processing stops as soon as the consuming loop breaks.
    /// </summary>
    public static IEnumerable<IFormLinkGetter<IMajorRecordGetter>> LazyEnumerateMajorRecords(ISkyrimModGetter mod)
    {
        // Use an empty enumerable if a property is null to prevent NullReferenceException
        var empty = Enumerable.Empty<IMajorRecordGetter>();
        
        foreach (var record in mod.AcousticSpaces ?? empty) yield return record.ToLink();
        foreach (var record in mod.Actions ?? empty) yield return record.ToLink();
        foreach (var record in mod.Activators ?? empty) yield return record.ToLink();
        foreach (var record in mod.ActorValueInformation ?? empty) yield return record.ToLink();
        foreach (var record in mod.AddonNodes ?? empty) yield return record.ToLink();
        foreach (var record in mod.AlchemicalApparatuses ?? empty) yield return record.ToLink();
        foreach (var record in mod.Ammunitions ?? empty) yield return record.ToLink();
        foreach (var record in mod.AnimatedObjects ?? empty) yield return record.ToLink();
        foreach (var record in mod.ArmorAddons ?? empty) yield return record.ToLink();
        foreach (var record in mod.Armors ?? empty) yield return record.ToLink();
        foreach (var record in mod.ArtObjects ?? empty) yield return record.ToLink();
        foreach (var record in mod.AssociationTypes ?? empty) yield return record.ToLink();
        foreach (var record in mod.BodyParts ?? empty) yield return record.ToLink();
        foreach (var record in mod.Books ?? empty) yield return record.ToLink();
        foreach (var record in mod.CameraPaths ?? empty) yield return record.ToLink();
        foreach (var record in mod.CameraShots ?? empty) yield return record.ToLink();
        foreach (var record in mod.Classes ?? empty) yield return record.ToLink();
        foreach (var record in mod.Climates ?? empty) yield return record.ToLink();
        foreach (var record in mod.CollisionLayers ?? empty) yield return record.ToLink();
        foreach (var record in mod.Colors ?? empty) yield return record.ToLink();
        foreach (var record in mod.CombatStyles ?? empty) yield return record.ToLink();
        foreach (var record in mod.ConstructibleObjects ?? empty) yield return record.ToLink();
        foreach (var record in mod.Containers ?? empty) yield return record.ToLink();
        foreach (var record in mod.Debris ?? empty) yield return record.ToLink();
        foreach (var record in mod.DefaultObjectManagers ?? empty) yield return record.ToLink();
        foreach (var record in mod.DialogBranches ?? empty) yield return record.ToLink();
        foreach (var record in mod.DialogTopics ?? empty) yield return record.ToLink();
        foreach (var record in mod.DialogViews ?? empty) yield return record.ToLink();
        foreach (var record in mod.Doors ?? empty) yield return record.ToLink();
        foreach (var record in mod.DualCastData ?? empty) yield return record.ToLink();
        foreach (var record in mod.EffectShaders ?? empty) yield return record.ToLink();
        foreach (var record in mod.EncounterZones ?? empty) yield return record.ToLink();
        foreach (var record in mod.EquipTypes ?? empty) yield return record.ToLink();
        foreach (var record in mod.Explosions ?? empty) yield return record.ToLink();
        foreach (var record in mod.Eyes ?? empty) yield return record.ToLink();
        foreach (var record in mod.Factions ?? empty) yield return record.ToLink();
        foreach (var record in mod.Florae ?? empty) yield return record.ToLink();
        foreach (var record in mod.FootstepSets ?? empty) yield return record.ToLink();
        foreach (var record in mod.Footsteps ?? empty) yield return record.ToLink();
        foreach (var record in mod.FormLists ?? empty) yield return record.ToLink();
        foreach (var record in mod.Furniture ?? empty) yield return record.ToLink();
        foreach (var record in mod.GameSettings ?? empty) yield return record.ToLink();
        foreach (var record in mod.Globals ?? empty) yield return record.ToLink();
        foreach (var record in mod.Grasses ?? empty) yield return record.ToLink();
        foreach (var record in mod.Hairs ?? empty) yield return record.ToLink();
        foreach (var record in mod.Hazards ?? empty) yield return record.ToLink();
        foreach (var record in mod.HeadParts ?? empty) yield return record.ToLink();
        foreach (var record in mod.IdleAnimations ?? empty) yield return record.ToLink();
        foreach (var record in mod.IdleMarkers ?? empty) yield return record.ToLink();
        foreach (var record in mod.ImageSpaceAdapters ?? empty) yield return record.ToLink();
        foreach (var record in mod.ImageSpaces ?? empty) yield return record.ToLink();
        foreach (var record in mod.ImpactDataSets ?? empty) yield return record.ToLink();
        foreach (var record in mod.Impacts ?? empty) yield return record.ToLink();
        foreach (var record in mod.Ingestibles ?? empty) yield return record.ToLink();
        foreach (var record in mod.Ingredients ?? empty) yield return record.ToLink();
        foreach (var record in mod.Keys ?? empty) yield return record.ToLink();
        foreach (var record in mod.Keywords ?? empty) yield return record.ToLink();
        foreach (var record in mod.LandscapeTextures ?? empty) yield return record.ToLink();
        foreach (var record in mod.LensFlares ?? empty) yield return record.ToLink();
        foreach (var record in mod.LeveledItems ?? empty) yield return record.ToLink();
        foreach (var record in mod.LeveledNpcs ?? empty) yield return record.ToLink();
        foreach (var record in mod.LeveledSpells ?? empty) yield return record.ToLink();
        foreach (var record in mod.LightingTemplates ?? empty) yield return record.ToLink();
        foreach (var record in mod.Lights ?? empty) yield return record.ToLink();
        foreach (var record in mod.LoadScreens ?? empty) yield return record.ToLink();
        foreach (var record in mod.LocationReferenceTypes ?? empty) yield return record.ToLink();
        foreach (var record in mod.Locations ?? empty) yield return record.ToLink();
        foreach (var record in mod.MagicEffects ?? empty) yield return record.ToLink();
        foreach (var record in mod.MaterialObjects ?? empty) yield return record.ToLink();
        foreach (var record in mod.MaterialTypes ?? empty) yield return record.ToLink();
        foreach (var record in mod.Messages ?? empty) yield return record.ToLink();
        foreach (var record in mod.MiscItems ?? empty) yield return record.ToLink();
        foreach (var record in mod.MoveableStatics ?? empty) yield return record.ToLink();
        foreach (var record in mod.MovementTypes ?? empty) yield return record.ToLink();
        foreach (var record in mod.MusicTracks ?? empty) yield return record.ToLink();
        foreach (var record in mod.MusicTypes ?? empty) yield return record.ToLink();
        foreach (var record in mod.NavigationMeshInfoMaps ?? empty) yield return record.ToLink();
        foreach (var record in mod.Npcs ?? empty) yield return record.ToLink();
        foreach (var record in mod.ObjectEffects ?? empty) yield return record.ToLink();
        foreach (var record in mod.Outfits ?? empty) yield return record.ToLink();
        foreach (var record in mod.Packages ?? empty) yield return record.ToLink();
        foreach (var record in mod.Perks ?? empty) yield return record.ToLink();
        foreach (var record in mod.Projectiles ?? empty) yield return record.ToLink();
        foreach (var record in mod.Quests ?? empty) yield return record.ToLink();
        foreach (var record in mod.Races ?? empty) yield return record.ToLink();
        foreach (var record in mod.Regions ?? empty) yield return record.ToLink();
        foreach (var record in mod.Relationships ?? empty) yield return record.ToLink();
        foreach (var record in mod.ReverbParameters ?? empty) yield return record.ToLink();
        foreach (var record in mod.Scenes ?? empty) yield return record.ToLink();
        foreach (var record in mod.Scrolls ?? empty) yield return record.ToLink();
        foreach (var record in mod.ShaderParticleGeometries ?? empty) yield return record.ToLink();
        foreach (var record in mod.Shouts ?? empty) yield return record.ToLink();
        foreach (var record in mod.SoulGems ?? empty) yield return record.ToLink();
        foreach (var record in mod.SoundCategories ?? empty) yield return record.ToLink();
        foreach (var record in mod.SoundDescriptors ?? empty) yield return record.ToLink();
        foreach (var record in mod.SoundMarkers ?? empty) yield return record.ToLink();
        foreach (var record in mod.SoundOutputModels ?? empty) yield return record.ToLink();
        foreach (var record in mod.Spells ?? empty) yield return record.ToLink();
        foreach (var record in mod.Statics ?? empty) yield return record.ToLink();
        foreach (var record in mod.StoryManagerBranchNodes ?? empty) yield return record.ToLink();
        foreach (var record in mod.StoryManagerEventNodes ?? empty) yield return record.ToLink();
        foreach (var record in mod.StoryManagerQuestNodes ?? empty) yield return record.ToLink();
        foreach (var record in mod.TalkingActivators ?? empty) yield return record.ToLink();
        foreach (var record in mod.TextureSets ?? empty) yield return record.ToLink();
        foreach (var record in mod.Trees ?? empty) yield return record.ToLink();
        foreach (var record in mod.VisualEffects ?? empty) yield return record.ToLink();
        foreach (var record in mod.VoiceTypes ?? empty) yield return record.ToLink();
        foreach (var record in mod.VolumetricLightings ?? empty) yield return record.ToLink();
        foreach (var record in mod.Waters ?? empty) yield return record.ToLink();
        foreach (var record in mod.Weapons ?? empty) yield return record.ToLink();
        foreach (var record in mod.Weathers ?? empty) yield return record.ToLink();
        foreach (var record in mod.WordsOfPower ?? empty) yield return record.ToLink();
        foreach (var record in mod.Worldspaces ?? empty) yield return record.ToLink();
    }
    
    /// <summary>
    /// Lazily enumerates all major records in a mod, skipping any record types included in the provided HashSet.
    /// </summary>
    public static IEnumerable<IFormLinkGetter<IMajorRecordGetter>> LazyEnumerateMajorRecords(ISkyrimModGetter mod, HashSet<Type> typesToSkip)
    {
        // Use an empty enumerable if a property is null to prevent NullReferenceException
        var empty = Enumerable.Empty<IMajorRecordGetter>();
        
        if (!typesToSkip.Contains(typeof(IAcousticSpaceGetter))) foreach (var record in mod.AcousticSpaces ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IActionRecordGetter))) foreach (var record in mod.Actions ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IActivatorGetter))) foreach (var record in mod.Activators ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IActorValueInformationGetter))) foreach (var record in mod.ActorValueInformation ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IAddonNodeGetter))) foreach (var record in mod.AddonNodes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IAlchemicalApparatusGetter))) foreach (var record in mod.AlchemicalApparatuses ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IAmmunitionGetter))) foreach (var record in mod.Ammunitions ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IAnimatedObjectGetter))) foreach (var record in mod.AnimatedObjects ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IArmorAddonGetter))) foreach (var record in mod.ArmorAddons ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IArmorGetter))) foreach (var record in mod.Armors ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IArtObjectGetter))) foreach (var record in mod.ArtObjects ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IAssociationTypeGetter))) foreach (var record in mod.AssociationTypes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IBodyPartGetter))) foreach (var record in mod.BodyParts ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IBookGetter))) foreach (var record in mod.Books ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ICameraPathGetter))) foreach (var record in mod.CameraPaths ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ICameraShotGetter))) foreach (var record in mod.CameraShots ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IClassGetter))) foreach (var record in mod.Classes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IClimateGetter))) foreach (var record in mod.Climates ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ICollisionLayerGetter))) foreach (var record in mod.CollisionLayers ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IColorRecordGetter))) foreach (var record in mod.Colors ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ICombatStyleGetter))) foreach (var record in mod.CombatStyles ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IConstructibleObjectGetter))) foreach (var record in mod.ConstructibleObjects ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IContainerGetter))) foreach (var record in mod.Containers ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IDebrisGetter))) foreach (var record in mod.Debris ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IDefaultObjectManagerGetter))) foreach (var record in mod.DefaultObjectManagers ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IDialogBranchGetter))) foreach (var record in mod.DialogBranches ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IDialogTopicGetter))) foreach (var record in mod.DialogTopics ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IDialogViewGetter))) foreach (var record in mod.DialogViews ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IDoorGetter))) foreach (var record in mod.Doors ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IDualCastDataGetter))) foreach (var record in mod.DualCastData ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IEffectShaderGetter))) foreach (var record in mod.EffectShaders ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IEncounterZoneGetter))) foreach (var record in mod.EncounterZones ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IEquipTypeGetter))) foreach (var record in mod.EquipTypes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IExplosionGetter))) foreach (var record in mod.Explosions ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IEyesGetter))) foreach (var record in mod.Eyes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IFactionGetter))) foreach (var record in mod.Factions ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IFloraGetter))) foreach (var record in mod.Florae ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IFootstepSetGetter))) foreach (var record in mod.FootstepSets ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IFootstepGetter))) foreach (var record in mod.Footsteps ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IFormListGetter))) foreach (var record in mod.FormLists ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IFurnitureGetter))) foreach (var record in mod.Furniture ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IGameSettingGetter))) foreach (var record in mod.GameSettings ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IGlobalGetter))) foreach (var record in mod.Globals ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IGrassGetter))) foreach (var record in mod.Grasses ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IHairGetter))) foreach (var record in mod.Hairs ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IHazardGetter))) foreach (var record in mod.Hazards ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IHeadPartGetter))) foreach (var record in mod.HeadParts ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IIdleAnimationGetter))) foreach (var record in mod.IdleAnimations ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IIdleMarkerGetter))) foreach (var record in mod.IdleMarkers ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IImageSpaceAdapterGetter))) foreach (var record in mod.ImageSpaceAdapters ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IImageSpaceGetter))) foreach (var record in mod.ImageSpaces ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IImpactDataSetGetter))) foreach (var record in mod.ImpactDataSets ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IImpactGetter))) foreach (var record in mod.Impacts ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IIngestibleGetter))) foreach (var record in mod.Ingestibles ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IIngredientGetter))) foreach (var record in mod.Ingredients ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IKeyGetter))) foreach (var record in mod.Keys ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IKeywordGetter))) foreach (var record in mod.Keywords ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ILandscapeTextureGetter))) foreach (var record in mod.LandscapeTextures ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ILensFlareGetter))) foreach (var record in mod.LensFlares ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ILeveledItemGetter))) foreach (var record in mod.LeveledItems ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ILeveledNpcGetter))) foreach (var record in mod.LeveledNpcs ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ILeveledSpellGetter))) foreach (var record in mod.LeveledSpells ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ILightingTemplateGetter))) foreach (var record in mod.LightingTemplates ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ILightGetter))) foreach (var record in mod.Lights ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ILoadScreenGetter))) foreach (var record in mod.LoadScreens ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ILocationReferenceTypeGetter))) foreach (var record in mod.LocationReferenceTypes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ILocationGetter))) foreach (var record in mod.Locations ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IMagicEffectGetter))) foreach (var record in mod.MagicEffects ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IMaterialObjectGetter))) foreach (var record in mod.MaterialObjects ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IMaterialTypeGetter))) foreach (var record in mod.MaterialTypes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IMessageGetter))) foreach (var record in mod.Messages ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IMiscItemGetter))) foreach (var record in mod.MiscItems ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IMoveableStaticGetter))) foreach (var record in mod.MoveableStatics ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IMovementTypeGetter))) foreach (var record in mod.MovementTypes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IMusicTrackGetter))) foreach (var record in mod.MusicTracks ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IMusicTypeGetter))) foreach (var record in mod.MusicTypes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(INavigationMeshInfoMapGetter))) foreach (var record in mod.NavigationMeshInfoMaps ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(INpcGetter))) foreach (var record in mod.Npcs ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IObjectEffectGetter))) foreach (var record in mod.ObjectEffects ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IOutfitGetter))) foreach (var record in mod.Outfits ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IPackageGetter))) foreach (var record in mod.Packages ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IPerkGetter))) foreach (var record in mod.Perks ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IProjectileGetter))) foreach (var record in mod.Projectiles ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IQuestGetter))) foreach (var record in mod.Quests ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IRaceGetter))) foreach (var record in mod.Races ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IRegionGetter))) foreach (var record in mod.Regions ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IRelationshipGetter))) foreach (var record in mod.Relationships ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IReverbParametersGetter))) foreach (var record in mod.ReverbParameters ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ISceneGetter))) foreach (var record in mod.Scenes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IScrollGetter))) foreach (var record in mod.Scrolls ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IShaderParticleGeometryGetter))) foreach (var record in mod.ShaderParticleGeometries ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IShoutGetter))) foreach (var record in mod.Shouts ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ISoulGemGetter))) foreach (var record in mod.SoulGems ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ISoundCategoryGetter))) foreach (var record in mod.SoundCategories ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ISoundDescriptorGetter))) foreach (var record in mod.SoundDescriptors ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ISoundMarkerGetter))) foreach (var record in mod.SoundMarkers ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ISoundOutputModelGetter))) foreach (var record in mod.SoundOutputModels ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ISpellGetter))) foreach (var record in mod.Spells ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IStaticGetter))) foreach (var record in mod.Statics ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IStoryManagerBranchNodeGetter))) foreach (var record in mod.StoryManagerBranchNodes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IStoryManagerEventNodeGetter))) foreach (var record in mod.StoryManagerEventNodes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IStoryManagerQuestNodeGetter))) foreach (var record in mod.StoryManagerQuestNodes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ITalkingActivatorGetter))) foreach (var record in mod.TalkingActivators ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ITextureSetGetter))) foreach (var record in mod.TextureSets ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(ITreeGetter))) foreach (var record in mod.Trees ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IVisualEffectGetter))) foreach (var record in mod.VisualEffects ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IVoiceTypeGetter))) foreach (var record in mod.VoiceTypes ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IVolumetricLightingGetter))) foreach (var record in mod.VolumetricLightings ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IWaterGetter))) foreach (var record in mod.Waters ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IWeaponGetter))) foreach (var record in mod.Weapons ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IWeatherGetter))) foreach (var record in mod.Weathers ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IWordOfPowerGetter))) foreach (var record in mod.WordsOfPower ?? empty) yield return record.ToLink();
        if (!typesToSkip.Contains(typeof(IWorldspaceGetter))) foreach (var record in mod.Worldspaces ?? empty) yield return record.ToLink();
    }
    
    /// <summary>
    /// Removes or replaces characters that are invalid in file paths.
    /// </summary>
    /// <param name="path">The input string to sanitize.</param>
    /// <returns>A path string that is safe for use as a file name.</returns>
    public static string MakeStringPathSafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(path.Length);
        foreach (char c in path)
        {
            // Array.IndexOf is a simple way to check for existence
            if (Array.IndexOf(invalidChars, c) != -1)
            {
                sb.Append('_'); // Replace invalid char with an underscore
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
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
            TryRegularizePath(meshPath, out var regularizedMeshPath);
            meshPath = regularizedMeshPath;

            TryRegularizePath(texPath, out var regularizedTexPath);
            texPath = regularizedTexPath;
        }

        // Return lowercase paths for case-insensitive comparisons later
        return (meshPath.ToLowerInvariant(), texPath.ToLowerInvariant());
    }

    /// <summary>
    /// OPTIMIZED: This method no longer performs a slow linear search.
    /// It now uses the pre-built dictionary for an instantaneous lookup.
    /// </summary>
    public bool TryFormKeyToFormIDString(FormKey formKey, out string formIDstr)
    {
        formIDstr = string.Empty;
        if (_environmentStateProvider.TryGetPluginIndex(formKey.ModKey, out var prefix))
        {
            if (prefix.StartsWith("FE"))
            {
                // For ESLs, the local ID is the last 12 bits (3 hex characters).
                formIDstr = $"{prefix}{formKey.ID & 0xFFF:X3}";
            }
            else
            {
                // For regular plugins, the local ID is the last 24 bits (6 hex characters).
                formIDstr = prefix + formKey.IDString();
            }
            return true;
        }
        return false;
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
    /// <returns>
    /// True if the path was guaranteed to be regularized (e.g., a "data" prefix was removed
    /// or a type folder was added). Returns false otherwise.
    /// </returns>
    public static bool TryRegularizePath(string? inputPath, out string regularizedPath)
    {
        // A path will be returned in all cases, so initialize it to a known value.
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
            ".tri" => "meshes",
            _      => null
        };

        // Split into components.
        var segments = path
            .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // The return value will be true if we perform an action that guarantees regularization.
        var canGuaranteeRegularization = false;
        
        // Check if the path contains “…\data\…” as a prefix to be removed.
        // This is the first regularization check.
        var dataIdx = segments
            .FindIndex(s => s.Equals("data", StringComparison.OrdinalIgnoreCase));

        if (dataIdx >= 0 && dataIdx + 1 < segments.Count)
        {
            // If the path contains "...data\..." as a prefix, we can guarantee
            // that it has been regularized by removing that prefix.
            regularizedPath = string.Join("\\", segments.Skip(dataIdx + 1));
            canGuaranteeRegularization = true;
        }
        // If we couldn't remove the "data" prefix, then we check if the file extension
        // is a known type, which allows us to perform further regularization.
        else if (expectedType is not null)
        {
            // We can guarantee regularization because we know the type and can act on it.
            canGuaranteeRegularization = true;

            // Relative path already starts with a type folder?
            if (segments[0].Equals(expectedType, StringComparison.OrdinalIgnoreCase))
            {
                regularizedPath = string.Join("\\", segments);
            }
            else
            {
                // Bare “arbitrary\file.ext” – prepend inferred type.
                regularizedPath = $"{expectedType}\\{string.Join("\\", segments)}";
            }
        }
        // If the path did not contain a "data" prefix, and the file extension is
        // not one of the supported types, we return the input path as-is.
        // We cannot guarantee that it has been regularized.
        else
        {
            regularizedPath = path;
        }

        return canGuaranteeRegularization;
    }
    
    public static void OpenFolder(string folderPath)
    {
        if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(folderPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ScrollableMessageBox.ShowError($"Could not open folder '{folderPath}':\n{ex.Message}", "Error");
            }
        }
        else
        {
            ScrollableMessageBox.ShowWarning($"The folder path '{folderPath}' could not be found.", "Path Not Found");
        }
    }
    
    /// <summary>
    /// Opens the given URL in the default web browser.
    /// </summary>
    /// <remarks>
    /// Uses explorer.exe to open the URL rather than ShellExecuteEx directly.
    /// When launched from a standalone .exe (outside an IDE), ShellExecuteEx can cause
    /// Chromium-based browsers (Edge, Chrome) to crash immediately with STATUS_ACCESS_VIOLATION
    /// (0xC0000005) due to problematic handle/job-object inheritance from the parent process.
    /// Routing through explorer.exe avoids this because it launches the browser from its own
    /// clean process context.
    /// </remarks>
    /// <param name="url">The URL to open.</param>
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Debug.WriteLine("OpenUrl called with a null or empty URL.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{url}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error opening URL '{url}': {ex.Message}");
            throw;
        }
    }
    
    public static (string? gameName, string? modId) ParseMetaIni(string filePath)
    {
        string? gameName = null;
        string? modId = null;
        try
        {
            var lines = File.ReadAllLines(filePath);
            foreach (var line in lines)
            {
                if (line.StartsWith("gameName=", StringComparison.OrdinalIgnoreCase))
                {
                    gameName = line.Split('=').Last().Trim();
                    // Add special case for SkyrimSE
                    if (gameName.Equals("SkyrimSE", StringComparison.OrdinalIgnoreCase))
                    {
                        gameName = "skyrimspecialedition";
                    }
                }
                else if (line.StartsWith("modid=", StringComparison.OrdinalIgnoreCase))
                {
                    modId = line.Split('=').Last().Trim();
                }
                if (gameName != null && modId != null) break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error parsing {filePath}: {ex.Message}");
        }
        return (gameName, modId);
    }
    
    public static string? FindExistingCachedImage(string baseFilePath)
    {
        // Check for the most common formats in order of likelihood.
        var extensionsToTry = new[] { ".webp", ".png", ".jpg", ".jpeg" };
        foreach (var ext in extensionsToTry)
        {
            var fullPath = baseFilePath + ext;
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }
        return null;
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
            duplicateRecord = GetOrAddAsOverrideMixIns.GetOrAddAsOverride(group, recordGetter);
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


public enum Gender
{
    Female,
    Male
}