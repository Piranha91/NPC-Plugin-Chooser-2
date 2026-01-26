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
    
    // Additional properties (for logging only)
    public string CreationClubListingsFilePath { get; set; }
    public string LoadOrderFilePath { get; set; }
    [Reactive] public string EnvironmentBuilderError { get; set; }
    [Reactive] public int NumPlugins { get; set; } = 0;
    [Reactive] public int NumActivePlugins { get; set; } = 0;
    [Reactive] public EnvironmentStatus Status { get; private set; } = EnvironmentStatus.Invalid;
    
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

    public void SetEnvironmentTarget(SkyrimRelease skyrimRelease, string dataFolderPath, string outputPluginName)
    {
        SkyrimVersion = skyrimRelease;
        _targetDataFolderPath = dataFolderPath;
        OutputPluginName = !string.IsNullOrWhiteSpace(outputPluginName) ? outputPluginName : DefaultPluginName;
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
            _environment = builder
                .TransformModListings(x =>
                    x.OnlyEnabledAndExisting()
                        .TrimDependentPlugins())
                    .WithOutputMod(OutputMod)
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
            
            CreationClubListingsFilePath = _environment.CreationClubListingsFilePath ?? string.Empty;
            LoadOrderFilePath = _environment.LoadOrderFilePath;
            DataFolderPath = _environment.DataFolderPath; // If a custom data folder path was provided it will not change. If no custom data folder path was provided, this will set it to the default path.

            CreationClubPlugins = GetCreationClubPlugins();
            
            ComputeFormIdPrefixes();
            
            Status = EnvironmentStatus.Valid;
            NumPlugins = LoadOrder.ListedOrder.Count();
            NumActivePlugins = LoadOrder.ListedOrder.Count(p => p.Enabled);
        }
        catch (Exception ex)
        {
            EnvironmentBuilderError = ExceptionLogger.GetExceptionStack(ex);
            Status = EnvironmentStatus.Invalid;
        }
        
        _environmentUpdatedSubject.OnNext(Unit.Default);
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
}