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

namespace NPC_Plugin_Chooser_2.BackEnd;

public class EnvironmentStateProvider
{
    // "Core" state properties and fields
    private IGameEnvironment<ISkyrimMod, ISkyrimModGetter> _environment;
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

    public EnvironmentStateProvider(Settings settings)
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

        SkyrimVersion = settings.SkyrimRelease;
        if (!settings.SkyrimGamePath.IsNullOrWhitespace())
        {
            DataFolderPath = settings.SkyrimGamePath;
        }
        
        if (!settings.OutputPluginName.IsNullOrWhitespace())
        {
            OutputPluginName = settings.OutputPluginName;
        }
        
        UpdateEnvironment();
    }

    public void UpdateEnvironment()
    {
        EnvironmentBuilderError = string.Empty;
        EnvironmentIsValid = false;
        
        var builder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(SkyrimVersion.ToGameRelease());
        if (!DataFolderPath.ToString().IsNullOrWhitespace())
        {
            builder = builder.WithTargetDataFolder(DataFolderPath);
        }

        OutputMod = null;
        OutputMod = new SkyrimMod(ModKey.FromName(OutputPluginFileName, ModType.Plugin), SkyrimVersion);

        var built = false;

        try
        {
            string notificationStr = "";
            _environment = builder
                .TransformModListings(x =>
                    x.OnlyEnabledAndExisting())
                    .WithOutputMod(OutputMod)
                .Build();
            
            if (!_environment.LoadOrderFilePath.Exists)
            {
                EnvironmentBuilderError =  "Load order file path at " + _environment.LoadOrderFilePath.Path + " does not exist"; // prevent successful initialization in the wrong mode.
                return;
            }

            EnvironmentIsValid = true;
            
            CreationClubListingsFilePath = _environment.CreationClubListingsFilePath ?? string.Empty;
            LoadOrderFilePath = _environment.LoadOrderFilePath;
            DataFolderPath = _environment.DataFolderPath; // If a custom data folder path was provided it will not change. If no custom data folder path was provided, this will set it to the default path.
        }
        catch (Exception ex)
        {
            EnvironmentBuilderError = ExceptionLogger.GetExceptionStack(ex);
        }
    }
}