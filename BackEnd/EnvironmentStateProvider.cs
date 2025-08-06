using System.Diagnostics;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Environments;
using Noggog;
using NPC_Plugin_Chooser_2.Models;
using System.IO;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.View_Models;
using YamlDotNet.RepresentationModel;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class EnvironmentStateProvider
{
    // "Core" state properties and fields
    private IGameEnvironment<ISkyrimMod, ISkyrimModGetter> _environment;
    private readonly VM_SplashScreen? _splashReporter; // Nullable if used optionally
    public ILoadOrderGetter<IModListingGetter<ISkyrimModGetter>> LoadOrder => _environment.LoadOrder;
    public List<IModListingGetter<ISkyrimModGetter>> LoadOrderList => LoadOrder.ListedOrder.ToList();
    public List<ModKey> LoadOrderModKeys => LoadOrderList.Select(m => m.ModKey).ToList();
    public ILinkCache<ISkyrimMod, ISkyrimModGetter> LinkCache => _environment.LinkCache;
    public SkyrimRelease SkyrimVersion { get; set; }
    public DirectoryPath ExtraSettingsDataPath { get; set; }
    public DirectoryPath InternalDataPath { get; set; }
    public DirectoryPath DataFolderPath { get; set; }
    public ISkyrimMod OutputMod { get; set; }
    public HashSet<ModKey> BaseGamePlugins => Implicits.Get(SkyrimVersion.ToGameRelease()).BaseMasters.ToHashSet();
    public HashSet<ModKey> CreationClubPlugins  { get; set; }
    public ModKey AbsoluteBasePlugin = ModKey.FromFileName("Skyrim.esm");
    
    public string OutputPluginName { get; set; }
    public string OutputPluginFileName => OutputPluginName + ".esp";
    public bool EnvironmentIsValid { get; set; } = false;
    
    // Additional properties (for logging only)
    public string CreationClubListingsFilePath { get; set; }
    public string LoadOrderFilePath { get; set; }
    public string EnvironmentBuilderError { get; set; }
    
    // Additional fields to help other classes
    private readonly Dictionary<ModKey, string> _modKeyFormIdPrefixCache = new();

    public EnvironmentStateProvider(Settings settings, VM_SplashScreen? splashReporter = null)
    {
        _splashReporter = splashReporter;
        
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

        SkyrimVersion = settings.SkyrimRelease;
        if (!settings.SkyrimGamePath.IsNullOrWhitespace())
        {
            DataFolderPath = settings.SkyrimGamePath;
        }
        
        if (!settings.OutputPluginName.IsNullOrWhitespace())
        {
            OutputPluginName = settings.OutputPluginName;
        }
        
        UpdateEnvironment(70);
    }

    public void UpdateEnvironment(double baseProgress = 0, double progressSpan = 5)
    {
        _splashReporter?.UpdateProgress(baseProgress, "Initializing game environment...");
        EnvironmentBuilderError = string.Empty;
        EnvironmentIsValid = false;
        
        var builder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(SkyrimVersion.ToGameRelease());
        if (!DataFolderPath.ToString().IsNullOrWhitespace())
        {
            builder = builder.WithTargetDataFolder(DataFolderPath);
        }

        var validatedName = Path.GetFileNameWithoutExtension(OutputPluginName);
        
        OutputMod = null;
        OutputMod = new SkyrimMod(ModKey.FromName(validatedName, ModType.Plugin), SkyrimVersion);

        var built = false;

        try
        {
            _splashReporter?.UpdateProgress(baseProgress + (progressSpan * 0.2), "Building game environment object...");
            string notificationStr = "";
            _environment = builder
                .TransformModListings(x =>
                    x.OnlyEnabledAndExisting()
                        .TrimPluginAndDependents(OutputMod.ModKey))
                    .WithOutputMod(OutputMod)
                .Build();
            
            _splashReporter?.UpdateProgress(baseProgress + (progressSpan * 0.6), "Validating load order path...");
            
            if (!_environment.LoadOrderFilePath.Exists)
            {
                EnvironmentBuilderError =  "Load order file path at " + _environment.LoadOrderFilePath.Path + " does not exist"; // prevent successful initialization in the wrong mode.
                _splashReporter?.UpdateProgress(baseProgress + progressSpan, $"Environment Error: {EnvironmentBuilderError}");
                return;
            }

            EnvironmentIsValid = true;
            
            CreationClubListingsFilePath = _environment.CreationClubListingsFilePath ?? string.Empty;
            LoadOrderFilePath = _environment.LoadOrderFilePath;
            DataFolderPath = _environment.DataFolderPath; // If a custom data folder path was provided it will not change. If no custom data folder path was provided, this will set it to the default path.

            CreationClubPlugins = GetCreationClubPlugins();
            
            ComputeFormIdPrefixes();
            
            _splashReporter?.UpdateProgress(baseProgress + progressSpan, "Game environment initialized successfully.");
        }
        catch (Exception ex)
        {
            EnvironmentBuilderError = ExceptionLogger.GetExceptionStack(ex);
            _splashReporter?.UpdateProgress(baseProgress + progressSpan, "Error initializing game environment.");
        }
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
}