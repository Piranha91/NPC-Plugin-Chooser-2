using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
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
    
    // Session-scoped cache: true = chain terminates in a Leveled NPC, false = chain is valid.
    // Keyed by the NPC FormKey whose template link was (or would be) followed.
    private readonly ConcurrentDictionary<FormKey, bool> _leveledNpcChainCache = new();

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
        _leveledNpcChainCache.Clear();
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
    /// Only applies the fix if the input doesn't already contain valid non-Latin
    /// script and the round-trip produces a clearly improved result.
    ///
    /// We intentionally do NOT pre-filter with pattern detection. Windows-1252
    /// maps bytes 0x80-0x9F to scattered Unicode codepoints (€, ‚, ƒ, „, …),
    /// making reliable mojibake heuristics impractical. Instead we always attempt
    /// the round-trip and validate the result.
    /// </summary>
    public static string FixMojibake(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        try
        {
            // Already contains CJK / Cyrillic / Hangul / etc. → decoded correctly
            if (ContainsNonLatinScript(input))
                return input;

            // Attempt Windows-1252 → bytes → UTF-8 round-trip
            byte[] rawBytes = _windows1252.Value.GetBytes(input);
            string candidate = Encoding.UTF8.GetString(rawBytes);

            // U+FFFD means the bytes weren't valid UTF-8 — not mojibake
            if (candidate.Contains('\uFFFD'))
                return input;

            // Accept only if the conversion actually changed something AND
            // the result contains meaningful non-Latin text or is at least
            // reasonable (> 50% printable, non-replacement characters)
            if (candidate != input &&
                (ContainsNonLatinScript(candidate) || IsReasonableText(candidate)))
            {
                return candidate;
            }

            return input;
        }
        catch
        {
            return input;
        }
    }

    /// <summary>
    /// Returns true if the string contains characters from non-Latin scripts,
    /// indicating it was already decoded correctly.
    /// </summary>
    private static bool ContainsNonLatinScript(string s)
    {
        foreach (char c in s)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) return true;  // CJK Unified Ideographs
            if (c >= 0x3400 && c <= 0x4DBF) return true;  // CJK Extension A
            if (c >= 0x3040 && c <= 0x309F) return true;  // Hiragana
            if (c >= 0x30A0 && c <= 0x30FF) return true;  // Katakana
            if (c >= 0xAC00 && c <= 0xD7AF) return true;  // Hangul Syllables
            if (c >= 0x0400 && c <= 0x04FF) return true;  // Cyrillic
            if (c >= 0x0600 && c <= 0x06FF) return true;  // Arabic
            if (c >= 0x0590 && c <= 0x05FF) return true;  // Hebrew
            if (c >= 0x0E00 && c <= 0x0E7F) return true;  // Thai
            if (c >= 0x0900 && c <= 0x097F) return true;  // Devanagari
            if (c >= 0xF900 && c <= 0xFAFF) return true;  // CJK Compat. Ideographs
            if (c >= 0xFF65 && c <= 0xFFDC) return true;  // Halfwidth Katakana/CJK
            if (c >= 0x3000 && c <= 0x303F) return true;  // CJK Symbols & Punctuation
        }
        return false;
    }

    /// <summary>
    /// Fallback: accepts the conversion if more than half the characters are
    /// printable non-replacement content. Catches cases where the converted
    /// text is improved but uses scripts not explicitly listed in
    /// ContainsNonLatinScript (e.g. Georgian, Tibetan, Ethiopic).
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
    
    /// <summary>
    /// Returns a directory path safe to assign to a WPF file dialog's InitialDirectory.
    /// The Vista-style common item dialog throws E_INVALIDARG ("Value does not fall within
    /// the expected range") when InitialDirectory does not resolve to an existing folder,
    /// so this returns the first path that exists (preferred -> fallback -> MyDocuments).
    /// </summary>
    public static string GetSafeInitialDirectory(string? preferredPath, string? fallbackPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && Directory.Exists(preferredPath))
        {
            return preferredPath;
        }

        if (!string.IsNullOrWhiteSpace(fallbackPath) && Directory.Exists(fallbackPath))
        {
            return fallbackPath;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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

    public bool IsValidAppearanceRace(FormKey raceFormKey, INpcGetter npcGetter, Language? language, out string rejectionMessage, out IRaceGetter? resolvedRace, IRaceGetter? sourcePluginRace = null)
    {
        bool isCached = false;
        bool isValid = true;
        rejectionMessage = "";
        resolvedRace = null;
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
            isTemplate = IsValidTemplatedNpc(npcGetter);
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
                    resolvedRace = raceGetter;
                }
                else if (raceFormKey.IsNull)
                {
                    raceEvaluation = RaceEvaluation.InvalidNull;
                }
                else if (!_environmentStateProvider.LinkCache.TryResolve<IRaceGetter>(raceFormKey,
                             out raceGetter) || raceGetter is null)
                {
                    raceEvaluation = RaceEvaluation.InvalidNotInLoadOrder;
                    resolvedRace = raceGetter;
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
                    else if (!raceGetter.Keywords.Contains(Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim
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
                // Bethesda assigned FoxRace to some templated human NPCs; allow those through
                if (isTemplate && raceFormKey == Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Race.FoxRace.FormKey)
                {
                    return true;
                }
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

    /// <summary>
    /// Builds the textual content of a spawn-batch file (the in-game console ".bat" / "sel.txt" used to
    /// place the patched NPCs for inspection): optional pre-commands, then one
    /// <c>player.placeatme &lt;FormID&gt;</c> per resolvable NPC (in the given order), then optional
    /// post-commands. Pure - no file IO or dialog - so the group-&gt;spawn-batch flow is unit-testable;
    /// <see cref="NPC_Plugin_Chooser_2.View_Models.VM_Run.GenerateSpawnBatFileAsync"/> wraps it with the
    /// save dialog and disk write. FormKeys that cannot be resolved to a FormID are skipped and reported
    /// via <paramref name="unresolved"/>.
    /// </summary>
    public string BuildSpawnBatchContent(IEnumerable<FormKey> npcFormKeys, string? preCommands,
        string? postCommands, out int successCount, out List<FormKey> unresolved)
    {
        successCount = 0;
        unresolved = new List<FormKey>();
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(preCommands))
        {
            sb.AppendLine(preCommands);
        }

        foreach (var npcFormKey in npcFormKeys)
        {
            string formId = FormKeyToFormIDString(npcFormKey);
            if (!string.IsNullOrEmpty(formId))
            {
                sb.AppendLine($"player.placeatme {formId}");
                successCount++;
            }
            else
            {
                unresolved.Add(npcFormKey);
            }
        }

        if (!string.IsNullOrWhiteSpace(postCommands))
        {
            sb.AppendLine(postCommands);
        }

        return sb.ToString();
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

    /// <summary>
    /// Parses a Race filter term. A trailing '~' means "exact match" (whole-string),
    /// e.g. "NordRace~" matches only "NordRace", not "NordRaceVampire". Returns the
    /// bare term (terminator and surrounding whitespace stripped) plus whether exact
    /// matching was requested.
    /// </summary>
    public static (string Term, bool Exact) ParseRaceSearchTerm(string? searchText)
    {
        var trimmed = (searchText ?? string.Empty).Trim();
        bool exact = trimmed.EndsWith("~", StringComparison.Ordinal);
        if (exact) trimmed = trimmed.TrimEnd('~').Trim();
        return (trimmed, exact);
    }

    /// <summary>
    /// Formats a race's Name + EditorID as a single "Name (EditorID)" label for the Race
    /// filter combo — matching what <see cref="BuildRaceFilterOptions"/> lists. Falls back
    /// to whichever part exists, and collapses "X (X)" to "X" when Name and EditorID are
    /// the same. Returns null when both are blank.
    /// </summary>
    public static string? CombineRaceLabel(string? name, string? editorId)
    {
        name = name?.Trim();
        editorId = editorId?.Trim();
        bool hasName = !string.IsNullOrEmpty(name);
        bool hasId = !string.IsNullOrEmpty(editorId);
        if (hasName && hasId)
            return string.Equals(name, editorId, StringComparison.OrdinalIgnoreCase) ? name : $"{name} ({editorId})";
        if (hasId) return editorId;
        if (hasName) return name;
        return null;
    }

    /// <summary>
    /// Matches a resolved race against a Race filter term. The term is tested against the
    /// race's Name, its EditorID, and the combined "Name (EditorID)" label — so typing a
    /// raw Name/EditorID and picking a combined dropdown entry both work. Partial
    /// (case-insensitive Contains) unless <paramref name="exact"/>, in which case one of
    /// those three must equal the term exactly (case-insensitive).
    /// </summary>
    public static bool RaceMatches(string? raceName, string? raceEditorId, string term, bool exact)
    {
        if (string.IsNullOrEmpty(term)) return false;
        var combined = CombineRaceLabel(raceName, raceEditorId);
        if (exact)
        {
            return string.Equals(raceEditorId, term, StringComparison.OrdinalIgnoreCase)
                || string.Equals(raceName, term, StringComparison.OrdinalIgnoreCase)
                || string.Equals(combined, term, StringComparison.OrdinalIgnoreCase);
        }
        return (raceEditorId?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || (raceName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || (combined?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// Builds the sorted, distinct list of "Name (EditorID)" labels that populates the
    /// Race filter's editable combo (one entry per race). Blank pairs are dropped and
    /// duplicates are collapsed case-insensitively.
    /// </summary>
    public static List<string> BuildRaceFilterOptions(IEnumerable<(string? Name, string? EditorId)> races)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, editorId) in races)
        {
            var label = CombineRaceLabel(name, editorId);
            if (!string.IsNullOrWhiteSpace(label)) set.Add(label);
        }
        return set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Sortable key over NPC-record fields that predict which renderer assets
    /// will overlap between adjacent renders (skeleton/body/skin/head). Sorting
    /// a list of NPCs by this key — without parsing any NIF — clusters NPCs
    /// that share the same race/skin/worn-armor/head-parts/hair so the renderer
    /// reuses meshes and textures across consecutive frames. Used by the
    /// "Generate All Mugshots" batch flow.
    /// </summary>
    public readonly record struct NpcGroupingKey(
        bool IsFemale,
        string Race,
        string WornArmor,
        string HeadPartsHash,
        string HairColor) : IComparable<NpcGroupingKey>
    {
        public int CompareTo(NpcGroupingKey other)
        {
            int c = IsFemale.CompareTo(other.IsFemale);
            if (c != 0) return c;
            c = string.CompareOrdinal(Race, other.Race);
            if (c != 0) return c;
            c = string.CompareOrdinal(WornArmor, other.WornArmor);
            if (c != 0) return c;
            c = string.CompareOrdinal(HeadPartsHash, other.HeadPartsHash);
            if (c != 0) return c;
            return string.CompareOrdinal(HairColor, other.HairColor);
        }
    }

    public static NpcGroupingKey BuildNpcGroupingKey(INpcGetter npc)
    {
        string race = npc.Race.IsNull ? string.Empty : npc.Race.FormKey.ToString();
        string wornArmor = npc.WornArmor.IsNull ? string.Empty : npc.WornArmor.FormKey.ToString();
        string hairColor = npc.HairColor.IsNull ? string.Empty : npc.HairColor.FormKey.ToString();

        string headPartsHash;
        if (npc.HeadParts == null || npc.HeadParts.Count == 0)
        {
            headPartsHash = string.Empty;
        }
        else
        {
            // Sort so re-orderings of the same set produce the same key. Join
            // on a separator that can't appear inside a FormKey string so the
            // hash is collision-free over the input set.
            var keys = new List<string>(npc.HeadParts.Count);
            foreach (var link in npc.HeadParts)
            {
                if (!link.IsNull) keys.Add(link.FormKey.ToString());
            }
            keys.Sort(StringComparer.Ordinal);
            headPartsHash = string.Join("|", keys);
        }

        return new NpcGroupingKey(IsFemale(npc), race, wornArmor, headPartsHash, hairColor);
    }

    public static bool HasTraitsFlag(INpcGetter npc)
    {
        return npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits);
    }

    public static bool IsValidTemplatedNpc(INpcGetter? npc)
    {
        return npc != null &&
               HasTraitsFlag(npc) && 
               npc.Template != null && 
               !npc.Template.IsNull;
    }

    /// <summary>
    /// Walks the template chain starting from the given NPC and returns true if the chain
    /// terminates in a Leveled NPC (LVLN record). NPCs whose template chain ends in a
    /// Leveled NPC cannot have a unique appearance selected for them.
    /// 
    /// Results are cached per-session so that overlapping chains (e.g. A→B→C→LVLN then
    /// D→B→…) short-circuit as soon as they hit an already-evaluated FormKey.
    /// 
    /// Resolution order for each link in the chain:
    ///   1. Search the provided mod plugins (if any).
    ///   2. Fall back to the environment link cache.
    /// </summary>
    public bool TemplateChainTerminatesInLeveledNpc(
        INpcGetter npcGetter,
        IEnumerable<ISkyrimModGetter>? modPlugins = null,
        int maxDepth = 50)
    {
        if (!IsValidTemplatedNpc(npcGetter))
        {
            return false; // Not templated at all — nothing to check
        }

        // Check if this exact NPC was already evaluated
        if (_leveledNpcChainCache.TryGetValue(npcGetter.FormKey, out var cachedResult))
        {
            return cachedResult;
        }

        // Collect every NPC FormKey we visit so we can backfill the cache afterwards
        var visitedNpcFormKeys = new List<FormKey> { npcGetter.FormKey };
        var visitedSet = new HashSet<FormKey>();
        var templateFormKey = npcGetter.Template.FormKey;
        var pluginList = modPlugins?.ToList(); // avoid multiple enumeration
        bool result = false; // assume valid until proven otherwise

        for (int depth = 0; depth < maxDepth; depth++)
        {
            if (templateFormKey.IsNull || !visitedSet.Add(templateFormKey))
            {
                break; // null link or cycle detected
            }

            // --- Check the cache for this template FormKey ---
            if (_leveledNpcChainCache.TryGetValue(templateFormKey, out var cached))
            {
                result = cached;
                break; // propagate the cached answer to everything upstream
            }

            // --- Try to resolve as a Leveled NPC first (cheapest decisive check) ---
            bool isLeveled = false;

            // Check plugins
            if (!isLeveled && pluginList != null)
            {
                foreach (var plugin in pluginList)
                {
                    if (plugin.LeveledNpcs.FirstOrDefault(l => l.FormKey == templateFormKey) != null)
                    {
                        isLeveled = true;
                        break;
                    }
                }
            }

            // Check link cache
            if (!isLeveled)
            {
                isLeveled = _environmentStateProvider.LinkCache
                    .TryResolve<ILeveledNpcGetter>(templateFormKey, out _);
            }

            if (isLeveled)
            {
                result = true;
                break;
            }

            // --- Not a Leveled NPC — try to resolve as a regular NPC and continue walking ---
            INpcGetter? nextNpc = null;

            // Check plugins first
            if (pluginList != null)
            {
                foreach (var plugin in pluginList)
                {
                    nextNpc = plugin.Npcs.FirstOrDefault(n => n.FormKey == templateFormKey);
                    if (nextNpc != null) break;
                }
            }

            // Fall back to link cache
            if (nextNpc == null)
            {
                _environmentStateProvider.LinkCache.TryResolve<INpcGetter>(templateFormKey, out nextNpc);
            }

            if (nextNpc == null)
            {
                break; // can't resolve further — assume valid
            }

            // Track this intermediate NPC so it gets cached too
            visitedNpcFormKeys.Add(nextNpc.FormKey);

            if (!IsValidTemplatedNpc(nextNpc))
            {
                break; // chain ends at a non-templated NPC — valid
            }

            templateFormKey = nextNpc.Template.FormKey;
        }

        // --- Backfill the cache for every NPC FormKey we visited in this chain ---
        foreach (var formKey in visitedNpcFormKeys)
        {
            _leveledNpcChainCache.TryAdd(formKey, result);
        }

        return result;
    }
    
    /// <summary>
    /// The top-level record groups covered by <see cref="LazyEnumerateMajorRecords(ISkyrimModGetter)"/>: each
    /// group's getter interface (the key matched against typesToSkip / <see cref="AppearanceRecordTypes"/>)
    /// paired with its accessor on the mod.
    /// </summary>
    private static readonly (Type GetterType, Func<ISkyrimModGetter, IGroupGetter?> GetGroup)[] TopLevelRecordGroups =
    {
        (typeof(IAcousticSpaceGetter), static m => m.AcousticSpaces),
        (typeof(IActionRecordGetter), static m => m.Actions),
        (typeof(IActivatorGetter), static m => m.Activators),
        (typeof(IActorValueInformationGetter), static m => m.ActorValueInformation),
        (typeof(IAddonNodeGetter), static m => m.AddonNodes),
        (typeof(IAlchemicalApparatusGetter), static m => m.AlchemicalApparatuses),
        (typeof(IAmmunitionGetter), static m => m.Ammunitions),
        (typeof(IAnimatedObjectGetter), static m => m.AnimatedObjects),
        (typeof(IArmorAddonGetter), static m => m.ArmorAddons),
        (typeof(IArmorGetter), static m => m.Armors),
        (typeof(IArtObjectGetter), static m => m.ArtObjects),
        (typeof(IAssociationTypeGetter), static m => m.AssociationTypes),
        (typeof(IBodyPartGetter), static m => m.BodyParts),
        (typeof(IBookGetter), static m => m.Books),
        (typeof(ICameraPathGetter), static m => m.CameraPaths),
        (typeof(ICameraShotGetter), static m => m.CameraShots),
        (typeof(IClassGetter), static m => m.Classes),
        (typeof(IClimateGetter), static m => m.Climates),
        (typeof(ICollisionLayerGetter), static m => m.CollisionLayers),
        (typeof(IColorRecordGetter), static m => m.Colors),
        (typeof(ICombatStyleGetter), static m => m.CombatStyles),
        (typeof(IConstructibleObjectGetter), static m => m.ConstructibleObjects),
        (typeof(IContainerGetter), static m => m.Containers),
        (typeof(IDebrisGetter), static m => m.Debris),
        (typeof(IDefaultObjectManagerGetter), static m => m.DefaultObjectManagers),
        (typeof(IDialogBranchGetter), static m => m.DialogBranches),
        (typeof(IDialogTopicGetter), static m => m.DialogTopics),
        (typeof(IDialogViewGetter), static m => m.DialogViews),
        (typeof(IDoorGetter), static m => m.Doors),
        (typeof(IDualCastDataGetter), static m => m.DualCastData),
        (typeof(IEffectShaderGetter), static m => m.EffectShaders),
        (typeof(IEncounterZoneGetter), static m => m.EncounterZones),
        (typeof(IEquipTypeGetter), static m => m.EquipTypes),
        (typeof(IExplosionGetter), static m => m.Explosions),
        (typeof(IEyesGetter), static m => m.Eyes),
        (typeof(IFactionGetter), static m => m.Factions),
        (typeof(IFloraGetter), static m => m.Florae),
        (typeof(IFootstepSetGetter), static m => m.FootstepSets),
        (typeof(IFootstepGetter), static m => m.Footsteps),
        (typeof(IFormListGetter), static m => m.FormLists),
        (typeof(IFurnitureGetter), static m => m.Furniture),
        (typeof(IGameSettingGetter), static m => m.GameSettings),
        (typeof(IGlobalGetter), static m => m.Globals),
        (typeof(IGrassGetter), static m => m.Grasses),
        (typeof(IHairGetter), static m => m.Hairs),
        (typeof(IHazardGetter), static m => m.Hazards),
        (typeof(IHeadPartGetter), static m => m.HeadParts),
        (typeof(IIdleAnimationGetter), static m => m.IdleAnimations),
        (typeof(IIdleMarkerGetter), static m => m.IdleMarkers),
        (typeof(IImageSpaceAdapterGetter), static m => m.ImageSpaceAdapters),
        (typeof(IImageSpaceGetter), static m => m.ImageSpaces),
        (typeof(IImpactDataSetGetter), static m => m.ImpactDataSets),
        (typeof(IImpactGetter), static m => m.Impacts),
        (typeof(IIngestibleGetter), static m => m.Ingestibles),
        (typeof(IIngredientGetter), static m => m.Ingredients),
        (typeof(IKeyGetter), static m => m.Keys),
        (typeof(IKeywordGetter), static m => m.Keywords),
        (typeof(ILandscapeTextureGetter), static m => m.LandscapeTextures),
        (typeof(ILensFlareGetter), static m => m.LensFlares),
        (typeof(ILeveledItemGetter), static m => m.LeveledItems),
        (typeof(ILeveledNpcGetter), static m => m.LeveledNpcs),
        (typeof(ILeveledSpellGetter), static m => m.LeveledSpells),
        (typeof(ILightingTemplateGetter), static m => m.LightingTemplates),
        (typeof(ILightGetter), static m => m.Lights),
        (typeof(ILoadScreenGetter), static m => m.LoadScreens),
        (typeof(ILocationReferenceTypeGetter), static m => m.LocationReferenceTypes),
        (typeof(ILocationGetter), static m => m.Locations),
        (typeof(IMagicEffectGetter), static m => m.MagicEffects),
        (typeof(IMaterialObjectGetter), static m => m.MaterialObjects),
        (typeof(IMaterialTypeGetter), static m => m.MaterialTypes),
        (typeof(IMessageGetter), static m => m.Messages),
        (typeof(IMiscItemGetter), static m => m.MiscItems),
        (typeof(IMoveableStaticGetter), static m => m.MoveableStatics),
        (typeof(IMovementTypeGetter), static m => m.MovementTypes),
        (typeof(IMusicTrackGetter), static m => m.MusicTracks),
        (typeof(IMusicTypeGetter), static m => m.MusicTypes),
        (typeof(INavigationMeshInfoMapGetter), static m => m.NavigationMeshInfoMaps),
        (typeof(INpcGetter), static m => m.Npcs),
        (typeof(IObjectEffectGetter), static m => m.ObjectEffects),
        (typeof(IOutfitGetter), static m => m.Outfits),
        (typeof(IPackageGetter), static m => m.Packages),
        (typeof(IPerkGetter), static m => m.Perks),
        (typeof(IProjectileGetter), static m => m.Projectiles),
        (typeof(IQuestGetter), static m => m.Quests),
        (typeof(IRaceGetter), static m => m.Races),
        (typeof(IRegionGetter), static m => m.Regions),
        (typeof(IRelationshipGetter), static m => m.Relationships),
        (typeof(IReverbParametersGetter), static m => m.ReverbParameters),
        (typeof(ISceneGetter), static m => m.Scenes),
        (typeof(IScrollGetter), static m => m.Scrolls),
        (typeof(IShaderParticleGeometryGetter), static m => m.ShaderParticleGeometries),
        (typeof(IShoutGetter), static m => m.Shouts),
        (typeof(ISoulGemGetter), static m => m.SoulGems),
        (typeof(ISoundCategoryGetter), static m => m.SoundCategories),
        (typeof(ISoundDescriptorGetter), static m => m.SoundDescriptors),
        (typeof(ISoundMarkerGetter), static m => m.SoundMarkers),
        (typeof(ISoundOutputModelGetter), static m => m.SoundOutputModels),
        (typeof(ISpellGetter), static m => m.Spells),
        (typeof(IStaticGetter), static m => m.Statics),
        (typeof(IStoryManagerBranchNodeGetter), static m => m.StoryManagerBranchNodes),
        (typeof(IStoryManagerEventNodeGetter), static m => m.StoryManagerEventNodes),
        (typeof(IStoryManagerQuestNodeGetter), static m => m.StoryManagerQuestNodes),
        (typeof(ITalkingActivatorGetter), static m => m.TalkingActivators),
        (typeof(ITextureSetGetter), static m => m.TextureSets),
        (typeof(ITreeGetter), static m => m.Trees),
        (typeof(IVisualEffectGetter), static m => m.VisualEffects),
        (typeof(IVoiceTypeGetter), static m => m.VoiceTypes),
        (typeof(IVolumetricLightingGetter), static m => m.VolumetricLightings),
        (typeof(IWaterGetter), static m => m.Waters),
        (typeof(IWeaponGetter), static m => m.Weapons),
        (typeof(IWeatherGetter), static m => m.Weathers),
        (typeof(IWordOfPowerGetter), static m => m.WordsOfPower),
        (typeof(IWorldspaceGetter), static m => m.Worldspaces),
    };

    private static readonly HashSet<Type> NoSkippedTypes = new();

    /// <summary>
    /// Lazily enumerates the identities (FormKey + record type) of all top-level major records in a mod.
    /// Walks each group's FormKey cache (built from the record headers alone) instead of constructing the
    /// records themselves, so a record whose subrecord data Mutagen rejects as malformed (e.g. a FootstepSet
    /// whose DATA counts disagree with its lists) is still enumerated instead of aborting the whole scan.
    /// The returned links carry only record identity; callers that need record CONTENTS must resolve them
    /// separately and handle parse failures. Processing stops as soon as the consuming loop breaks.
    /// </summary>
    public static IEnumerable<IFormLinkGetter> LazyEnumerateMajorRecords(ISkyrimModGetter mod)
    {
        return LazyEnumerateMajorRecords(mod, NoSkippedTypes);
    }

    /// <summary>
    /// Same as <see cref="LazyEnumerateMajorRecords(ISkyrimModGetter)"/>, skipping any record groups whose
    /// getter type is included in the provided HashSet.
    /// </summary>
    public static IEnumerable<IFormLinkGetter> LazyEnumerateMajorRecords(ISkyrimModGetter mod, HashSet<Type> typesToSkip)
    {
        foreach (var (getterType, getGroup) in TopLevelRecordGroups)
        {
            if (typesToSkip.Contains(getterType)) continue;

            var group = getGroup(mod);
            if (group == null) continue;

            foreach (var formKey in group.FormKeys)
            {
                yield return new FormLinkInformation(formKey, getterType);
            }
        }
    }

    /// <summary>
    /// The appearance-related getter interface types (the NPC group plus its visual-support
    /// groups). <see cref="MergeInClassifier"/> treats records OUTSIDE these groups as "hard"
    /// records when classifying a mod as appearance replacer vs base mod; keep the two in sync.
    /// </summary>
    public static readonly HashSet<Type> AppearanceRecordTypes = new()
    {
        typeof(INpcGetter),
        typeof(IArmorGetter),
        typeof(IArmorAddonGetter),
        typeof(ITextureSetGetter),
        typeof(IHeadPartGetter),
        typeof(IHairGetter),
        typeof(IColorRecordGetter),
        typeof(IEyesGetter)
    };

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
    /// True when a data-relative path lies under one of the FaceGen output trees
    /// (meshes\actors\character\facegendata\..., textures\actors\character\facegendata\...).
    /// FaceGen files are inherently per-NPC and must be written at their vanilla-derived
    /// paths, so base-game-overwrite protection never applies to them. Accepts either slash
    /// style and an optional leading separator; comparison is case-insensitive.
    /// </summary>
    public static bool IsFaceGenPath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;
        string normalized = relativePath.Replace('/', '\\').TrimStart('\\');
        return normalized.StartsWith(@"meshes\actors\character\facegendata\", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith(@"textures\actors\character\facegendata\", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Canonicalises a folder path for case-insensitive root comparison:
    /// resolves to a full path and strips trailing separators. Returns an empty
    /// string for null/whitespace input or paths that Path.GetFullPath rejects.</summary>
    public static string NormalizeFolderForCompare(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    /// <summary>True when <paramref name="normalizedCandidate"/> equals
    /// <paramref name="normalizedRoot"/> or is a descendant of it (separator-aware,
    /// case-insensitive). Both arguments must have been produced by
    /// <see cref="NormalizeFolderForCompare"/>.</summary>
    public static bool IsUnderRoot(string normalizedCandidate, string normalizedRoot)
    {
        if (string.IsNullOrEmpty(normalizedRoot)) return false;
        if (normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
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