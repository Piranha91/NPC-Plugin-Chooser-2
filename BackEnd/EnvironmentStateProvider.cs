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

namespace NPC_Plugin_Chooser_2.BackEnd;

public class EnvironmentStateProvider
{
    // "Core" state properties and fields
    private IGameEnvironment<ISkyrimMod, ISkyrimModGetter> _environment;
    private readonly VM_SplashScreen? _splashReporter; // Nullable if used optionally
    public ILoadOrderGetter<IModListingGetter<ISkyrimModGetter>> LoadOrder => _environment.LoadOrder;
    public ILinkCache<ISkyrimMod, ISkyrimModGetter> LinkCache => _environment.LinkCache;
    public SkyrimRelease SkyrimVersion { get; set; }
    public DirectoryPath ExtraSettingsDataPath { get; set; }
    public DirectoryPath InternalDataPath { get; set; }
    public DirectoryPath DataFolderPath { get; set; }
    public ISkyrimMod OutputMod { get; set; }
    
    public string OutputPluginName { get; set; }
    public string OutputPluginFileName => OutputPluginName + ".esp";
    public bool EnvironmentIsValid { get; set; } = false;
    
    // Additional properties (for logging only)
    public string CreationClubListingsFilePath { get; set; }
    public string LoadOrderFilePath { get; set; }
    public string EnvironmentBuilderError { get; set; }

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
            _splashReporter?.UpdateProgress(baseProgress + progressSpan, "Game environment initialized successfully.");
        }
        catch (Exception ex)
        {
            EnvironmentBuilderError = ExceptionLogger.GetExceptionStack(ex);
            _splashReporter?.UpdateProgress(baseProgress + progressSpan, "Error initializing game environment.");
        }
    }
}