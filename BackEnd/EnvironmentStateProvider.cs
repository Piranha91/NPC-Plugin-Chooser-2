using System.Diagnostics;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Environments;
using Noggog;
using NPC_Plugin_Chooser_2.Models;
using System.IO;
using System.Reactive;
using System.Reactive.Subjects;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Allocators;
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using YamlDotNet.RepresentationModel;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class EnvironmentStateProvider : ReactiveObject
{
    // "Core" state properties and fields
    private IGameEnvironment<ISkyrimMod, ISkyrimModGetter> _environment;
    private static ILoadOrderGetter<IModListingGetter<ISkyrimModGetter>> _emptyLoadOrder =
        new LoadOrder<IModListingGetter<ISkyrimModGetter>>();
    public ILoadOrderGetter<IModListingGetter<ISkyrimModGetter>>? LoadOrder => _environment?.LoadOrder ?? _emptyLoadOrder;
    public IEnumerable<ModKey> LoadOrderModKeys => LoadOrder?.ListedOrder?.Select(m => m.ModKey) ?? new HashSet<ModKey>();
    public ILinkCache<ISkyrimMod, ISkyrimModGetter>? LinkCache => _environment?.LinkCache;
    public SkyrimRelease SkyrimVersion { get; private set; }
    public DirectoryPath ExtraSettingsDataPath { get; set; }
    public DirectoryPath InternalDataPath { get; set; }
    public DirectoryPath DataFolderPath { get; private set; }
    public ISkyrimMod OutputMod { get; set; }
    public TextFileFormKeyAllocator? CurrentAllocator { get; set; }
    public HashSet<ModKey> BaseGamePlugins => Implicits.Get(SkyrimVersion.ToGameRelease()).BaseMasters.ToHashSet();
    public HashSet<ModKey> CreationClubPlugins { get; set; } = new();
    public ModKey AbsoluteBasePlugin = ModKey.FromFileName("Skyrim.esm");
    
    public static string DefaultPluginName { get; } = "NPC";
    public string OutputPluginName { get; private set; }
    public string OutputPluginFileName => (OutputPluginName ?? DefaultPluginName) + ".esp";

    // Absolute path to the folder this app writes its output plugins into (when known). Used to
    // exclude the app's OWN output plugins from the resolved load order BEFORE Mutagen loads them,
    // so building the environment never memory-maps (and thus never locks) a previously generated
    // output plugin that a mod manager has enabled in the load order.
    public string? OutputModFolderPath { get; private set; }
    
    // Additional properties (for logging and diagnostics)
    [Reactive] public string CreationClubListingsFilePath { get; set; } = string.Empty;
    [Reactive] public string LoadOrderFilePath { get; set; } = string.Empty;
    [Reactive] public string EnvironmentBuilderError { get; set; }
    [Reactive] public int NumPlugins { get; set; } = 0;
    [Reactive] public int NumActivePlugins { get; set; } = 0;
    [Reactive] public EnvironmentStatus Status { get; private set; } = EnvironmentStatus.Invalid;

    // Diagnostics surfaced on the Settings → Environment Status panel so users can
    // tell where Mutagen located plugins.txt / Skyrim.ccc, whether the files exist,
    // and how many CC plugins (if any) were parsed vs actually present in the
    // resolved load order. Helpful for non-standard installs (renamed folders,
    // moved drives) where the default registry-based discovery falls through.
    [Reactive] public bool LoadOrderFileExists { get; private set; }
    [Reactive] public bool CreationClubListingsFileExists { get; private set; }
    [Reactive] public CreationClubListingsSourceKind CreationClubListingsSource { get; private set; } = CreationClubListingsSourceKind.NotFound;
    [Reactive] public int CreationClubPluginsCount { get; private set; }
    [Reactive] public int CreationClubPluginsInLoadOrderCount { get; private set; }

    public enum CreationClubListingsSourceKind
    {
        NotFound,
        Mutagen,
        Fallback
    }
    
    // Additional fields to help other classes
    private readonly Dictionary<ModKey, string> _modKeyFormIdPrefixCache = new();
    private SkyrimRelease _targetSkyrimRelease;
    private string _targetDataFolderPath;
    
    // 1. Create a private Subject to control the broadcast
    private readonly Subject<Unit> _environmentUpdatedSubject = new();

    // 2. Expose it publicly as an IObservable so others can subscribe but not broadcast
    public IObservable<Unit> OnEnvironmentUpdated => _environmentUpdatedSubject;

    public enum EnvironmentStatus
    {
        Valid,
        Invalid,
        Pending
    }

    public EnvironmentStateProvider(VM_SplashScreen? splashReporter = null)
    {
        string? exeLocation = null;
        var assembly = Assembly.GetEntryAssembly();
        if (assembly != null)
        {
            exeLocation = Path.GetDirectoryName(assembly.Location);
        }
        else
        {
            throw new Exception("Could not locate running assembly");
        }
        
        ExtraSettingsDataPath = Path.Combine(exeLocation, "Settings");
        InternalDataPath = Path.Combine(exeLocation, "InternalData");
    }

    public void SetEnvironmentTarget(SkyrimRelease skyrimRelease, string dataFolderPath, string outputPluginName, string? outputModFolderPath = null)
    {
        SkyrimVersion = skyrimRelease;
        _targetDataFolderPath = dataFolderPath;
        OutputPluginName = !string.IsNullOrWhiteSpace(outputPluginName) ? outputPluginName : DefaultPluginName;
        OutputModFolderPath = outputModFolderPath;
    }

    public void UpdateEnvironment()
    {
        EnvironmentBuilderError = string.Empty;
        Status = EnvironmentStatus.Pending;
        
        var builder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(SkyrimVersion.ToGameRelease());
        if (!_targetDataFolderPath.IsNullOrWhitespace() && Directory.Exists(_targetDataFolderPath))
        {
            builder = builder.WithTargetDataFolder(_targetDataFolderPath);
        }

        var validatedName = Path.GetFileNameWithoutExtension(OutputPluginName);
        
        OutputMod = null;
        OutputMod = new SkyrimMod(ModKey.FromName(validatedName, ModType.Plugin), SkyrimVersion);

        var built = false;

        try
        {
            string notificationStr = "";

            // Exclude THIS app's own output plugins (those it writes into OutputModFolderPath, which a
            // mod manager may have enabled in the load order) BEFORE Mutagen loads them. The stamp-based
            // TrimDependentPlugins below reads each plugin's header to identify our output — and that read
            // memory-maps the file, a map the environment then retains for its lifetime, locking the file
            // so the patcher can't overwrite it. Filtering here, at the pre-load listing stage (ModKey
            // only, no Mod materialization), means our output is never mapped and never locked.
            var ownOutputModKeys = GetOwnOutputModKeys();
            if (ownOutputModKeys.Count > 0)
            {
                StartupLogger.Log($"Excluding {ownOutputModKeys.Count} own output plugin(s) from the environment pre-load: {string.Join(", ", ownOutputModKeys.Select(k => k.FileName))}");
            }

            _environment = builder
                .TransformLoadOrderListings(listings =>
                    ownOutputModKeys.Count == 0
                        ? listings
                        : listings.Where(l => !ownOutputModKeys.Contains(l.ModKey)))
                .TransformModListings(x =>
                    x.OnlyEnabledAndExisting()
                        .TrimDependentPlugins(OutputMod.ModKey))
                    .WithOutputMod(OutputMod, OutputModTrimming.Self)
                .Build();

            if (!Directory.Exists(_environment.DataFolderPath))
            {
                Status = EnvironmentStatus.Invalid;
                return;
            }

            if (_environment.LoadOrder?.ListedOrder?.Count() == 0)
            {
                Status = EnvironmentStatus.Invalid;
                return;
            }

            if (!_environment.LoadOrder.ContainsKey(AbsoluteBasePlugin))
            {
                Status = EnvironmentStatus.Invalid;
                return;
            }
            
            if (!_environment.LoadOrderFilePath.Exists)
            {
                EnvironmentBuilderError =  "Load order file path at " + _environment.LoadOrderFilePath.Path + " does not exist"; // prevent successful initialization in the wrong mode.
                Status = EnvironmentStatus.Invalid;
                return;
            }
            
            LoadOrderFilePath = _environment.LoadOrderFilePath;
            LoadOrderFileExists = !string.IsNullOrEmpty(LoadOrderFilePath) && File.Exists(LoadOrderFilePath);
            DataFolderPath = _environment.DataFolderPath; // If a custom data folder path was provided it will not change. If no custom data folder path was provided, this will set it to the default path.

            ResolveCreationClubListingsPath();
            CreationClubPlugins = GetCreationClubPlugins();
            CreationClubPluginsCount = CreationClubPlugins.Count;
            var listedKeys = LoadOrder.ListedOrder.Select(p => p.ModKey).ToHashSet();
            CreationClubPluginsInLoadOrderCount = CreationClubPlugins.Count(listedKeys.Contains);

            ComputeFormIdPrefixes();

            Status = EnvironmentStatus.Valid;
            NumPlugins = LoadOrder.ListedOrder.Count();
            NumActivePlugins = LoadOrder.ListedOrder.Count(p => p.Enabled);

            StartupLogger.Log($"Environment resolved: DataFolder='{DataFolderPath}', LoadOrderFile='{LoadOrderFilePath}' (exists={LoadOrderFileExists}), CreationClubFile='{CreationClubListingsFilePath}' (exists={CreationClubListingsFileExists}, source={CreationClubListingsSource}), CC parsed={CreationClubPluginsCount}, CC in LoadOrder={CreationClubPluginsInLoadOrderCount}, NumPlugins={NumPlugins}, NumActive={NumActivePlugins}");
        }
        catch (Exception ex)
        {
            EnvironmentBuilderError = ExceptionLogger.GetExceptionStack(ex);
            Status = EnvironmentStatus.Invalid;
        }
        
        _environmentUpdatedSubject.OnNext(Unit.Default);
    }

    // ModKeys of the plugin files physically present in this app's own output folder (if known).
    // These are excluded from the resolved load order before Mutagen loads them (see UpdateEnvironment)
    // so the environment never memory-maps — and thus never locks — a previously generated output
    // plugin. Best-effort: returns an empty set if the folder is unset/missing or unreadable, in which
    // case only the stamp-based TrimDependentPlugins fallback applies.
    private HashSet<ModKey> GetOwnOutputModKeys()
    {
        var result = new HashSet<ModKey>();
        var dir = OutputModFolderPath;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return result;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".esp", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".esm", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".esl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var mk = ModKey.TryFromFileName(Path.GetFileName(file));
                if (mk != null) result.Add(mk.Value);
            }
        }
        catch
        {
            // Best-effort only; the stamp-based TrimDependentPlugins remains as a fallback.
        }

        return result;
    }

    // Mutagen's default Skyrim.ccc discovery uses registry-based game lookup, which
    // fails for non-standard installs (renamed folder, drive move). When that happens
    // _environment.CreationClubListingsFilePath is empty / missing, so the manual
    // parse below returns an empty set, the free CC plugins never appear as implicit
    // masters, and screening rejects mods that declare them as required. Fall back
    // to probing the data folder's parent for Skyrim.ccc before giving up.
    private void ResolveCreationClubListingsPath()
    {
        var mutagenPath = _environment.CreationClubListingsFilePath ?? string.Empty;
        if (!string.IsNullOrEmpty(mutagenPath) && File.Exists(mutagenPath))
        {
            CreationClubListingsFilePath = mutagenPath;
            CreationClubListingsFileExists = true;
            CreationClubListingsSource = CreationClubListingsSourceKind.Mutagen;
            return;
        }

        try
        {
            var parent = Directory.GetParent(_environment.DataFolderPath)?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                var fallback = Path.Combine(parent, "Skyrim.ccc");
                if (File.Exists(fallback))
                {
                    CreationClubListingsFilePath = fallback;
                    CreationClubListingsFileExists = true;
                    CreationClubListingsSource = CreationClubListingsSourceKind.Fallback;
                    return;
                }
            }
        }
        catch
        {
            // Fall through to NotFound; we don't want path-probing exceptions to
            // break environment initialization.
        }

        CreationClubListingsFilePath = mutagenPath;
        CreationClubListingsFileExists = false;
        CreationClubListingsSource = CreationClubListingsSourceKind.NotFound;
    }

    public HashSet<ModKey> GetCreationClubPlugins()
    {
        HashSet<ModKey> creationClubModKeys = new ();

        try // currently Implicits.Get doesn't seem to include creation club plugins
        {
            if (File.Exists(CreationClubListingsFilePath))
            {
                var ccListings = File.ReadAllText(CreationClubListingsFilePath);
                var ccPlugins = ccListings.Split(Environment.NewLine);
                foreach (var pluginName in ccPlugins)
                {
                    var plugin = ModKey.TryFromFileName(pluginName);
                    if (plugin != null && !creationClubModKeys.Contains(plugin.Value))
                    {
                        creationClubModKeys.Add(plugin.Value);
                    }
                }
            }
        }
        catch
        {
            return new HashSet<ModKey>();
        }
        
        return creationClubModKeys;
    }

    private void ComputeFormIdPrefixes()
    {
        var loadOrder = LoadOrder.ListedOrder.ToList();
        
        // This is the separate counter for full masters ONLY.
        int fullMasterIndex = 0; 
        int lightMasterIndex = 0;

        for (int i = 0; i < loadOrder.Count; i++)
        {
            var listing = loadOrder[i];
            if (listing.Mod != null && listing.Mod.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small))
            {
                // Handle ESLs using the light master counter.
                if (lightMasterIndex > 4095) // Max is FFF (4095)
                {
                    Debug.WriteLine($"WARNING: Load order exceeds the 4096 light master limit. Plugin '{listing.ModKey.FileName}' will not be correctly indexed.");
                    continue;
                }
                        
                // The prefix uses the current lightMasterIndex, formatted to 3 hex digits.
                _modKeyFormIdPrefixCache[listing.ModKey] = $"FE{lightMasterIndex:X3}";
                        
                // Increment ONLY the light master counter.
                lightMasterIndex++; 
            }
            else
            {
                // Handle full masters using the full master counter.
                if (fullMasterIndex > 253) // Max is FD (253)
                {
                    Debug.WriteLine($"WARNING: Load order exceeds the 254 full master limit. Plugin '{listing.ModKey.FileName}' and subsequent plugins will have invalid FormIDs.");
                    continue;
                }

                // The prefix is the fullMasterIndex formatted to 2 hex digits.
                _modKeyFormIdPrefixCache[listing.ModKey] = fullMasterIndex.ToString("X2");

                // Increment ONLY the full master counter.
                fullMasterIndex++; 
            }
        }
    }

    public bool TryGetPluginIndex(ModKey modKey, out string prefix)
    {
        if (_modKeyFormIdPrefixCache.TryGetValue(modKey, out prefix))
        {
            return true;
        }

        return false;
    }

    public string GetAllocatorPath()
    {
        string pluginName = Path.GetFileNameWithoutExtension(OutputPluginName);
        string allocatorName = "Allocator_" + pluginName;
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, allocatorName) + ".txt";
    }

    /// <summary>
    /// Builds a throwaway environment from the real on-disk load order WITHOUT trimming
    /// this app's generated output (and without overlaying a fresh in-memory output mod),
    /// so callers can inspect the true conflict-winning records the game actually sees —
    /// including a deployed output plugin and anything overriding it. The normal
    /// environment (<see cref="UpdateEnvironment"/>) deliberately trims the output via
    /// <c>TrimDependentPlugins</c>, which hides it from validation.
    ///
    /// The returned environment is the caller's to dispose. Returns null on failure with
    /// the reason in <paramref name="error"/>.
    /// </summary>
    public IGameEnvironment<ISkyrimMod, ISkyrimModGetter>? TryBuildUntrimmedEnvironment(out string? error)
    {
        error = null;
        try
        {
            var builder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(SkyrimVersion.ToGameRelease());
            if (!_targetDataFolderPath.IsNullOrWhitespace() && Directory.Exists(_targetDataFolderPath))
            {
                builder = builder.WithTargetDataFolder(_targetDataFolderPath);
            }

            // Only OnlyEnabledAndExisting() — the set the game actually loads. No
            // TrimDependentPlugins, no WithOutputMod: the deployed output plugin and any
            // overrides of it stay in the link cache so winning records are the real ones.
            var env = builder
                .TransformModListings(x => x.OnlyEnabledAndExisting())
                .Build();

            if (!Directory.Exists(env.DataFolderPath) ||
                env.LoadOrder?.ListedOrder == null ||
                !env.LoadOrder.ListedOrder.Any())
            {
                error = "Untrimmed environment built with no usable load order or data folder.";
                env.Dispose();
                return null;
            }

            return env;
        }
        catch (Exception ex)
        {
            error = ExceptionLogger.GetExceptionStack(ex);
            return null;
        }
    }
}