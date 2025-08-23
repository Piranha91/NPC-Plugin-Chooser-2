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

    public bool IsValidAppearanceRace(FormKey raceFormKey, INpcGetter npcGetter, out string rejectionMessage, IRaceGetter? sourcePluginRace = null)
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
                        identifier = GetLogString(raceGetter, true);
                    }
                    else if (!isTemplate && !raceGetter.Keywords.Contains(Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim
                                 .Keyword
                                 .ActorTypeNPC))
                    {
                        raceEvaluation = RaceEvaluation.InvalidNotNpc;
                        identifier = GetLogString(raceGetter, true);
                    }
                    else
                    {
                        raceEvaluation = RaceEvaluation.Valid;
                        identifier = GetLogString(raceGetter, true);
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
    public static IEnumerable<IMajorRecordGetter> LazyEnumerateMajorRecords(ISkyrimModGetter mod)
    {
        foreach (var record in mod.AcousticSpaces) yield return record;
        foreach (var record in mod.Actions) yield return record;
        foreach (var record in mod.Activators) yield return record;
        foreach (var record in mod.ActorValueInformation) yield return record;
        foreach (var record in mod.AddonNodes) yield return record;
        foreach (var record in mod.AlchemicalApparatuses) yield return record;
        foreach (var record in mod.Ammunitions) yield return record;
        foreach (var record in mod.AnimatedObjects) yield return record;
        foreach (var record in mod.ArmorAddons) yield return record;
        foreach (var record in mod.Armors) yield return record;
        foreach (var record in mod.ArtObjects) yield return record;
        foreach (var record in mod.AssociationTypes) yield return record;
        foreach (var record in mod.BodyParts) yield return record;
        foreach (var record in mod.Books) yield return record;
        foreach (var record in mod.CameraPaths) yield return record;
        foreach (var record in mod.CameraShots) yield return record;
        foreach (var record in mod.Classes) yield return record;
        foreach (var record in mod.Climates) yield return record;
        foreach (var record in mod.CollisionLayers) yield return record;
        foreach (var record in mod.Colors) yield return record;
        foreach (var record in mod.CombatStyles) yield return record;
        foreach (var record in mod.ConstructibleObjects) yield return record;
        foreach (var record in mod.Containers) yield return record;
        foreach (var record in mod.Debris) yield return record;
        foreach (var record in mod.DefaultObjectManagers) yield return record;
        foreach (var record in mod.DialogBranches) yield return record;
        foreach (var record in mod.DialogTopics) yield return record;
        foreach (var record in mod.DialogViews) yield return record;
        foreach (var record in mod.Doors) yield return record;
        foreach (var record in mod.DualCastData) yield return record;
        foreach (var record in mod.EffectShaders) yield return record;
        foreach (var record in mod.EncounterZones) yield return record;
        foreach (var record in mod.EquipTypes) yield return record;
        foreach (var record in mod.Explosions) yield return record;
        foreach (var record in mod.Eyes) yield return record;
        foreach (var record in mod.Factions) yield return record;
        foreach (var record in mod.Florae) yield return record;
        foreach (var record in mod.FootstepSets) yield return record;
        foreach (var record in mod.Footsteps) yield return record;
        foreach (var record in mod.FormLists) yield return record;
        foreach (var record in mod.Furniture) yield return record;
        foreach (var record in mod.GameSettings) yield return record;
        foreach (var record in mod.Globals) yield return record;
        foreach (var record in mod.Grasses) yield return record;
        foreach (var record in mod.Hairs) yield return record;
        foreach (var record in mod.Hazards) yield return record;
        foreach (var record in mod.HeadParts) yield return record;
        foreach (var record in mod.IdleAnimations) yield return record;
        foreach (var record in mod.IdleMarkers) yield return record;
        foreach (var record in mod.ImageSpaceAdapters) yield return record;
        foreach (var record in mod.ImageSpaces) yield return record;
        foreach (var record in mod.ImpactDataSets) yield return record;
        foreach (var record in mod.Impacts) yield return record;
        foreach (var record in mod.Ingestibles) yield return record;
        foreach (var record in mod.Ingredients) yield return record;
        foreach (var record in mod.Keys) yield return record;
        foreach (var record in mod.Keywords) yield return record;
        foreach (var record in mod.LandscapeTextures) yield return record;
        foreach (var record in mod.LensFlares) yield return record;
        foreach (var record in mod.LeveledItems) yield return record;
        foreach (var record in mod.LeveledNpcs) yield return record;
        foreach (var record in mod.LeveledSpells) yield return record;
        foreach (var record in mod.LightingTemplates) yield return record;
        foreach (var record in mod.Lights) yield return record;
        foreach (var record in mod.LoadScreens) yield return record;
        foreach (var record in mod.LocationReferenceTypes) yield return record;
        foreach (var record in mod.Locations) yield return record;
        foreach (var record in mod.MagicEffects) yield return record;
        foreach (var record in mod.MaterialObjects) yield return record;
        foreach (var record in mod.MaterialTypes) yield return record;
        foreach (var record in mod.Messages) yield return record;
        foreach (var record in mod.MiscItems) yield return record;
        foreach (var record in mod.MoveableStatics) yield return record;
        foreach (var record in mod.MovementTypes) yield return record;
        foreach (var record in mod.MusicTracks) yield return record;
        foreach (var record in mod.MusicTypes) yield return record;
        foreach (var record in mod.NavigationMeshInfoMaps) yield return record;
        foreach (var record in mod.Npcs) yield return record;
        foreach (var record in mod.ObjectEffects) yield return record;
        foreach (var record in mod.Outfits) yield return record;
        foreach (var record in mod.Packages) yield return record;
        foreach (var record in mod.Perks) yield return record;
        foreach (var record in mod.Projectiles) yield return record;
        foreach (var record in mod.Quests) yield return record;
        foreach (var record in mod.Races) yield return record;
        foreach (var record in mod.Regions) yield return record;
        foreach (var record in mod.Relationships) yield return record;
        foreach (var record in mod.ReverbParameters) yield return record;
        foreach (var record in mod.Scenes) yield return record;
        foreach (var record in mod.Scrolls) yield return record;
        foreach (var record in mod.ShaderParticleGeometries) yield return record;
        foreach (var record in mod.Shouts) yield return record;
        foreach (var record in mod.SoulGems) yield return record;
        foreach (var record in mod.SoundCategories) yield return record;
        foreach (var record in mod.SoundDescriptors) yield return record;
        foreach (var record in mod.SoundMarkers) yield return record;
        foreach (var record in mod.SoundOutputModels) yield return record;
        foreach (var record in mod.Spells) yield return record;
        foreach (var record in mod.Statics) yield return record;
        foreach (var record in mod.StoryManagerBranchNodes) yield return record;
        foreach (var record in mod.StoryManagerEventNodes) yield return record;
        foreach (var record in mod.StoryManagerQuestNodes) yield return record;
        foreach (var record in mod.TalkingActivators) yield return record;
        foreach (var record in mod.TextureSets) yield return record;
        foreach (var record in mod.Trees) yield return record;
        foreach (var record in mod.VisualEffects) yield return record;
        foreach (var record in mod.VoiceTypes) yield return record;
        foreach (var record in mod.VolumetricLightings) yield return record;
        foreach (var record in mod.Waters) yield return record;
        foreach (var record in mod.Weapons) yield return record;
        foreach (var record in mod.Weathers) yield return record;
        foreach (var record in mod.WordsOfPower) yield return record;
        foreach (var record in mod.Worldspaces) yield return record;
    }
    
    /// <summary>
    /// Lazily enumerates all major records in a mod, skipping any record types included in the provided HashSet.
    /// </summary>
    public static IEnumerable<IMajorRecordGetter> LazyEnumerateMajorRecords(ISkyrimModGetter mod, HashSet<Type> typesToSkip)
    {
        if (!typesToSkip.Contains(typeof(IAcousticSpaceGetter))) foreach (var record in mod.AcousticSpaces) yield return record;
        if (!typesToSkip.Contains(typeof(IActionRecordGetter))) foreach (var record in mod.Actions) yield return record;
        if (!typesToSkip.Contains(typeof(IActivatorGetter))) foreach (var record in mod.Activators) yield return record;
        if (!typesToSkip.Contains(typeof(IActorValueInformationGetter))) foreach (var record in mod.ActorValueInformation) yield return record;
        if (!typesToSkip.Contains(typeof(IAddonNodeGetter))) foreach (var record in mod.AddonNodes) yield return record;
        if (!typesToSkip.Contains(typeof(IAlchemicalApparatusGetter))) foreach (var record in mod.AlchemicalApparatuses) yield return record;
        if (!typesToSkip.Contains(typeof(IAmmunitionGetter))) foreach (var record in mod.Ammunitions) yield return record;
        if (!typesToSkip.Contains(typeof(IAnimatedObjectGetter))) foreach (var record in mod.AnimatedObjects) yield return record;
        if (!typesToSkip.Contains(typeof(IArmorAddonGetter))) foreach (var record in mod.ArmorAddons) yield return record;
        if (!typesToSkip.Contains(typeof(IArmorGetter))) foreach (var record in mod.Armors) yield return record;
        if (!typesToSkip.Contains(typeof(IArtObjectGetter))) foreach (var record in mod.ArtObjects) yield return record;
        if (!typesToSkip.Contains(typeof(IAssociationTypeGetter))) foreach (var record in mod.AssociationTypes) yield return record;
        if (!typesToSkip.Contains(typeof(IBodyPartGetter))) foreach (var record in mod.BodyParts) yield return record;
        if (!typesToSkip.Contains(typeof(IBookGetter))) foreach (var record in mod.Books) yield return record;
        if (!typesToSkip.Contains(typeof(ICameraPathGetter))) foreach (var record in mod.CameraPaths) yield return record;
        if (!typesToSkip.Contains(typeof(ICameraShotGetter))) foreach (var record in mod.CameraShots) yield return record;
        if (!typesToSkip.Contains(typeof(IClassGetter))) foreach (var record in mod.Classes) yield return record;
        if (!typesToSkip.Contains(typeof(IClimateGetter))) foreach (var record in mod.Climates) yield return record;
        if (!typesToSkip.Contains(typeof(ICollisionLayerGetter))) foreach (var record in mod.CollisionLayers) yield return record;
        if (!typesToSkip.Contains(typeof(IColorRecordGetter))) foreach (var record in mod.Colors) yield return record;
        if (!typesToSkip.Contains(typeof(ICombatStyleGetter))) foreach (var record in mod.CombatStyles) yield return record;
        if (!typesToSkip.Contains(typeof(IConstructibleObjectGetter))) foreach (var record in mod.ConstructibleObjects) yield return record;
        if (!typesToSkip.Contains(typeof(IContainerGetter))) foreach (var record in mod.Containers) yield return record;
        if (!typesToSkip.Contains(typeof(IDebrisGetter))) foreach (var record in mod.Debris) yield return record;
        if (!typesToSkip.Contains(typeof(IDefaultObjectManagerGetter))) foreach (var record in mod.DefaultObjectManagers) yield return record;
        if (!typesToSkip.Contains(typeof(IDialogBranchGetter))) foreach (var record in mod.DialogBranches) yield return record;
        if (!typesToSkip.Contains(typeof(IDialogTopicGetter))) foreach (var record in mod.DialogTopics) yield return record;
        if (!typesToSkip.Contains(typeof(IDialogViewGetter))) foreach (var record in mod.DialogViews) yield return record;
        if (!typesToSkip.Contains(typeof(IDoorGetter))) foreach (var record in mod.Doors) yield return record;
        if (!typesToSkip.Contains(typeof(IDualCastDataGetter))) foreach (var record in mod.DualCastData) yield return record;
        if (!typesToSkip.Contains(typeof(IEffectShaderGetter))) foreach (var record in mod.EffectShaders) yield return record;
        if (!typesToSkip.Contains(typeof(IEncounterZoneGetter))) foreach (var record in mod.EncounterZones) yield return record;
        if (!typesToSkip.Contains(typeof(IEquipTypeGetter))) foreach (var record in mod.EquipTypes) yield return record;
        if (!typesToSkip.Contains(typeof(IExplosionGetter))) foreach (var record in mod.Explosions) yield return record;
        if (!typesToSkip.Contains(typeof(IEyesGetter))) foreach (var record in mod.Eyes) yield return record;
        if (!typesToSkip.Contains(typeof(IFactionGetter))) foreach (var record in mod.Factions) yield return record;
        if (!typesToSkip.Contains(typeof(IFloraGetter))) foreach (var record in mod.Florae) yield return record;
        if (!typesToSkip.Contains(typeof(IFootstepSetGetter))) foreach (var record in mod.FootstepSets) yield return record;
        if (!typesToSkip.Contains(typeof(IFootstepGetter))) foreach (var record in mod.Footsteps) yield return record;
        if (!typesToSkip.Contains(typeof(IFormListGetter))) foreach (var record in mod.FormLists) yield return record;
        if (!typesToSkip.Contains(typeof(IFurnitureGetter))) foreach (var record in mod.Furniture) yield return record;
        if (!typesToSkip.Contains(typeof(IGameSettingGetter))) foreach (var record in mod.GameSettings) yield return record;
        if (!typesToSkip.Contains(typeof(IGlobalGetter))) foreach (var record in mod.Globals) yield return record;
        if (!typesToSkip.Contains(typeof(IGrassGetter))) foreach (var record in mod.Grasses) yield return record;
        if (!typesToSkip.Contains(typeof(IHairGetter))) foreach (var record in mod.Hairs) yield return record;
        if (!typesToSkip.Contains(typeof(IHazardGetter))) foreach (var record in mod.Hazards) yield return record;
        if (!typesToSkip.Contains(typeof(IHeadPartGetter))) foreach (var record in mod.HeadParts) yield return record;
        if (!typesToSkip.Contains(typeof(IIdleAnimationGetter))) foreach (var record in mod.IdleAnimations) yield return record;
        if (!typesToSkip.Contains(typeof(IIdleMarkerGetter))) foreach (var record in mod.IdleMarkers) yield return record;
        if (!typesToSkip.Contains(typeof(IImageSpaceAdapterGetter))) foreach (var record in mod.ImageSpaceAdapters) yield return record;
        if (!typesToSkip.Contains(typeof(IImageSpaceGetter))) foreach (var record in mod.ImageSpaces) yield return record;
        if (!typesToSkip.Contains(typeof(IImpactDataSetGetter))) foreach (var record in mod.ImpactDataSets) yield return record;
        if (!typesToSkip.Contains(typeof(IImpactGetter))) foreach (var record in mod.Impacts) yield return record;
        if (!typesToSkip.Contains(typeof(IIngestibleGetter))) foreach (var record in mod.Ingestibles) yield return record;
        if (!typesToSkip.Contains(typeof(IIngredientGetter))) foreach (var record in mod.Ingredients) yield return record;
        if (!typesToSkip.Contains(typeof(IKeyGetter))) foreach (var record in mod.Keys) yield return record;
        if (!typesToSkip.Contains(typeof(IKeywordGetter))) foreach (var record in mod.Keywords) yield return record;
        if (!typesToSkip.Contains(typeof(ILandscapeTextureGetter))) foreach (var record in mod.LandscapeTextures) yield return record;
        if (!typesToSkip.Contains(typeof(ILensFlareGetter))) foreach (var record in mod.LensFlares) yield return record;
        if (!typesToSkip.Contains(typeof(ILeveledItemGetter))) foreach (var record in mod.LeveledItems) yield return record;
        if (!typesToSkip.Contains(typeof(ILeveledNpcGetter))) foreach (var record in mod.LeveledNpcs) yield return record;
        if (!typesToSkip.Contains(typeof(ILeveledSpellGetter))) foreach (var record in mod.LeveledSpells) yield return record;
        if (!typesToSkip.Contains(typeof(ILightingTemplateGetter))) foreach (var record in mod.LightingTemplates) yield return record;
        if (!typesToSkip.Contains(typeof(ILightGetter))) foreach (var record in mod.Lights) yield return record;
        if (!typesToSkip.Contains(typeof(ILoadScreenGetter))) foreach (var record in mod.LoadScreens) yield return record;
        if (!typesToSkip.Contains(typeof(ILocationReferenceTypeGetter))) foreach (var record in mod.LocationReferenceTypes) yield return record;
        if (!typesToSkip.Contains(typeof(ILocationGetter))) foreach (var record in mod.Locations) yield return record;
        if (!typesToSkip.Contains(typeof(IMagicEffectGetter))) foreach (var record in mod.MagicEffects) yield return record;
        if (!typesToSkip.Contains(typeof(IMaterialObjectGetter))) foreach (var record in mod.MaterialObjects) yield return record;
        if (!typesToSkip.Contains(typeof(IMaterialTypeGetter))) foreach (var record in mod.MaterialTypes) yield return record;
        if (!typesToSkip.Contains(typeof(IMessageGetter))) foreach (var record in mod.Messages) yield return record;
        if (!typesToSkip.Contains(typeof(IMiscItemGetter))) foreach (var record in mod.MiscItems) yield return record;
        if (!typesToSkip.Contains(typeof(IMoveableStaticGetter))) foreach (var record in mod.MoveableStatics) yield return record;
        if (!typesToSkip.Contains(typeof(IMovementTypeGetter))) foreach (var record in mod.MovementTypes) yield return record;
        if (!typesToSkip.Contains(typeof(IMusicTrackGetter))) foreach (var record in mod.MusicTracks) yield return record;
        if (!typesToSkip.Contains(typeof(IMusicTypeGetter))) foreach (var record in mod.MusicTypes) yield return record;
        if (!typesToSkip.Contains(typeof(INavigationMeshInfoMapGetter))) foreach (var record in mod.NavigationMeshInfoMaps) yield return record;
        if (!typesToSkip.Contains(typeof(INpcGetter))) foreach (var record in mod.Npcs) yield return record;
        if (!typesToSkip.Contains(typeof(IObjectEffectGetter))) foreach (var record in mod.ObjectEffects) yield return record;
        if (!typesToSkip.Contains(typeof(IOutfitGetter))) foreach (var record in mod.Outfits) yield return record;
        if (!typesToSkip.Contains(typeof(IPackageGetter))) foreach (var record in mod.Packages) yield return record;
        if (!typesToSkip.Contains(typeof(IPerkGetter))) foreach (var record in mod.Perks) yield return record;
        if (!typesToSkip.Contains(typeof(IProjectileGetter))) foreach (var record in mod.Projectiles) yield return record;
        if (!typesToSkip.Contains(typeof(IQuestGetter))) foreach (var record in mod.Quests) yield return record;
        if (!typesToSkip.Contains(typeof(IRaceGetter))) foreach (var record in mod.Races) yield return record;
        if (!typesToSkip.Contains(typeof(IRegionGetter))) foreach (var record in mod.Regions) yield return record;
        if (!typesToSkip.Contains(typeof(IRelationshipGetter))) foreach (var record in mod.Relationships) yield return record;
        if (!typesToSkip.Contains(typeof(IReverbParametersGetter))) foreach (var record in mod.ReverbParameters) yield return record;
        if (!typesToSkip.Contains(typeof(ISceneGetter))) foreach (var record in mod.Scenes) yield return record;
        if (!typesToSkip.Contains(typeof(IScrollGetter))) foreach (var record in mod.Scrolls) yield return record;
        if (!typesToSkip.Contains(typeof(IShaderParticleGeometryGetter))) foreach (var record in mod.ShaderParticleGeometries) yield return record;
        if (!typesToSkip.Contains(typeof(IShoutGetter))) foreach (var record in mod.Shouts) yield return record;
        if (!typesToSkip.Contains(typeof(ISoulGemGetter))) foreach (var record in mod.SoulGems) yield return record;
        if (!typesToSkip.Contains(typeof(ISoundCategoryGetter))) foreach (var record in mod.SoundCategories) yield return record;
        if (!typesToSkip.Contains(typeof(ISoundDescriptorGetter))) foreach (var record in mod.SoundDescriptors) yield return record;
        if (!typesToSkip.Contains(typeof(ISoundMarkerGetter))) foreach (var record in mod.SoundMarkers) yield return record;
        if (!typesToSkip.Contains(typeof(ISoundOutputModelGetter))) foreach (var record in mod.SoundOutputModels) yield return record;
        if (!typesToSkip.Contains(typeof(ISpellGetter))) foreach (var record in mod.Spells) yield return record;
        if (!typesToSkip.Contains(typeof(IStaticGetter))) foreach (var record in mod.Statics) yield return record;
        if (!typesToSkip.Contains(typeof(IStoryManagerBranchNodeGetter))) foreach (var record in mod.StoryManagerBranchNodes) yield return record;
        if (!typesToSkip.Contains(typeof(IStoryManagerEventNodeGetter))) foreach (var record in mod.StoryManagerEventNodes) yield return record;
        if (!typesToSkip.Contains(typeof(IStoryManagerQuestNodeGetter))) foreach (var record in mod.StoryManagerQuestNodes) yield return record;
        if (!typesToSkip.Contains(typeof(ITalkingActivatorGetter))) foreach (var record in mod.TalkingActivators) yield return record;
        if (!typesToSkip.Contains(typeof(ITextureSetGetter))) foreach (var record in mod.TextureSets) yield return record;
        if (!typesToSkip.Contains(typeof(ITreeGetter))) foreach (var record in mod.Trees) yield return record;
        if (!typesToSkip.Contains(typeof(IVisualEffectGetter))) foreach (var record in mod.VisualEffects) yield return record;
        if (!typesToSkip.Contains(typeof(IVoiceTypeGetter))) foreach (var record in mod.VoiceTypes) yield return record;
        if (!typesToSkip.Contains(typeof(IVolumetricLightingGetter))) foreach (var record in mod.VolumetricLightings) yield return record;
        if (!typesToSkip.Contains(typeof(IWaterGetter))) foreach (var record in mod.Waters) yield return record;
        if (!typesToSkip.Contains(typeof(IWeaponGetter))) foreach (var record in mod.Weapons) yield return record;
        if (!typesToSkip.Contains(typeof(IWeatherGetter))) foreach (var record in mod.Weathers) yield return record;
        if (!typesToSkip.Contains(typeof(IWordOfPowerGetter))) foreach (var record in mod.WordsOfPower) yield return record;
        if (!typesToSkip.Contains(typeof(IWorldspaceGetter))) foreach (var record in mod.Worldspaces) yield return record;
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
    
    public static bool TryGetPatchRecordGroup(Type getterType, ISkyrimModGetter outputMod, out dynamic? group)
    {
        bool success = false;
        group = null;
        try
        {
            group = outputMod.GetTopLevelGroup(getterType);
        }
        catch
        {

        }

        if (group == null)
        {
            group = outputMod.GetTopLevelGroup(GetLoquiType(getterType));
        }

        return group != null;
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


public enum Gender
{
    Female,
    Male
}