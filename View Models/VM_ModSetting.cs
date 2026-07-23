using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics; // For Debug.WriteLine
using System.IO;
using System.Reactive;
using System.Reactive.Linq; // Needed for Select and ObservableAsPropertyHelper
using System.Windows.Forms; // For FolderBrowserDialog
using Mutagen.Bethesda.Plugins;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Text;
using System.Windows;
using System.Windows.Media;
using DynamicData;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using GongSolutions.Wpf.DragDrop;
using Microsoft.Extensions.Primitives;
using Mutagen.Bethesda.Strings;
using DragDrop = GongSolutions.Wpf.DragDrop.DragDrop;
using IDropTarget = GongSolutions.Wpf.DragDrop.IDropTarget;
using LinkCacheConstructionMixIn = Mutagen.Bethesda.LinkCacheConstructionMixIn; // Assuming Models namespace

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Enum to specify the type of path being modified for merge checks.
/// </summary>
public enum PathType
{
    MugshotFolder,
    ModFolder
}

/// <summary>
/// Enum to specify the type of issue that a given modded NPC has
/// </summary>
public enum NpcIssueType
{
    Template,
    FaceGenOnly
}

[DebuggerDisplay("{DisplayName}")]
public class VM_ModSetting : ReactiveObject, IDisposable, IDropTarget
{
    public void Dispose()
    {
        _disposables.Dispose();
    }

    private readonly CompositeDisposable _disposables = new CompositeDisposable();

    // --- Factory Delegates ---
    public delegate VM_ModSetting FromModelFactory(ModSetting model, VM_Mods parentVm);

    public delegate VM_ModSetting FromMugshotPathFactory(string displayName, string mugshotPath, VM_Mods parentVm);

    public delegate VM_ModSetting FromModFolderFactory(string modFolderPath, IEnumerable<ModKey>? plugins,
        VM_Mods parentVm);

    // --- Properties ---
    [Reactive] public string DisplayName { get; set; } = string.Empty;
    [Reactive] public ObservableCollection<string> MugShotFolderPaths { get; private set; } = new();
    [Reactive] public ObservableCollection<ModKey> CorrespondingModKeys { get; private set; } = new();
    [Reactive] public HashSet<ModKey> ResourceOnlyModKeys { get; set; } = new();
    [Reactive] public ObservableCollection<string> CorrespondingFolderPaths { get; private set; } = new();
    [Reactive] public bool MergeInDependencyRecords { get; set; } = true;
    [Reactive] public bool MergeInDependencyRecordsVisible { get; set; } = true;
    [Reactive] public bool IncludeOutfits { get; set; } = false;
    [Reactive] public bool CopyAssets { get; set; } = true;

    // Base-game-overwrite protection (see Models.ModSetting for semantics). The checkbox is
    // only shown when the import/refresh scan found colliding asset paths in this mod.
    [Reactive] public bool OverwriteBaseGameAssets { get; set; } = false;
    [Reactive] public bool HasBaseGameAssetPaths { get; set; } = false;
    [Reactive] public int BaseGameAssetPathCount { get; set; } = 0;
    [ObservableAsProperty] public string OverwriteBaseGameAssetsToolTip { get; }

    [Reactive] public string MergeInToolTip { get; set; } = ModSetting.DefaultMergeInTooltip;
    [Reactive] public bool HasAlteredMergeLogic { get; set; } = false;
    [Reactive] public bool HandleInjectedRecords { get; set; } = false;
    [Reactive] public bool HasAlteredHandleInjectedRecordsLogic { get; set; } = false;
    [Reactive] public string HandleInjectedOverridesToolTip { get; set; } = ModSetting.DefaultRecordInjectionToolTip;

    [Reactive] public RecordOverrideHandlingMode? OverrideRecordOverrideHandlingMode { get; set; }
    [Reactive] public bool IsOverrideHandlingControlsVisible { get; set; }
    [Reactive] public ObservableCollection<string> Keywords { get; set; } = new();
    private const string DefaultKeywordsToolTip = "Add Keyword records that will be applied to all NPCs receiving appearances from this mod.";
    [ObservableAsProperty] public string KeywordsToolTip { get; }

    public IEnumerable<KeyValuePair<RecordOverrideHandlingMode?, string>> RecordOverrideHandlingModes { get; }
        = new[]
            {
                new KeyValuePair<RecordOverrideHandlingMode?, string>(null, "Default")
            }
            .Concat(Enum.GetValues(typeof(RecordOverrideHandlingMode))
                .Cast<RecordOverrideHandlingMode>()
                .Select(e =>
                    new KeyValuePair<RecordOverrideHandlingMode?, string>(e, e.ToString())
                ));

    // SelectedItem bridge for the dropdown (see the sync subscriptions in the
    // constructor). WPF's SelectedValue/SelectedValuePath matching cannot
    // ESTABLISH a selection when the bound value is null (the "Default"
    // entry's Key), so any mod whose persisted mode was null rendered a blank
    // ComboBox on every launch — interactive clicks set SelectedItem directly,
    // which masked the bug until the next load. Binding SelectedItem to the
    // actual KeyValuePair (struct, structural equality) displays the null-keyed
    // entry correctly; OverrideRecordOverrideHandlingMode stays the persisted
    // source of truth.
    [Reactive] public KeyValuePair<RecordOverrideHandlingMode?, string> SelectedRecordOverrideHandlingItem { get; set; }

    [Reactive] public int MaxNestedIntervalDepth { get; set; } = 2;
    [Reactive] public bool IsMaxNestedIntervalDepthVisible { get; set; }
    [Reactive] public bool IncludeAllOverrides { get; set; } = false;

    // Wig/antler detection + per-mod handling modes (see Models.ModSetting /
    // Models.WigHandlingMode / Models.AntlerHandlingMode). Each dropdown is shown
    // only when the analysis scan found that class AND wig/antler handling is
    // active for the current output mode (Create-and-Patch or SkyPatcher). Wigs
    // and antlers are controlled independently.
    public HashSet<FormKey> DetectedWigArmors { get; set; } = new();
    public HashSet<FormKey> DetectedAntlerArmors { get; set; } = new();
    public HashSet<FormKey> DetectedAntlerArmatures { get; set; } = new();
    public HashSet<FormKey> DetectedAntlerHeadParts { get; set; } = new();
    [Reactive] public bool HasWigArmors { get; set; }
    [Reactive] public bool HasAntlers { get; set; }
    [Reactive] public WigHandlingMode? OverrideWigHandlingMode { get; set; }
    [Reactive] public AntlerHandlingMode? OverrideAntlerHandlingMode { get; set; }
    [Reactive] public bool IsWigHandlingVisible { get; set; }
    [Reactive] public bool IsAntlerHandlingVisible { get; set; }

    // Friendly names in display order (None last; see HandlingModeDisplay), with
    // the null-keyed "Default" entry first — matching the global Settings
    // dropdowns so both menus read identically.
    public IEnumerable<KeyValuePair<WigHandlingMode?, string>> WigHandlingModes { get; }
        = new[]
            {
                new KeyValuePair<WigHandlingMode?, string>(null, "Default")
            }
            .Concat(HandlingModeDisplay.WigModesInDisplayOrder
                .Select(e =>
                    new KeyValuePair<WigHandlingMode?, string>(e, HandlingModeDisplay.ToDisplayString(e))
                ));

    public IEnumerable<KeyValuePair<AntlerHandlingMode?, string>> AntlerHandlingModes { get; }
        = new[]
            {
                new KeyValuePair<AntlerHandlingMode?, string>(null, "Default")
            }
            .Concat(HandlingModeDisplay.AntlerModesInDisplayOrder
                .Select(e =>
                    new KeyValuePair<AntlerHandlingMode?, string>(e, HandlingModeDisplay.ToDisplayString(e))
                ));

    // SelectedItem bridges — same null-key display fix as
    // SelectedRecordOverrideHandlingItem (null = "Default" is these dropdowns'
    // normal state, so without the bridge they rendered blank for every mod).
    [Reactive] public KeyValuePair<WigHandlingMode?, string> SelectedWigHandlingItem { get; set; }
    [Reactive] public KeyValuePair<AntlerHandlingMode?, string> SelectedAntlerHandlingItem { get; set; }
    
    public HashSet<string> NpcNames { get; set; } = new();
    public HashSet<string> NpcEditorIDs { get; set; } = new();
    public HashSet<FormKey> NpcFormKeys { get; set; } = new();
    public Dictionary<FormKey, string> NpcFormKeysToDisplayName { get; set; } = new();

    /// <summary>
    /// ModKeys of NPCs that this mod patches via SkyPatcher templates (the "target" NPC
    /// in <c>filterByNpcs=...</c>). Populated during AnalyzeModSettingsAsync from
    /// <see cref="GetSkyPatcherImportsAsync"/>'s guest list. Used by
    /// <see cref="VM_Mods.FindAndAddMissingMasters"/> to treat the foundation plugin as an
    /// effective NPC source so the cleanup pass doesn't re-attach the foundation folder
    /// for SkyPatcher-style replacers (e.g. <c>t_Amalee_Replacer.esp</c> → <c>3DNPC.esp</c>).
    ///
    /// Intentionally NOT persisted: it's only consulted by the cleanup pass that runs
    /// inside the same <c>PopulateModSettingsAsync</c> call that populates it. On
    /// subsequent launches the persisted folder list is already correct, so an empty
    /// transient set is fine.
    /// </summary>
    public HashSet<ModKey> SkyPatcherTargetModKeys { get; set; } = new();

    public Dictionary<FormKey, List<ModKey>> AvailablePluginsForNpcs { get; set; } =
        new(); // tracks which plugins contain which Npc entry

    public Dictionary<FormKey, (NpcIssueType IssueType, string IssueMessage, FormKey? ReferencedFormKey)>
        NpcFormKeysToNotifications
            = new();
    // tracks any notifications the user should be alerted to for the given Npc

    // New Property: Maps NPC FormKey to the ModKey from which it should inherit data,
    // specifically for NPCs appearing in multiple plugins within this ModSetting.
    // This is loaded from and saved to Models.ModSetting.
    public Dictionary<FormKey, ModKey> NpcPluginDisambiguation { get; set; }

    // Stores FormKeys of NPCs found in multiple plugins within this setting (Error State)
    public HashSet<FormKey> AmbiguousNpcFormKeys { get; private set; } = new();
    [Reactive] public int NpcCount { get; private set; }
    public ModStateSnapshot? LastKnownState { get; set; } // hang on to DTO for serialization

    private readonly SkyrimRelease _skyrimRelease;
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Auxilliary _aux;
    private readonly BsaHandler _bsaHandler;
    private readonly PluginProvider _pluginProvider;
    private readonly RecordHandler _recordHandler;
    private readonly Lazy<VM_Settings> _lazySettingsVm;

    [Reactive] public bool IsRefreshing { get; set; } = false;

    // Flag indicating if this VM was created dynamically only from a Mugshot folder
    // and wasn't loaded from the persisted ModSettings.
    public bool IsMugshotOnlyEntry { get; set; } = false;

    // Flag indicating if this VM was created from a facegen-only Mod folder (in which case only NPCs with facegen
    // rather than all NPCs in the corresponding plugins should be displayed.
    // [Reactive] because it is assigned after construction (model load, folder scan, refresh) and
    // MergeInDependencyRecordsVisible is derived from it via a WhenAnyValue subscription.
    [Reactive] public bool IsFaceGenOnlyEntry { get; set; } = false;

    public HashSet<FormKey> FaceGenOnlyNpcFormKeys { get; set; } =
        new(); // NPCs contained in the given FaceGen-only mod

    // Flag indicating if this VM was created automatically from base game or creation club (in which case its data
    // folder path should remain unset.
    public bool IsAutoGenerated { get; set; } = false;

    // Helper properties derived from other reactive properties for UI Styling/Logic
    [ObservableAsProperty]
    public bool HasMugshotPathAssigned { get; } // True if MugShotFolderPath is not null/whitespace

    [ObservableAsProperty] public bool HasModPathsAssigned { get; } // True if CorrespondingFolderPaths has items

    // Calculated property for displaying the ModKey suffix in the UI
    private readonly ObservableAsPropertyHelper<string> _modKeyDisplaySuffix;
    public string ModKeyDisplaySuffix => _modKeyDisplaySuffix.Value;

    // Calculated property for displaying whether or not the contained plugins have an override
    private HashSet<ModKey> _pluginsWithOverrideRecords = new();
    public bool HasPluginWithOverrideRecords => _pluginsWithOverrideRecords.Any();

    // HasValidMugshots now indicates if *actual* mugshots are present.
    // If false, but MugShotFolderPath is assigned (or even if not),
    // the mod is still "clickable" to show placeholders.
    [Reactive] public bool HasValidMugshots { get; set; }

    private readonly ObservableAsPropertyHelper<bool> _canUnlinkMugshots;

    public bool CanUnlinkMugshots => _canUnlinkMugshots.Value;

    // Reactive property to control Delete button visibility ***
    [ObservableAsProperty] public bool CanDelete { get; }

    // Property control if additional expensive checks are performed the first time a mod is discovered
    public bool IsNewlyCreated { get; set; } = false;

    // Populated by VM_Mods.FindAndAddMissingMasters when a master listed in a plugin
    // header could not be located in any mod folder. Read by
    // PruneEmptyNewlyCreatedAppearanceMods to encode the "missing master" reason
    // in Settings.CachedMissingMasterMods. Not persisted -- recomputed on every scan.
    public List<string> UnresolvedMastersAtScan { get; set; } = new();

    // --- Commands ---
    public ReactiveCommand<Unit, Unit> AddFolderPathCommand { get; }
    public ReactiveCommand<string, Unit> BrowseFolderPathCommand { get; }
    public ReactiveCommand<string, Unit> RemoveFolderPathCommand { get; }
    public ReactiveCommand<Unit, Unit> AddMugshotFolderPathCommand { get; }
    public ReactiveCommand<string, Unit> BrowseMugshotFolderPathCommand { get; }
    public ReactiveCommand<string, Unit> RemoveMugshotFolderPathCommand { get; }
    public ReactiveCommand<Unit, Unit> UnlinkMugshotDataCommand { get; }
    public ReactiveCommand<Unit, Unit> SetResourcePluginsCommand { get; }
    public ReactiveCommand<Unit, Unit> SetKeywordsCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    // --- Private Fields ---
    private readonly VM_Mods _parentVm; // Reference to the parent VM (VM_Mods)
    private bool _isLoadingFromModel = false;

    // --- Block reactive notifications if performing batch action
    public bool IsPerformingBatchAction { get; set; } = false;

    // --- Constructors ---

    /// <summary>
    /// Constructor used when loading from an existing Models.ModSetting.
    /// Called by FromModelFactory.
    /// </summary>
    public VM_ModSetting(Models.ModSetting model, VM_Mods parentVm, Auxilliary aux, BsaHandler bsaHandler,
        PluginProvider pluginProvider, RecordHandler recordHandler, Lazy<VM_Settings> lazySettingsVm)
        : this(model.DisplayName, parentVm, aux, bsaHandler, pluginProvider, recordHandler, lazySettingsVm, isMugshotOnly: false,
            correspondingFolderPaths: model.CorrespondingFolderPaths,
            correspondingModKeys: model.CorrespondingModKeys)
    {
        _isLoadingFromModel = true;
        // Properties specific to loading existing model
        IsAutoGenerated = model.IsAutoGenerated; // make sure this loads first
        ResourceOnlyModKeys = new HashSet<ModKey>(model.ResourceOnlyModKeys ?? new HashSet<ModKey>());
        MugShotFolderPaths = new ObservableCollection<string>(model.MugShotFolderPaths);
        NpcPluginDisambiguation =
            new Dictionary<FormKey, ModKey>(model.NpcPluginDisambiguation ?? new Dictionary<FormKey, ModKey>());
        MergeInDependencyRecords = model.MergeInDependencyRecords;
        HasAlteredHandleInjectedRecordsLogic = model.HasAlteredHandleInjectedRecordsLogic;
        IncludeOutfits = model.IncludeOutfits;
        CopyAssets = model.CopyAssets;
        OverwriteBaseGameAssets = model.OverwriteBaseGameAssets;
        HasBaseGameAssetPaths = model.HasBaseGameAssetPaths;
        BaseGameAssetPathCount = model.BaseGameAssetPathCount;
        MergeInToolTip = model.MergeInToolTip;
        HandleInjectedRecords = model.HandleInjectedRecords;
        HasAlteredHandleInjectedRecordsLogic = model.HasAlteredHandleInjectedRecordsLogic;
        HandleInjectedOverridesToolTip = model.HandleInjectedOverridesToolTip;
        OverrideRecordOverrideHandlingMode = model.ModRecordOverrideHandlingMode;
        IncludeAllOverrides = model.IncludeAllOverrides;
        MaxNestedIntervalDepth = model.MaxNestedIntervalDepth;
        DetectedWigArmors = new HashSet<FormKey>(model.DetectedWigArmors ?? new HashSet<FormKey>());
        DetectedAntlerArmors = new HashSet<FormKey>(model.DetectedAntlerArmors ?? new HashSet<FormKey>());
        DetectedAntlerArmatures = new HashSet<FormKey>(model.DetectedAntlerArmatures ?? new HashSet<FormKey>());
        DetectedAntlerHeadParts = new HashSet<FormKey>(model.DetectedAntlerHeadParts ?? new HashSet<FormKey>());
        HasWigArmors = DetectedWigArmors.Count > 0;
        RecomputeHasAntlers();
        OverrideWigHandlingMode = model.ModWigHandlingMode;
        OverrideAntlerHandlingMode = model.ModAntlerHandlingMode;
        // AvailablePluginsForNpcs should be re-calculated on load.
        // IsMugshotOnlyEntry is set to false via chaining
        IsFaceGenOnlyEntry = model.IsFaceGenOnlyEntry;
        FaceGenOnlyNpcFormKeys = new(model.FaceGenOnlyNpcFormKeys);

        _pluginsWithOverrideRecords = model.PluginsWithOverrideRecords;
        HasAlteredMergeLogic = model.HasAlteredMergeLogic;
        LastKnownState = model.LastKnownState;

        NpcFormKeys = new HashSet<FormKey>(model.NpcFormKeys);
        NpcFormKeysToDisplayName = new Dictionary<FormKey, string>(model.NpcFormKeysToDisplayName);
        AvailablePluginsForNpcs = new Dictionary<FormKey, List<ModKey>>(model.AvailablePluginsForNpcs);
        AmbiguousNpcFormKeys = new HashSet<FormKey>(model.AmbiguousNpcFormKeys);
        NpcFormKeysToNotifications = new(model.NpcFormKeysToNotifications);
        Keywords = new ObservableCollection<string>(model.Keywords ?? new HashSet<string>());
        _isLoadingFromModel = false;
    }

    /// <summary>
    /// Constructor used when creating dynamically from a Mugshot folder.
    /// Called by FromMugshotPathFactory.
    /// </summary>
    public VM_ModSetting(string displayName, string mugshotPath, VM_Mods parentVm, Auxilliary aux,
        BsaHandler bsaHandler, PluginProvider pluginProvider, RecordHandler recordHandler, Lazy<VM_Settings> lazySettingsVm)
        : this(displayName, parentVm, aux, bsaHandler, pluginProvider, recordHandler, lazySettingsVm, isMugshotOnly: true)
    {
        MugShotFolderPaths = new() { mugshotPath };
    }

    /// <summary>
    /// Constructor used when creating from a new mod folder entry.
    /// Called by FromModFolderFactory.
    /// </summary>
    public VM_ModSetting(string modFolderPath, IEnumerable<ModKey>? plugins, VM_Mods parentVm, Auxilliary aux,
        BsaHandler bsaHandler, PluginProvider pluginProvider, RecordHandler recordHandler, Lazy<VM_Settings> lazySettingsVm)
        : this(Path.GetFileName(modFolderPath) ?? "Invalid Path",
            parentVm, aux, bsaHandler, pluginProvider, recordHandler, lazySettingsVm,
            isMugshotOnly: false,
            correspondingFolderPaths: new List<string>() { modFolderPath },
            correspondingModKeys: plugins ?? aux.GetModKeysInDirectory(modFolderPath, new List<string>(), false))
    {

    }

    /// <summary>
    /// Base constructor (private to enforce factory usage for specific scenarios if desired, or internal).
    /// Making it public for simplicity with Autofac delegate factories if they directly target this,
    /// but current setup chains to it.
    /// </summary>
    private VM_ModSetting(string displayName, VM_Mods parentVm, Auxilliary aux, BsaHandler bsaHandler,
        PluginProvider pluginProvider, RecordHandler recordHandler, Lazy<VM_Settings> lazySettingsVm, bool isMugshotOnly,
        IEnumerable<string>? correspondingFolderPaths = null, IEnumerable<ModKey>? correspondingModKeys = null)
    {
        _parentVm = parentVm;
        DisplayName = displayName;
        IsMugshotOnlyEntry = isMugshotOnly; // Set the flag based on how it was created
        _skyrimRelease = parentVm.SkyrimRelease; // Get SkyrimRelease from parent
        _environmentStateProvider = parentVm.EnvironmentStateProvider; // Get EnvironmentStateProvider from parent
        _aux = aux;
        _bsaHandler = bsaHandler;
        _pluginProvider = pluginProvider;
        _recordHandler = recordHandler;
        _lazySettingsVm = lazySettingsVm;

        // Initialize NpcPluginDisambiguation if not loaded from model (chained constructors handle this)
        if (NpcPluginDisambiguation ==
            null) // Should only be null if this base constructor is called directly without chaining from model constructor
        {
            NpcPluginDisambiguation = new Dictionary<FormKey, ModKey>();
        }

        // Set paths and trigger key update BEFORE subscriptions are created
        if (correspondingFolderPaths != null)
        {
            CorrespondingFolderPaths = new ObservableCollection<string>(correspondingFolderPaths);
        }

        if (correspondingModKeys == null)
        {
            UpdateCorrespondingModKeys();
        }
        else
        {
            CorrespondingModKeys = new(correspondingModKeys);
        }

        // --- Setup for ModKeyDisplaySuffix ---
        _modKeyDisplaySuffix = this
            .WhenAnyValue(x => x.DisplayName, x => x.CorrespondingModKeys.Count) // Trigger on count change
            .Select(_ =>
            {
                var name = DisplayName;
                var keys = CorrespondingModKeys;
                if (keys == null || !keys.Any()) return string.Empty;

                var keyStrings = keys.Select(k => k.ToString()).ToList();

                // Try to find a key matching the display name
                var matchingKey = keys.FirstOrDefault(k =>
                    !string.IsNullOrEmpty(name) && name.Equals(k.FileName, StringComparison.OrdinalIgnoreCase));
                if (keys.Count == 1)
                {
                    return $"({keys.First()})"; // Display single key if no match
                }
                else
                {
                    return $"({keys.Count} Plugins)"; // Indicate multiple plugins
                }
            })
            .ToProperty(this, x => x.ModKeyDisplaySuffix, scheduler: RxApp.MainThreadScheduler)
            .DisposeWith(_disposables);

        // --- Setup Reactive Helper Properties for UI ---
        this.WhenAnyValue(x => x.MugShotFolderPaths)
            .Select(paths =>
            {
                if (paths is null)
                    return Observable.Return(false);

                // Observe this collection's changes + seed current value
                var changes = Observable
                    .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                        h => paths.CollectionChanged += h,
                        h => paths.CollectionChanged -= h)
                    .StartWith((EventPattern<NotifyCollectionChangedEventArgs>?)null);

                return changes.Select(_ => HasAnyAssignedPath(paths));
            })
            .Switch() // switch to the latest collection
            .DistinctUntilChanged()
            .ToPropertyEx(this, x => x.HasMugshotPathAssigned)
            .DisposeWith(_disposables);

        Observable.Merge(
                this.WhenAnyValue(x => x.CorrespondingFolderPaths.Count).Select(count => count > 0),
                Observable.Return(this.CorrespondingFolderPaths?.Any() ?? false) // Initial check
            )
            .DistinctUntilChanged()
            .ToPropertyEx(this, x => x.HasModPathsAssigned)
            .DisposeWith(_disposables);

        // Merge-in is meaningless for FaceGen-only entries (no plugins to merge from; the
        // patcher neutralizes it per NPC anyway) and hard-disabled for the synthetic Base
        // Game / Creation Club entries — hide the checkbox in both cases. Reactive because
        // IsFaceGenOnlyEntry is assigned after construction (model load, folder scan, refresh).
        this.WhenAnyValue(x => x.IsFaceGenOnlyEntry)
            .Subscribe(isFaceGenOnly => MergeInDependencyRecordsVisible =
                !isFaceGenOnly &&
                DisplayName != VM_Mods.BaseGameModSettingName &&
                DisplayName != VM_Mods.CreationClubModsettingName)
            .DisposeWith(_disposables);

        // When folder paths are added or removed, trigger a full refresh of this mod setting.
        this.WhenAnyValue(x => x.CorrespondingFolderPaths.Count)
            .Skip(1) // Skip the initial value set in the constructor to avoid a refresh on load.
            .Throttle(TimeSpan.FromMilliseconds(500),
                RxApp.MainThreadScheduler) // Throttle to prevent rapid firing if multiple changes occur.
            .Select(_ => Unit.Default)
            .InvokeCommand(this, x => x.RefreshCommand) // Invoke the existing RefreshCommand.
            .DisposeWith(_disposables);

        // When MugShotFolderPath changes OR CorrespondingModKeys.Count changes,
        // re-evaluate HasValidMugshots.
        Observable.Merge(
                // React to the collection *instance* changing, then to its item changes
                this.WhenAnyValue(x => x.MugShotFolderPaths)
                    .Select(paths =>
                    {
                        if (paths is null)
                            return Observable.Return(Unit.Default); // nothing to watch yet

                        // Fire once immediately, then on add/remove/reset/etc.
                        return Observable
                            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                                h => paths.CollectionChanged += h,
                                h => paths.CollectionChanged -= h)
                            .StartWith((EventPattern<NotifyCollectionChangedEventArgs>?)null)
                            .Select(_ => Unit.Default);
                    })
                    .Switch(),

                // Still watch count changes on the other collection
                this.WhenAnyValue(x => x.CorrespondingModKeys.Count).Select(_ => Unit.Default)
            )
            .Skip(1) // keep your existing behavior if you don't want an initial recalc
            .Throttle(TimeSpan.FromMilliseconds(100))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => _parentVm?.RecalculateMugshotValidity(this))
            .DisposeWith(_disposables);

        // --- Setup for CanUnlinkMugshots ---
        // Build a stream that re-evaluates when either collection (or its items) changes
        var mugshotChanges =
            this.WhenAnyValue(x => x.MugShotFolderPaths)
                .Select(ObserveItems)
                .Switch();

        var dataFolderChanges =
            this.WhenAnyValue(x => x.CorrespondingFolderPaths)
                .Select(ObserveItems)
                .Switch();

        var dataCountChanges =
            this.WhenAnyValue(x => x.CorrespondingFolderPaths.Count).Select(_ => Unit.Default);

        // Merge all triggers and compute the bool
        _canUnlinkMugshots =
            Observable.Merge(mugshotChanges, dataFolderChanges, dataCountChanges)
                .StartWith(Unit.Default) // compute an initial value
                .Select(_ =>
                {
                    var mugshotNames = SafeFolderNames(this.MugShotFolderPaths);
                    if (mugshotNames.Count == 0) return false;

                    var dataNames = SafeFolderNames(this.CorrespondingFolderPaths);
                    if (dataNames.Count == 0) return false;

                    // Can unlink iff NO mugshot folder name matches ANY data folder name
                    return !mugshotNames.Overlaps(dataNames);
                })
                .DistinctUntilChanged()
                .ObserveOn(RxApp.MainThreadScheduler)
                .ToProperty(this, x => x.CanUnlinkMugshots)
                .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.MugShotFolderPaths.Count)
            .Select(count => count > 0)
            .ToPropertyEx(this, x => x.HasMugshotPathAssigned)
            .DisposeWith(_disposables);

        // --- Setup for CanDelete ---
        // Condition: MugShotFolderPaths are empty AND NO CorrespondingFolderPaths are assigned OR exist on disk.
        this.WhenAnyValue(
                x => x.MugShotFolderPaths.Count,
                x => x.CorrespondingFolderPaths.Count,
                (mugshotCount, _) =>
                {
                    bool mugshotIsEmpty = mugshotCount == 0;
                    // Check if ANY path in the CorrespondingFolderPaths collection is assigned AND exists.
                    // If any such path exists, the item cannot be deleted.
                    bool anyModPathExists = CorrespondingFolderPaths.Any(path =>
                        !string.IsNullOrWhiteSpace(path) && System.IO.Directory.Exists(path)
                    );
                    return mugshotIsEmpty && !anyModPathExists;
                }
            )
            .ToPropertyEx(this, x => x.CanDelete)
            .DisposeWith(_disposables);
        
        this.WhenAnyValue(x => x.CopyAssets)
            .Skip(1) // Skip initial value
            .Where(isChecked => !isChecked) // Only trigger when unchecked
            .Subscribe(_ =>
            {
                if (!_isLoadingFromModel && !IsPerformingBatchAction && !_parentVm.SuppressPopupWarnings)
                {
                    const string message =
                        "Disabling asset copying means only FaceGen files (.nif/.dds) will be transferred.\n\n" +
                        "It becomes your responsibility to ensure that all other required assets (meshes, textures for armor, hair, eyes, etc.) are still available, though you can disable or hide the source mod plugins.\n\n" +
                        "Are you sure you want to disable asset copying for this mod?";

                    if (!ScrollableMessageBox.Confirm(message, "Confirm Disable Asset Copying"))
                    {
                        // Revert if user cancels
                        Observable.Timer(TimeSpan.FromMilliseconds(1), RxApp.MainThreadScheduler)
                            .Subscribe(__ => { CopyAssets = true; });
                    }
                }
            })
            .DisposeWith(_disposables);
        
        this.WhenAnyValue(x => x.Keywords)
            .Select(keywords =>
            {
                if (keywords is null)
                    return Observable.Return(DefaultKeywordsToolTip);

                return Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                        h => keywords.CollectionChanged += h,
                        h => keywords.CollectionChanged -= h)
                    .StartWith((EventPattern<NotifyCollectionChangedEventArgs>?)null)
                    .Select(_ => keywords.Count > 0 
                        ? $"Keywords: {string.Join(", ", keywords)}" 
                        : DefaultKeywordsToolTip);
            })
            .Switch()
            .ToPropertyEx(this, x => x.KeywordsToolTip, initialValue: DefaultKeywordsToolTip, scheduler: RxApp.MainThreadScheduler)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.BaseGameAssetPathCount)
            .Select(BuildOverwriteBaseGameAssetsToolTip)
            .ToPropertyEx(this, x => x.OverwriteBaseGameAssetsToolTip,
                initialValue: BuildOverwriteBaseGameAssetsToolTip(0), scheduler: RxApp.MainThreadScheduler)
            .DisposeWith(_disposables);

        // --- Command Initializations ---
        AddFolderPathCommand = ReactiveCommand.CreateFromTask(AddFolderPathAsync).DisposeWith(_disposables);
        BrowseFolderPathCommand = ReactiveCommand.CreateFromTask<string>(BrowseFolderPathAsync).DisposeWith(_disposables);
        RemoveFolderPathCommand = ReactiveCommand.Create<string>(RemoveFolderPath).DisposeWith(_disposables);
        AddMugshotFolderPathCommand = ReactiveCommand.CreateFromTask(AddMugshotFolderPathAsync).DisposeWith(_disposables);
        BrowseMugshotFolderPathCommand =
            ReactiveCommand.CreateFromTask<string>(BrowseMugshotFolderPathAsync).DisposeWith(_disposables);
        RemoveMugshotFolderPathCommand =
            ReactiveCommand.Create<string>(RemoveMugshotFolderPath).DisposeWith(_disposables);
        UnlinkMugshotDataCommand = ReactiveCommand
            .CreateFromTask(UnlinkMugshotDataAsync, this.WhenAnyValue(x => x.CanUnlinkMugshots)).DisposeWith(_disposables);
        UnlinkMugshotDataCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error unlinking mugshot data: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        SetResourcePluginsCommand = ReactiveCommand.Create(SetResourcePlugins).DisposeWith(_disposables);
        SetResourcePluginsCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error refreshing mod '{DisplayName}': {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        SetKeywordsCommand = ReactiveCommand.Create(SetKeywords).DisposeWith(_disposables);
        SetKeywordsCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error setting keywords for mod '{DisplayName}': {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        DeleteCommand = ReactiveCommand.Create(Delete, this.WhenAnyValue(x => x.CanDelete)).DisposeWith(_disposables);
        DeleteCommand.ThrownExceptions.Subscribe(ex => Debug.WriteLine($"Error executing DeleteCommand: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync).DisposeWith(_disposables);
        RefreshCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error refreshing mod '{DisplayName}': {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.CorrespondingFolderPaths.Count)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(CanUnlinkMugshots)))
            .DisposeWith(_disposables);
        ; // Manually trigger update if needed, though WhenAnyValue should handle it

        this.WhenAnyValue(x => x.OverrideRecordOverrideHandlingMode)
            .Skip(1) // Skip initial value when loading from model
            .Subscribe(mode =>
            {
                // A non-default mode has been selected.
                if (!_isLoadingFromModel &&
                    !IsPerformingBatchAction &&
                    !_parentVm.SuppressPopupWarnings &&
                    mode.HasValue &&
                    mode.Value != RecordOverrideHandlingMode.Ignore)
                {
                    // Note: To implement the "Don't show me popup warnings" setting,
                    // a check against that global setting would be added here.

                    const string message =
                        "WARNING: Setting the override handling mode to anything other than 'Default' is generally not recommended.\n\n" +
                        "It can significantly increase patching time and is only necessary in very specific, rare scenarios.\n\n" +
                        "Only enable override handling for plugins if you see they need it via SSEedit, or if troubleshooting an NPC with bugged appearance.\n\n" +
                        "Are you sure you want to change this setting for this specific mod?";

                    if (!ScrollableMessageBox.Confirm(message, "Confirm Override Handling Mode"))
                    {
                        // If user clicks "No", schedule a reversion to the default (null) value.
                        // This runs on the UI thread after a short delay to allow the ComboBox to process the initial selection.
                        Observable.Timer(TimeSpan.FromMilliseconds(1), RxApp.MainThreadScheduler)
                            .Subscribe(_ => { OverrideRecordOverrideHandlingMode = null; });
                    }
                }
            })
            .DisposeWith(_disposables);
        ;

        // Convert To Headparts is experimental — confirm before enabling it for
        // THIS mod (mirrors the override-handling-mode confirmation above).
        // Buffer(2,1) yields (previous, current) pairs starting with the first
        // change after the model load, and a decline reverts to the previous
        // selection (usually "Default") rather than always to null.
        this.WhenAnyValue(x => x.OverrideWigHandlingMode)
            .Buffer(2, 1)
            .Subscribe(pair =>
            {
                var previous = pair[0];
                var current = pair[1];
                if (current != WigHandlingMode.ConvertToHeadParts ||
                    previous == WigHandlingMode.ConvertToHeadParts ||
                    _isLoadingFromModel ||
                    IsPerformingBatchAction ||
                    _parentVm.SuppressPopupWarnings)
                {
                    return;
                }

                if (!ScrollableMessageBox.Confirm(HandlingModeDisplay.ConvertToHeadPartsWarning,
                        HandlingModeDisplay.ConvertToHeadPartsWarningTitle, MessageBoxImage.Warning))
                {
                    // Revert on the UI thread after a short delay so the ComboBox
                    // finishes processing the selection first.
                    Observable.Timer(TimeSpan.FromMilliseconds(1), RxApp.MainThreadScheduler)
                        .Subscribe(_ => { OverrideWigHandlingMode = previous; });
                }
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.IncludeOutfits)
            .Skip(1) // Skip the initial value loaded from the model
            .Where(isChecked => isChecked) // Only trigger when the box is checked (i.e., set to true)
            .Subscribe(_ =>
            {
                // Only show the popup if warnings are not suppressed
                if (!_isLoadingFromModel &&
                    !IsPerformingBatchAction &&
                    !_parentVm.SuppressPopupWarnings)
                {
                    const string message =
                        "Modifying NPC outfits on an existing save can lead to NPCs unequipping their outifts entirely. Are you sure you want to enable outfit modification?";

                    if (!ScrollableMessageBox.Confirm(message, "Confirm Outfit Forwarding"))
                    {
                        // If the user clicks "No", revert the checkbox state to false.
                        // The timer prevents UI contention with the checkbox's state.
                        Observable.Timer(TimeSpan.FromMilliseconds(1), RxApp.MainThreadScheduler)
                            .Subscribe(__ => { IncludeOutfits = false; });
                    }
                }
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.HandleInjectedRecords)
            .Skip(1) // Skip the initial value loaded from the model
            .Where(isChecked => isChecked) // Only trigger when the box is checked (i.e., set to true)
            .Subscribe(_ =>
            {
                // Only show the popup if warnings are not suppressed
                if (!_isLoadingFromModel &&
                    !IsPerformingBatchAction &&
                    !_parentVm.SuppressPopupWarnings)
                {
                    const string message =
                        "Searching for injected records makes patching take longer, and most appearance mods don't need it. Are you sure you want to enable this?";

                    if (!ScrollableMessageBox.Confirm(message, "Confirm Injected Record Search"))
                    {
                        // If the user clicks "No", revert the checkbox state to false.
                        // The timer prevents UI contention with the checkbox's state.
                        Observable.Timer(TimeSpan.FromMilliseconds(1), RxApp.MainThreadScheduler)
                            .Subscribe(__ => { HandleInjectedRecords = false; });
                    }
                }
            })
            .DisposeWith(_disposables);
        
        this.WhenAnyValue(x => x.OverrideRecordOverrideHandlingMode)
            .Subscribe(_ => UpdateIsMaxNestedIntervalDepthVisible())
            .DisposeWith(_disposables);

        _lazySettingsVm.Value.WhenAnyValue(x => x.SelectedRecordOverrideHandlingMode)
            .Subscribe(_ => UpdateIsMaxNestedIntervalDepthVisible())
            .DisposeWith(_disposables);

        // Wig / Antler dropdown visibility: each dropdown shows only when the
        // analysis scan detected that class AND the output mode activates the
        // handling — Create-and-Patch record mode, or SkyPatcher mode in either
        // PatchingMode (mirrors Settings.GetEffectiveWigMode/GetEffectiveAntlerMode).
        // Wigs and antlers are gated on their own detection sets (not the union),
        // so a mod with antlers-only shows only the Antler dropdown and vice versa.
        this.WhenAnyValue(x => x.HasWigArmors)
            .CombineLatest(
                _lazySettingsVm.Value.WhenAnyValue(x => x.SelectedPatchingMode, x => x.UseSkyPatcherMode),
                (hasWigs, output) => hasWigs &&
                                     (output.Item2 || output.Item1 == PatchingMode.CreateAndPatch))
            .Subscribe(visible => IsWigHandlingVisible = visible)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.HasAntlers)
            .CombineLatest(
                _lazySettingsVm.Value.WhenAnyValue(x => x.SelectedPatchingMode, x => x.UseSkyPatcherMode),
                (hasAntlers, output) => hasAntlers &&
                                        (output.Item2 || output.Item1 == PatchingMode.CreateAndPatch))
            .Subscribe(visible => IsAntlerHandlingVisible = visible)
            .DisposeWith(_disposables);

        // Two-way sync between the persisted nullable modes and their
        // SelectedItem bridges (see the bridge properties for why SelectedValue
        // can't be bound directly). Mode → item registers FIRST so each bridge
        // is initialized from the current mode before its own writer attaches;
        // Fody's if-changed setters terminate the ping-pong (KeyValuePair is a
        // struct with structural equality). Programmatic writes (model load,
        // batch actions, the decline-popup revert to null) flow into the
        // display, and user selections flow back through the same
        // OverrideRecordOverrideHandlingMode / OverrideWigHandlingMode
        // properties, so the existing confirmation-popup subscriptions keep
        // working unchanged.
        this.WhenAnyValue(x => x.OverrideRecordOverrideHandlingMode)
            .Subscribe(mode => SelectedRecordOverrideHandlingItem =
                RecordOverrideHandlingModes.First(kv => kv.Key == mode))
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SelectedRecordOverrideHandlingItem)
            .Subscribe(item => OverrideRecordOverrideHandlingMode = item.Key)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.OverrideWigHandlingMode)
            .Subscribe(mode => SelectedWigHandlingItem =
                WigHandlingModes.First(kv => kv.Key == mode))
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SelectedWigHandlingItem)
            .Subscribe(item => OverrideWigHandlingMode = item.Key)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.OverrideAntlerHandlingMode)
            .Subscribe(mode => SelectedAntlerHandlingItem =
                AntlerHandlingModes.First(kv => kv.Key == mode))
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SelectedAntlerHandlingItem)
            .Subscribe(item => OverrideAntlerHandlingMode = item.Key)
            .DisposeWith(_disposables);
        
        this.WhenAnyValue(x => x.IncludeAllOverrides)
            .Skip(1) // Skip initial value
            .Where(isChecked => isChecked) // Only trigger when checked
            .Subscribe(_ =>
            {
                if (!_isLoadingFromModel && !IsPerformingBatchAction && !_parentVm.SuppressPopupWarnings)
                {
                    const string message =
                        "WARNING: The 'Include All' option will grab ALL override records from the selected plugins, " +
                        "not just those linked to the NPCs being processed.\n\n" +
                        "This method might include overrides that aren't relevant to the NPCs being selected.\n\n" +
                        "This option should only be used if:\n" +
                        "• You are selecting ALL NPCs in this mod, OR\n" +
                        "• As a fallback if you can't set the right Max Nested Search Layers without your computer running out of memory and crashing.\n\n" +
                        "Are you sure you want to enable this option?";

                    if (!ScrollableMessageBox.Confirm(message, "Confirm Include All Overrides"))
                    {
                        // Revert if user cancels
                        Observable.Timer(TimeSpan.FromMilliseconds(1), RxApp.MainThreadScheduler)
                            .Subscribe(__ => { IncludeAllOverrides = false; });
                    }
                }
                UpdateIsMaxNestedIntervalDepthVisible();
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.IncludeAllOverrides)
            .Skip(1)
            .Where(isChecked => !isChecked) // When unchecked, just update visibility
            .Subscribe(_ => UpdateIsMaxNestedIntervalDepthVisible())
            .DisposeWith(_disposables);

        UpdateIsMaxNestedIntervalDepthVisible();
    }

    private static string BuildOverwriteBaseGameAssetsToolTip(int count)
    {
        return $"This mod contains {count} asset file(s) at the same paths as base game assets (e.g. body/skin textures)." +
               Environment.NewLine +
               "If unchecked (default), those files are NOT copied to the output, so the versions from your other installed mods (e.g. skin replacers) stay in effect." +
               Environment.NewLine +
               "If checked, this mod's versions are copied into the output and will override them game-wide (not just for this mod's NPCs)." +
               Environment.NewLine +
               "Note: leaving this unchecked can make skin textures in game differ slightly from the mugshot." +
               Environment.NewLine +
               "FaceGen files are unaffected by this setting and are always copied.";
    }

    /// <summary>
    /// Detects wig/antler armors in this mod's loaded plugins and stores the
    /// result on the VM (persisted via <see cref="SaveToModel"/>). Runs only
    /// on analysis cache misses — same lifecycle as the base-game-asset scan —
    /// so unchanged mods keep their persisted detection across launches.
    /// ARMA links the mod inherits from masters resolve through the
    /// load-order link cache.
    /// </summary>
    public void ScanForWigs(IReadOnlyCollection<ISkyrimModGetter> plugins)
    {
        var scan = WigDetector.Scan(plugins, formKey =>
            _environmentStateProvider.LinkCache != null &&
            _environmentStateProvider.LinkCache.TryResolve<IArmorAddonGetter>(formKey, out var arma)
                ? arma
                : null);
        DetectedWigArmors = scan.Wigs;
        DetectedAntlerArmors = scan.AntlerArmors;
        DetectedAntlerArmatures = scan.AntlerArmatures;
        DetectedAntlerHeadParts = scan.AntlerHeadParts;
        HasWigArmors = scan.Wigs.Count > 0;
        RecomputeHasAntlers();
    }

    /// <summary>HasAntlers = scan-detected antlers (any source) OR user-designated
    /// manual antler head parts for this mod. Recomputed after detection changes
    /// and after a manual designation via the 3D preview selector (so the Antler
    /// Handling dropdown appears for a manual-only mod without a restart).</summary>
    public void RecomputeHasAntlers()
    {
        HasAntlers = DetectedAntlerArmors.Count > 0 || DetectedAntlerArmatures.Count > 0 ||
                     DetectedAntlerHeadParts.Count > 0 ||
                     _parentVm.HasManualAntlerHeadParts(DisplayName);
    }

    public ModSetting SaveToModel()
    {
        var model = new Models.ModSetting
        {
            DisplayName = DisplayName,
            CorrespondingModKeys = CorrespondingModKeys.ToList(),
            ResourceOnlyModKeys = ResourceOnlyModKeys.ToHashSet(),
            // Important: Create new lists/collections when saving to the model
            // to avoid potential issues with shared references if the VM is reused.
            CorrespondingFolderPaths = CorrespondingFolderPaths.ToList(),
            MugShotFolderPaths = MugShotFolderPaths.ToList(), // Save the mugshot folder path
            NpcPluginDisambiguation = new Dictionary<FormKey, ModKey>(NpcPluginDisambiguation),
            AvailablePluginsForNpcs = new Dictionary<FormKey, List<ModKey>>(AvailablePluginsForNpcs),
            NpcFormKeys = this.NpcFormKeys,
            NpcFormKeysToDisplayName = this.NpcFormKeysToDisplayName,
            AmbiguousNpcFormKeys = this.AmbiguousNpcFormKeys,
            IsFaceGenOnlyEntry = IsFaceGenOnlyEntry,
            FaceGenOnlyNpcFormKeys = FaceGenOnlyNpcFormKeys,
            MergeInDependencyRecords = MergeInDependencyRecords,
            IncludeOutfits = IncludeOutfits,
            CopyAssets = CopyAssets,
            OverwriteBaseGameAssets = OverwriteBaseGameAssets,
            HasBaseGameAssetPaths = HasBaseGameAssetPaths,
            BaseGameAssetPathCount = BaseGameAssetPathCount,
            MergeInToolTip = MergeInToolTip,
            HasAlteredMergeLogic = HasAlteredMergeLogic,
            HandleInjectedRecords = HandleInjectedRecords,
            HasAlteredHandleInjectedRecordsLogic = HasAlteredHandleInjectedRecordsLogic,
            HandleInjectedOverridesToolTip = HandleInjectedOverridesToolTip,
            ModRecordOverrideHandlingMode = OverrideRecordOverrideHandlingMode,
            MaxNestedIntervalDepth = MaxNestedIntervalDepth,
            DetectedWigArmors = new HashSet<FormKey>(DetectedWigArmors),
            DetectedAntlerArmors = new HashSet<FormKey>(DetectedAntlerArmors),
            DetectedAntlerArmatures = new HashSet<FormKey>(DetectedAntlerArmatures),
            DetectedAntlerHeadParts = new HashSet<FormKey>(DetectedAntlerHeadParts),
            ModWigHandlingMode = OverrideWigHandlingMode,
            ModAntlerHandlingMode = OverrideAntlerHandlingMode,
            IsAutoGenerated = IsAutoGenerated,
            PluginsWithOverrideRecords = _pluginsWithOverrideRecords,
            IncludeAllOverrides = IncludeAllOverrides,
            NpcFormKeysToNotifications = NpcFormKeysToNotifications,
            Keywords = new HashSet<string>(Keywords),
            LastKnownState = LastKnownState
        };
        return model;
    }

    // --- Command Implementations ---

    private async Task AddFolderPathAsync()
    {
        using (var dialog = new FolderBrowserDialog())
        {
            dialog.Description = $"Select a corresponding folder for {DisplayName}";
            string initialPath = _parentVm.ModsFolderSetting;
            if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
            {
                initialPath = CorrespondingFolderPaths.FirstOrDefault(p => Directory.Exists(p)) ??
                              Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            dialog.SelectedPath = initialPath;

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                string addedPath = dialog.SelectedPath;
                if (!CorrespondingFolderPaths.Contains(addedPath, StringComparer.OrdinalIgnoreCase))
                {
                    // Store state *before* modification for merge check
                    bool hadMugshotBefore = HasMugshotPathAssigned;
                    bool hadModPathsBefore = HasModPathsAssigned;

                    CorrespondingFolderPaths.Add(addedPath); // Modify the collection

                    AutoSetResourcePlugins(addedPath);

                    // *** Notify parent VM AFTER path is added ***
                    await _parentVm.CheckForAndPerformMergeAsync(this, addedPath, PathType.ModFolder, hadMugshotBefore,
                        hadModPathsBefore);
                }
            }
        }
    }

    private async Task BrowseFolderPathAsync(string existingPath)
    {
        using (var dialog = new FolderBrowserDialog())
        {
            dialog.Description = $"Change corresponding folder for {DisplayName}";
            dialog.SelectedPath = Directory.Exists(existingPath) ? existingPath : _parentVm.ModsFolderSetting;
            if (string.IsNullOrWhiteSpace(dialog.SelectedPath) || !Directory.Exists(dialog.SelectedPath))
            {
                dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                string newPath = dialog.SelectedPath;
                int index = CorrespondingFolderPaths.IndexOf(existingPath);

                // Check if path actually changed and isn't already in the list elsewhere
                if (index >= 0 &&
                    !newPath.Equals(existingPath, StringComparison.OrdinalIgnoreCase) &&
                    !CorrespondingFolderPaths.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                {
                    // Store state *before* modification for merge check
                    bool hadMugshotBefore = HasMugshotPathAssigned;
                    bool hadModPathsBefore =
                        CorrespondingFolderPaths.Count > 0; // Had paths if count was > 0 before change

                    CorrespondingFolderPaths[index] = newPath; // Modify the collection

                    AutoSetResourcePlugins(newPath);

                    // *** Notify parent VM AFTER path is changed ***
                    await _parentVm.CheckForAndPerformMergeAsync(this, newPath, PathType.ModFolder, hadMugshotBefore,
                        hadModPathsBefore);
                }
                else if (index >= 0 && newPath.Equals(existingPath, StringComparison.OrdinalIgnoreCase))
                {
                    /* No change needed */
                }
                else if (index >= 0) // Path didn't change but new path already exists elsewhere
                {
                    ScrollableMessageBox.ShowWarning(
                        $"Cannot change path. The new path '{newPath}' already exists in the list.", "Browse Error");
                }
                else // index < 0
                {
                    ScrollableMessageBox.ShowWarning(
                        $"Cannot change path. The original path '{existingPath}' was not found.", "Browse Error");
                }
            }
        }
    }

    private void RemoveFolderPath(string pathToRemove)
    {
        // Removing a path doesn't trigger the merge check.
        if (CorrespondingFolderPaths.Contains(pathToRemove))
        {
            CorrespondingFolderPaths.Remove(pathToRemove);
        }
        
        RefreshCommand.Execute().Subscribe();
    }

    private static bool HasAnyAssignedPath(IReadOnlyCollection<string>? paths) =>
        paths != null && paths.Any(p =>
            !string.IsNullOrWhiteSpace(
                p?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

    // Helper: turn an OC<string> into a change stream that fires initially and on add/remove/reset
    IObservable<Unit> ObserveItems(ObservableCollection<string>? oc) =>
        oc is null
            ? Observable.Return(Unit.Default)
            : Observable
                .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    h => oc.CollectionChanged += h,
                    h => oc.CollectionChanged -= h)
                .StartWith((EventPattern<NotifyCollectionChangedEventArgs>?)null)
                .Select(_ => Unit.Default);

    // Helper: safely get folder names from paths (trim trailing slashes, ignore invalid/blank)
    HashSet<string> SafeFolderNames(IEnumerable<string>? paths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths is null) return set;

        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                var name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(name))
                    set.Add(name);
            }
            catch (ArgumentException)
            {
                /* ignore invalid path */
            }
        }

        return set;
    }

    public void UpdateCorrespondingModKeys(IEnumerable<ModKey>? explicitModKeys = null)
    {
        // For auto-generated mods, keys are set explicitly and should not be
        // recalculated based on folder paths. If this method is called
        // without explicit keys (e.g., from the folder path subscription),
        // we should not modify the existing keys.
        if (IsAutoGenerated && explicitModKeys == null)
        {
            return;
        }

        List<ModKey> correspondingModKeys = new();
        if (IsAutoGenerated)
        {
            if (explicitModKeys != null)
            {
                correspondingModKeys.AddRange(explicitModKeys);
            }
        }

        else
        {
            foreach (var path in CorrespondingFolderPaths)
            {
                correspondingModKeys.AddRange(_aux.GetModKeysInDirectory(path, new(), false));
            }
        }

        CorrespondingModKeys.Clear();
        CorrespondingModKeys.AddRange(correspondingModKeys);

        RecomputeResourceOnlyPlugins();
    }

    private async Task AddMugshotFolderPathAsync()
    {
        var selectedMugshotPath = SelectMugshotFolderPath(null);
        if (selectedMugshotPath != null)
        {
            // Store state *before* modification for merge check
            bool hadMugshotBefore = HasMugshotPathAssigned;
            bool hadModPathsBefore = HasModPathsAssigned;

            MugShotFolderPaths.Add(selectedMugshotPath); // Modify the property

            // *** Notify parent VM AFTER path is set ***
            await _parentVm.CheckForAndPerformMergeAsync(this, selectedMugshotPath, PathType.MugshotFolder, hadMugshotBefore,
                hadModPathsBefore);
        }
    }

    private async Task BrowseMugshotFolderPathAsync(string currentPath)
    {
        var selectedMugshotPath = SelectMugshotFolderPath(null);
        if (selectedMugshotPath != null)
        {
            // Store state *before* modification for merge check
            bool hadMugshotBefore = HasMugshotPathAssigned;
            bool hadModPathsBefore = HasModPathsAssigned;

            MugShotFolderPaths.Replace(currentPath, selectedMugshotPath); // Modify the property

            // *** Notify parent VM AFTER path is set ***
            await _parentVm.CheckForAndPerformMergeAsync(this, selectedMugshotPath, PathType.MugshotFolder, hadMugshotBefore,
                hadModPathsBefore);
        }
    }

    private void RemoveMugshotFolderPath(string pathToRemove)
    {
        if (MugShotFolderPaths.Contains(pathToRemove))
        {
            MugShotFolderPaths.Remove(pathToRemove);
        }
        
        RefreshCommand.Execute().Subscribe(); 
    }

    private string? SelectMugshotFolderPath(string? existingPath)
    {
        string? selectedPath = null;
        using (var dialog = new FolderBrowserDialog())
        {
            dialog.Description = $"Select the Mugshot Folder for {DisplayName}";
            var initialPath = _parentVm.MugshotsFolderSetting;
            if (existingPath != null)
            {
                initialPath = existingPath;
            }

            if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
            {
                initialPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            dialog.SelectedPath = initialPath;

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                string newPath = dialog.SelectedPath;
                if (Directory.Exists(newPath) &&
                    !MugShotFolderPaths.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                {
                    selectedPath = newPath;
                }
                else if (!Directory.Exists(newPath))
                {
                    ScrollableMessageBox.ShowWarning($"The selected folder does not exist: '{newPath}'",
                        "Browse Error");
                }
                else if (MugShotFolderPaths.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                {
                    ScrollableMessageBox.ShowWarning($"The selected folder is already listed for this Mod: '{newPath}'",
                        "Browse Error");
                }
            }
        }

        return selectedPath;
    }

    // --- Other Methods ---

    /// <summary>
    /// Reads associated plugin files (based on CorrespondingModKey and CorrespondingFolderPaths)
    /// and populates the NPC lists (NpcNames, NpcEditorIDs, NpcFormKeys, NpcFormKeysToDisplayName).
    /// Should typically be run asynchronously during initial load or after significant changes.
    /// </summary>
    public void RefreshNpcLists(Dictionary<string, HashSet<string>> allFaceGenLooseFiles,
        Dictionary<string, HashSet<string>> allFaceGenBsaFiles,
        HashSet<ISkyrimModGetter> plugins,
        Language? language)
    {
        var dbgTag = $"[RefreshNpcLists:{DisplayName}]";
        Debug.WriteLine($"{dbgTag} Entering. plugins={plugins.Count}, IsFaceGenOnlyEntry={IsFaceGenOnlyEntry}, IsMugshotOnlyEntry={IsMugshotOnlyEntry}, IsAutoGenerated={IsAutoGenerated}, HasModPathsAssigned={HasModPathsAssigned}, ResourceOnly=[{string.Join(", ", ResourceOnlyModKeys.Select(k => k.FileName))}]");

        NpcNames.Clear();
        NpcEditorIDs.Clear();
        NpcFormKeys.Clear();
        NpcFormKeysToDisplayName.Clear();
        AvailablePluginsForNpcs.Clear();
        // AmbiguousNpcFormKeys is cleared and repopulated below

        List<string> rejectionMessages = new();

        if (CorrespondingModKeys.Any() && (HasModPathsAssigned || IsAutoGenerated) && !IsFaceGenOnlyEntry)
        {
            // load plugins for downstream parsing
            Dictionary<ModKey, Dictionary<FormKey, IRaceGetter>> raceGetterCache = new();

            // Every NPC FormKey that has a plugin record in this mod (accepted or rejected).
            // Used below to detect FaceGen files the mod ships WITHOUT a plugin record.
            HashSet<FormKey> recordBackedNpcKeys = new();

            using (ContextualPerformanceTracer.Trace("RefreshNpcLists.MainLoop"))
            {
                try
                {
                    StartupLogger.Log($"  [{DisplayName}] Caching plugin races");
                    using (ContextualPerformanceTracer.Trace("RefreshNpcLists.CachePluginRaces"))
                    {
                        // If the plugin is not in the load order, cache its race to pass to downstream evaluation function so it doesn't waste time searching the main link cache for a FormKey that was never there
                        var loadOrderPlugins =
                            _environmentStateProvider.LoadOrderModKeys
                                .ToHashSet(); // source is IEnumerable; makes sure to convert.
                        foreach (var plugin in plugins)
                        {
                            foreach (var race in plugin.Races)
                            {
                                if (!loadOrderPlugins.Contains(race.FormKey.ModKey))
                                {
                                    raceGetterCache.TryAdd(plugin.ModKey, new Dictionary<FormKey, IRaceGetter>());
                                    raceGetterCache[plugin.ModKey].TryAdd(race.FormKey, race);
                                }
                            }
                        }
                    }

                    foreach (var plugin in plugins)
                    {
                        // Skip plugins marked as resource-only
                        if (ResourceOnlyModKeys.Contains(plugin.ModKey))
                        {
                            // Resource-only means "don't offer this plugin's NPCs", not "its
                            // FaceGen is orphaned": its records still count as record-backed so
                            // the FaceGen-only leftover pass below doesn't misread FaceGen shipped
                            // in dependency/resource folders (e.g. a CotR overhaul listing its
                            // base mods' folders as corresponding paths) as record-less NPCs.
                            foreach (var npcGetter in plugin.Npcs)
                            {
                                recordBackedNpcKeys.Add(npcGetter.FormKey);
                            }

                            Debug.WriteLine($"{dbgTag} SKIP plugin {plugin.ModKey.FileName} (resource-only); {plugin.Npcs.Count} NPC(s) ignored.");
                            continue;
                        }

                        StartupLogger.Log($"  [{DisplayName}] Processing plugin: {plugin.ModKey.FileName} ({plugin.Npcs.Count} NPCs)");
                        int acceptedFromPlugin = 0;
                        int rejectedFromPlugin = 0;
                        foreach (var npcGetter in plugin.Npcs)
                        {
                            recordBackedNpcKeys.Add(npcGetter.FormKey);

                            using (ContextualPerformanceTracer.Trace("RefreshNpcLists.RaceChecks"))
                            {
                                IRaceGetter? sourcePluginRace = null;
                                // first check and see if the race has been cached for the current plugin
                                if (raceGetterCache.TryGetValue(plugin.ModKey, out var samePluginEntry) &&
                                    samePluginEntry.TryGetValue(npcGetter.Race.FormKey, out var cachedRace))
                                {
                                    sourcePluginRace = cachedRace;
                                }
                                else
                                {
                                    foreach (var entry in raceGetterCache.Values)
                                    {
                                        if (entry.TryGetValue(npcGetter.Race.FormKey, out var cachedRace2))
                                        {
                                            sourcePluginRace = cachedRace2;
                                        }
                                    }
                                }

                                if (!_aux.IsValidAppearanceRace(npcGetter.Race.FormKey, npcGetter,
                                        language,
                                        out string rejectionMessage,
                                        out _,
                                        sourcePluginRace:
                                        sourcePluginRace)) // if sourcePluginRace is null, falls back to searching the link cache
                                {
                                    string message =
                                        $"Discarded {Auxilliary.GetLogString(npcGetter, language)} from {DisplayName} because {rejectionMessage}.";
                                    //Debug.WriteLine(message);
                                    rejectionMessages.Add(message);
                                    rejectedFromPlugin++;
                                    continue;
                                }
                            }

                            FormKey currentNpcKey = npcGetter.FormKey;
                            bool hasFaceGen = false;
                            bool isValidTemplatedNpc = Auxilliary.IsValidTemplatedNpc(npcGetter);

                            using (ContextualPerformanceTracer.Trace("RefreshNpcLists.FaceGenCheck"))
                            {
                                // This is the cache of BSA files relevant to the *current mod setting*
                                allFaceGenBsaFiles.TryGetValue(this.DisplayName, out var currentBsaCache);
                                hasFaceGen = FaceGenExists(currentNpcKey, allFaceGenLooseFiles,
                                    currentBsaCache ?? new HashSet<string>());

                                if (!hasFaceGen &&
                                    !isValidTemplatedNpc)
                                {
                                    string message =
                                        $"Discarded {Auxilliary.GetLogString(npcGetter, language, true)} from {DisplayName} because it has no FaceGen and does not use a template.";
                                    //Debug.WriteLine(message);
                                    rejectionMessages.Add(message);
                                    rejectedFromPlugin++;
                                    continue;
                                }
                            }

                            // --- Leveled NPC Template Chain Check ---
                            // NPCs whose template chain terminates in a Leveled NPC
                            // cannot have a unique appearance selected, so exclude them.
                            if (isValidTemplatedNpc &&
                                _aux.TemplateChainTerminatesInLeveledNpc(npcGetter, plugins))
                            {
                                string message =
                                    $"Discarded {Auxilliary.GetLogString(npcGetter, language, true)} from {DisplayName} because its template chain terminates in a Leveled NPC.";
                                //Debug.WriteLine(message);
                                rejectionMessages.Add(message);
                                rejectedFromPlugin++;
                                continue;
                            }

                            if (!AvailablePluginsForNpcs.TryGetValue(currentNpcKey, out var sourceList))
                            {
                                sourceList = new List<ModKey>();
                                AvailablePluginsForNpcs[currentNpcKey] = sourceList;
                            }

                            if (!sourceList.Contains(plugin.ModKey))
                            {
                                sourceList.Add(plugin.ModKey);
                            }
                            acceptedFromPlugin++;

                            using (ContextualPerformanceTracer.Trace("RefreshNpcLists.TemplateCheck"))
                            {
                                if (isValidTemplatedNpc)
                                {
                                    string templateStr = npcGetter.Template.FormKey.ToString();
                                    string issueMessage =
                                        $"his NPC from {plugin.ModKey.FileName} has the Traits flag so it inherits appearance from {templateStr}. Regardless of which mod you select here, if you see the Template icon, it'll use the appearance of the template.";
                                    if (hasFaceGen)
                                    {
                                        issueMessage = "Despite having FaceGen files, t" + issueMessage;
                                    }
                                    else
                                    {
                                        issueMessage = "T" + issueMessage;
                                    }

                                    NpcFormKeysToNotifications[currentNpcKey] = (
                                        IssueType: NpcIssueType.Template,
                                        IssueMessage: issueMessage,
                                        FormKey: npcGetter.Template.FormKey);
                                }
                            }

                            using (ContextualPerformanceTracer.Trace("RefreshNpcLists.Finalization"))
                            {
                                if (!NpcFormKeys.Contains(currentNpcKey))
                                {
                                    NpcFormKeys.Add(currentNpcKey);
                                    var npc = npcGetter;
                                    string displayName = string.Empty;

                                    // plugins don't ship with translations. For the name, if localization is needed, resolve from the main load order
                                    if (language != null &&
                                        _environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcGetter,
                                            out var mainGetter)
                                        && Auxilliary.TryGetName(npc, language, _lazySettingsVm.Value.FixGarbledText, out string localizedName))
                                    {
                                        NpcNames.Add(localizedName);
                                        displayName = localizedName;
                                    }

                                    else if (Auxilliary.TryGetName(npc, language, _lazySettingsVm.Value.FixGarbledText, out string name))
                                    {
                                        NpcNames.Add(name);
                                        displayName = name;
                                    }

                                    if (!string.IsNullOrEmpty(npc.EditorID))
                                    {
                                        NpcEditorIDs.Add(npc.EditorID);
                                        if (string.IsNullOrEmpty(displayName)) displayName = npc.EditorID;
                                    }

                                    if (string.IsNullOrEmpty(displayName)) displayName = npc.FormKey.ToString();
                                    NpcFormKeysToDisplayName.Add(currentNpcKey, displayName);
                                }
                            }
                        }
                        Debug.WriteLine($"{dbgTag} Plugin {plugin.ModKey.FileName}: total={plugin.Npcs.Count}, accepted={acceptedFromPlugin}, rejected={rejectedFromPlugin}");
                    }
                }
                catch (Exception ex)
                {
                    string message =
                        $"Error loading NPC data for ModSetting '{DisplayName}': \n{ExceptionLogger.GetExceptionStack(ex)}";
                    Debug.WriteLine(message);
                    rejectionMessages.Add(message);
                }
            }

            // Mixed-mod FaceGen-only support: a plugin-backed appearance mod may ship FaceGen
            // files for NPCs it carries no plugin record for (deliberately or by accident).
            // Offer those NPCs as FaceGen-only: at patch time the record-resolution fallback
            // inherits the NPC's origin record and applies only this mod's FaceGen assets.
            // For plugin-backed mods FaceGenOnlyNpcFormKeys therefore holds just these
            // record-less leftovers (whole-mod FaceGen entries keep their full set, populated
            // externally and consumed by the IsFaceGenOnlyEntry branch below).
            using (ContextualPerformanceTracer.Trace("RefreshNpcLists.FaceGenOnlyLeftovers"))
            {
                FaceGenOnlyNpcFormKeys.Clear();

                // The synthetic Base Game / Creation Club entries own the vanilla BSAs; any
                // orphaned vanilla FaceGen must not surface as selectable FaceGen-only NPCs.
                if (!IsAutoGenerated)
                {
                    var faceGenScan =
                        FaceGenScanner.CreateFaceGenScanResultFromCache(this, allFaceGenLooseFiles,
                            allFaceGenBsaFiles);
                    foreach (var (pluginName, npcIds) in faceGenScan.FaceGenFiles)
                    {
                        foreach (var id in npcIds.Where(x => x.Length == 8))
                        {
                            if (FormKey.TryFactory($"{id.Substring(2, 6)}:{pluginName}", out var faceGenKey) &&
                                !recordBackedNpcKeys.Contains(faceGenKey))
                            {
                                FaceGenOnlyNpcFormKeys.Add(faceGenKey);
                            }
                        }
                    }
                }

                // Drop stale FaceGen-only notifications (NPC gained a record or lost its FaceGen).
                var staleFaceGenNotifications = NpcFormKeysToNotifications
                    .Where(x => x.Value.IssueType == NpcIssueType.FaceGenOnly &&
                                !FaceGenOnlyNpcFormKeys.Contains(x.Key))
                    .Select(x => x.Key)
                    .ToList();
                foreach (var staleKey in staleFaceGenNotifications)
                {
                    NpcFormKeysToNotifications.Remove(staleKey);
                }

                foreach (var currentNpcKey in FaceGenOnlyNpcFormKeys)
                {
                    if (NpcFormKeys.Contains(currentNpcKey)) continue;
                    NpcFormKeys.Add(currentNpcKey);

                    string displayName = string.Empty;
                    var contexts =
                        _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(currentNpcKey);
                    var sourceContext = contexts.LastOrDefault();
                    if (sourceContext is not null)
                    {
                        var npc = sourceContext.Record;
                        if (Auxilliary.TryGetName(npc, language, _lazySettingsVm.Value.FixGarbledText,
                                out string name))
                        {
                            NpcNames.Add(name);
                            displayName = name;
                        }

                        if (!string.IsNullOrEmpty(npc.EditorID))
                        {
                            NpcEditorIDs.Add(npc.EditorID);
                            if (string.IsNullOrEmpty(displayName)) displayName = npc.EditorID;
                        }
                    }

                    if (string.IsNullOrEmpty(displayName)) displayName = currentNpcKey.ToString();
                    NpcFormKeysToDisplayName[currentNpcKey] = displayName;

                    NpcFormKeysToNotifications[currentNpcKey] = (
                        IssueType: NpcIssueType.FaceGenOnly,
                        IssueMessage:
                        $"{DisplayName} provides FaceGen files for this NPC but no plugin record, even though other NPCs in this mod do have plugin records. " +
                        "Make sure the mod author didn't forget to include a record for this NPC. " +
                        $"If you select this appearance, the NPC record is inherited from {currentNpcKey.ModKey.FileName} (the NPC's original plugin) " +
                        "and only this mod's face mesh/textures are applied. " +
                        "If this mod's FaceGen was built against different head parts than that record, the face may mismatch in game.",
                        ReferencedFormKey: null);
                }

                if (FaceGenOnlyNpcFormKeys.Any())
                {
                    Debug.WriteLine(
                        $"{dbgTag} Added {FaceGenOnlyNpcFormKeys.Count} FaceGen-only NPC(s) without plugin records.");
                }
            }
        }

        else if (IsFaceGenOnlyEntry)
        {
            using (ContextualPerformanceTracer.Trace("RefreshNpcLists.FaceGenOnly"))
            {
                foreach (var currentNpcKey in FaceGenOnlyNpcFormKeys)
                {
                    NpcFormKeys.Add(currentNpcKey);
                    var contexts =
                        _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(currentNpcKey);
                    var sourceContext = contexts.LastOrDefault();
                    if (sourceContext is not null)
                    {
                        var npc = sourceContext.Record;
                        string displayName = string.Empty;
                        if (Auxilliary.TryGetName(npc, language, _lazySettingsVm.Value.FixGarbledText, out string name))
                        {
                            NpcNames.Add(name);
                            displayName = name;
                        }

                        if (!string.IsNullOrEmpty(npc.EditorID))
                        {
                            NpcEditorIDs.Add(npc.EditorID);
                            if (string.IsNullOrEmpty(displayName)) displayName = npc.EditorID;
                        }

                        if (string.IsNullOrEmpty(displayName)) displayName = npc.FormKey.ToString();
                        NpcFormKeysToDisplayName.Add(currentNpcKey, displayName);
                    }
                    else
                    {
                        NpcFormKeysToDisplayName.Add(currentNpcKey, currentNpcKey.ToString());
                    }
                }
            }
        }
        
        else if (IsMugshotOnlyEntry)
        {
            using (ContextualPerformanceTracer.Trace("RefreshNpcLists.MugshotOnly"))
            {
                foreach (var folder in MugShotFolderPaths)
                {
                    if (!Directory.Exists(folder)) continue;

                    try
                    {
                        var imageFiles = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                            .Where(f => VM_Mods.MugshotNameRegex.IsMatch(Path.GetFileName(f)));

                        foreach (var file in imageFiles)
                        {
                            var fileName = Path.GetFileName(file);
                            var match = VM_Mods.MugshotNameRegex.Match(fileName);
                            if (!match.Success) continue;

                            var directoryInfo = new DirectoryInfo(Path.GetDirectoryName(file)!);
                            var pluginName = directoryInfo.Name;
                            var hexPart = match.Groups["hex"].Value;

                            var tail6 = hexPart.Length >= 6 ? hexPart[^6..] : hexPart;
                            var formKeyString = $"{tail6}:{pluginName}";

                            if (FormKey.TryFactory(formKeyString, out var npcFormKey))
                            {
                                if (!NpcFormKeys.Contains(npcFormKey))
                                {
                                    NpcFormKeys.Add(npcFormKey);
                                    string displayName = string.Empty;

                                    // Attempt to resolve the NPC from the active Load Order to get real names
                                    if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey,
                                            out var existingNpc))
                                    {
                                        if (Auxilliary.TryGetName(existingNpc, language, _lazySettingsVm.Value.FixGarbledText, out string name))
                                        {
                                            NpcNames.Add(name);
                                            displayName = name;
                                        }

                                        if (!string.IsNullOrEmpty(existingNpc.EditorID))
                                        {
                                            NpcEditorIDs.Add(existingNpc.EditorID);
                                            if (string.IsNullOrEmpty(displayName)) displayName = existingNpc.EditorID;
                                        }
                                    }

                                    // Fallback if the NPC doesn't exist in the load order or resolution failed
                                    if (string.IsNullOrEmpty(displayName))
                                    {
                                        displayName = npcFormKey.ToString();
                                    }

                                    NpcFormKeysToDisplayName.Add(npcFormKey, displayName);

                                    // Always ensure the ID string is searchable
                                    NpcEditorIDs.Add(npcFormKey.IDString());
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error scanning mugshot-only folder {folder}: {ex.Message}");
                    }
                }

                NpcCount = NpcFormKeys.Count;
            }
        }

        // --- Post-Processing: Populate NpcPluginDisambiguation, identify AmbiguousNpcFormKeys ---
        using (ContextualPerformanceTracer.Trace("RefreshNpcLists.PostProcessing"))
        {
            AmbiguousNpcFormKeys.Clear(); // Clear before repopulating

            foreach (var kvp in AvailablePluginsForNpcs)
            {
                FormKey npcKey = kvp.Key;
                List<ModKey> sources = kvp.Value;

                if (sources.Count == 1)
                {
                    NpcPluginDisambiguation.Remove(npcKey); // Not ambiguous, no disambiguation needed
                }
                else if (sources.Count > 1)
                {
                    AmbiguousNpcFormKeys.Add(npcKey); // Mark as having multiple origins within this ModSetting

                    ModKey resolvedSource;
                    if (NpcPluginDisambiguation.TryGetValue(npcKey, out var preferredKey) &&
                        sources.Contains(preferredKey))
                    {
                        resolvedSource = preferredKey;
                    }
                    else
                    {
                        // No valid disambiguation or preferred key is no longer a source. Determine default.
                        var loadOrder = _environmentStateProvider?.LoadOrder;
                        if (loadOrder == null || loadOrder.ListedOrder == null)
                        {
                            Debug.WriteLine(
                                $"CRITICAL ERROR for ModSetting '{DisplayName}': Load order not available from EnvironmentStateProvider. Cannot resolve default source for NPC {npcKey}. This NPC will be skipped.");
                            continue;
                        }

                        var loadOrderList = loadOrder.ListedOrder.Select(x => x.ModKey).ToList();
                        ModKey? winner = null;

                        // 1) Check if all sources are in the load order. If so, use load order winner.
                        if (sources.All(s => !s.IsNull && loadOrderList.Contains(s)))
                        {
                            winner = sources
                                .OrderBy(s => loadOrderList.IndexOf(s))
                                .FirstOrDefault();
                        }

                        // 2) If not, find a winner based on deepest valid master dependency.
                        var candidates = new List<(ModKey plugin, int masterCount)>();
                        if (winner == null || winner.Value.IsNull)
                        {
                            foreach (var source in sources)
                            {
                                if (source.IsNull) continue;

                                var masters = _pluginProvider.GetMasterPlugins(source, CorrespondingFolderPaths);
                                bool allMastersValid = masters.All(m =>
                                    loadOrderList.Contains(m) || CorrespondingModKeys.Contains(m));

                                if (allMastersValid)
                                {
                                    candidates.Add((source, masters.Count));
                                }
                            }

                            if (candidates.Any())
                            {
                                var maxDepth = candidates.Max(c => c.masterCount);
                                var topCandidates = candidates.Where(c => c.masterCount == maxDepth).ToList();
                                if (topCandidates.Count == 1)
                                {
                                    winner = topCandidates.First().plugin;
                                }
                            }
                        }

                        // 3) Refined Fallback Logic
                        if (winner == null || winner.Value.IsNull)
                        {
                            // 3a) If there were valid candidates (all masters available) but they tied for depth,
                            // pick alphabetically from that list of valid candidates only.
                            if (candidates.Any())
                            {
                                winner = candidates
                                    .Select(c => c.plugin)
                                    .OrderBy(s => s.FileName.String, StringComparer.OrdinalIgnoreCase)
                                    .FirstOrDefault();
                            }
                            // 3b) If there were NO valid candidates at all (all had missing masters),
                            // then and only then pick alphabetically from all original sources.
                            else
                            {
                                winner = sources
                                    .Where(s => !s.IsNull)
                                    .OrderBy(s => s.FileName.String, StringComparer.OrdinalIgnoreCase)
                                    .FirstOrDefault();
                            }
                        }

                        // Assign the winning source
                        if (winner != null && !winner.Value.IsNull)
                        {
                            resolvedSource = winner.Value;
                            NpcPluginDisambiguation[npcKey] = resolvedSource; // Persist the default choice
                        }
                        else
                        {
                            var message =
                                $"ERROR for ModSetting '{DisplayName}': NPC {npcKey} found in multiple associated plugins: {string.Join(", ", sources.Select(k => k.FileName))}, but no valid default source could be determined. This NPC will be skipped for this Mod Setting.";
                            Debug.WriteLine(message);
                            rejectionMessages.Add(message);
                            continue;
                        }
                    }
                }
            }
        }

        // Cleanup NpcPluginDisambiguation: remove entries for NPCs no longer found or no longer ambiguous (i.e. now only in 1 plugin)
        var keysInDisambiguation =
            NpcPluginDisambiguation.Keys.ToList(); // ToList() for safe removal while iterating
        foreach (var npcKeyInDisambiguation in keysInDisambiguation)
        {
            if (!AvailablePluginsForNpcs.TryGetValue(npcKeyInDisambiguation, out var currentSources) ||
                currentSources.Count <= 1)
            {
                NpcPluginDisambiguation.Remove(npcKeyInDisambiguation);
            }
        }

        Debug.WriteLine($"{dbgTag} Done. NpcFormKeys={NpcFormKeys.Count}, AvailablePluginsForNpcs={AvailablePluginsForNpcs.Count}, AmbiguousNpcFormKeys={AmbiguousNpcFormKeys.Count}, rejectionMessages={rejectionMessages.Count}");

        if (rejectionMessages.Any())
        {
            try
            {
                string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Rejected NPCs");
                Auxilliary.CreateDirectoryIfNeeded(logDirectory, Auxilliary.PathType.Directory);
                string safeDisplayName = Auxilliary.MakeStringPathSafe(this.DisplayName);
                if (!string.IsNullOrWhiteSpace(safeDisplayName))
                {
                    string logFilePath = Path.Combine(logDirectory, $"{safeDisplayName}.txt");
                    File.WriteAllLines(logFilePath, rejectionMessages);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not write rejection log file for {this.DisplayName}: {ExceptionLogger.GetExceptionStack(ex)}");
            }
        }

        RefreshNpcCount();
    }

    public void RefreshNpcCount() => NpcCount = NpcFormKeys.Count;

    /// <summary>
    /// Checks if FaceGen for the given FormKey exists using pre-cached sets of file paths.
    /// </summary>
    public bool FaceGenExists(FormKey formKey, Dictionary<string, HashSet<string>> looseFileCache,
        HashSet<string> bsaFileCache)
    {
        var faceGenRelPaths = Auxilliary.GetFaceGenSubPathStrings(formKey);

        // Normalize paths for consistent lookups.
        string faceGenMeshRelPath =
            Path.Combine("Meshes", faceGenRelPaths.MeshPath).Replace('\\', '/').ToLowerInvariant();
        string faceGenTexRelPath =
            Path.Combine("Textures", faceGenRelPaths.TexturePath).Replace('\\', '/').ToLowerInvariant();

        // NEW: Check loose files within the scope of THIS mod setting's folders
        foreach (var modFolderPath in this.CorrespondingFolderPaths)
        {
            if (looseFileCache.TryGetValue(modFolderPath, out var looseFilesInThisMod))
            {
                if (looseFilesInThisMod.Any(f => f.Equals(faceGenMeshRelPath, StringComparison.OrdinalIgnoreCase)) || 
                    looseFilesInThisMod.Any(f => f.Equals(faceGenTexRelPath, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        if (bsaFileCache.Contains(faceGenMeshRelPath) || bsaFileCache.Contains(faceGenTexRelPath))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Decides the default state of "Merge Dependencies" via <see cref="MergeInClassifier"/>:
    /// a mod is an appearance replacer (merge) when its NPC records predominantly OVERRIDE
    /// NPCs defined outside this ModSetting (or it targets them via SkyPatcher visual INIs)
    /// and its non-appearance record volume stays within the classifier's allowance;
    /// otherwise it is a base/source mod and merge-in is disabled by default.
    /// Runs identically for the initial folder scan, single-mod refresh, and refresh-all
    /// (which repopulates through the scan path) — all three call this method.
    /// </summary>
    public void CheckMergeInSuitability(Action<string>? showMessageAction)
    {
        StartupLogger.Log($"  [{DisplayName}] Checking merge-in suitability");

        // FaceGen-only entries have no plugins to classify (or only dummy/resource plugins):
        // merge-in is a no-op for them, and the provenance classifier was only validated on
        // plugin-bearing mods — leave their checkbox at its default.
        if (IsFaceGenOnlyEntry)
        {
            return;
        }

        var totals = new MergeInClassifier.Counts();
        bool isBaseGame = false;
        var internalKeys = this.CorrespondingModKeys.ToHashSet();

        foreach (var modKey in this.CorrespondingModKeys)
        {
            if (_environmentStateProvider.BaseGamePlugins.Contains(modKey) ||
                _environmentStateProvider.CreationClubPlugins.Contains(modKey))
            {
                isBaseGame = true;
                break;
            }

            if (!_pluginProvider.TryGetPlugin(modKey, this.CorrespondingFolderPaths.ToHashSet(),
                    out var plugin) || plugin == null)
            {
                continue;
            }

            if (ResourceOnlyModKeys.Contains(plugin.ModKey))
            {
                continue; // counting records in resource modkeys breaks this algorithm because such modkeys can include
                          // things like USSEP, base followers, or any other plugins from which the appearance mod might
                          // import things like headparts, textures, etc.
            }

            try
            {
                // Identity-only tallies (FormKey caches + group counts; no record parsing).
                totals += MergeInClassifier.CountPlugin(plugin, internalKeys);
            }
            catch (Exception e)
            {
                // write error log file here
                string logDirectory = Path.Combine(AppContext.BaseDirectory, "LoadingErrors");
                Directory.CreateDirectory(logDirectory);
                string safeDisplayName = Auxilliary.MakeStringPathSafe(this.DisplayName);
                string logFilePath = Path.Combine(logDirectory, $"{safeDisplayName}.txt");

                showMessageAction?.Invoke(
                    $"An error occurred during mod scanning for {plugin.ModKey.FileName} in {this.DisplayName}. See {logDirectory} for details.");

                string errorMessage =
                    $"An error occurred during mod scanning for {plugin.ModKey.FileName}: {Environment.NewLine}{ExceptionLogger.GetExceptionStack(e)}";

                if (File.Exists(logFilePath))
                {
                    File.AppendAllText(logFilePath,
                        Environment.NewLine + Environment.NewLine + "---" + Environment.NewLine + Environment.NewLine +
                        errorMessage);
                }
                else
                {
                    File.WriteAllText(logFilePath, errorMessage);
                }
            }
        }

        // Lexical INI scan: works at initial-scan time, before SkyPatcherTargetModKeys exists.
        int skyPatcherTargets = MergeInClassifier.CountSkyPatcherVisualTargets(this.CorrespondingFolderPaths);

        var verdict = isBaseGame
            ? MergeInClassifier.Verdict.BaseMod
            : MergeInClassifier.Classify(totals, skyPatcherTargets);

        StartupLogger.Log(
            $"  [{DisplayName}] Merge-in classifier: {verdict} (npcOverrides={totals.OverrideNpcs}, newNpcs={totals.NewNpcs}, " +
            $"skyPatcherTargets={skyPatcherTargets}, supportRecords={totals.SupportRecords}, hardRecords={totals.HardRecords}, isBaseGame={isBaseGame})");

        if (verdict == MergeInClassifier.Verdict.BaseMod)
        {
            this.HasAlteredMergeLogic = true;
            this.MergeInDependencyRecords = false;
            this.MergeInToolTip =
                $"N.P.C. has determined that {this.DisplayName} doesn't look like an NPC appearance replacer " +
                Environment.NewLine +
                $"(NPC overrides: {totals.OverrideNpcs}, own/new NPCs: {totals.NewNpcs}, SkyPatcher visual targets: {skyPatcherTargets}, " +
                Environment.NewLine +
                $"appearance-support records: {totals.SupportRecords}, other records: {totals.HardRecords}), " +
                "suggesting it defines its own content rather than replacing existing NPC appearances." +
                Environment.NewLine +
                "Merge-in has been disabled by default. You can re-enable it, but be warned that " +
                "merging in large plugins with a lot of non-appearance records can freeze the patcher" +
                Environment.NewLine +
                "and is completely unnecessary if the plugin is staying in your load order and you're just making sure its NPC appearances are winning conflicts." +
                Environment.NewLine +
                Environment.NewLine + ModSetting.DefaultMergeInTooltip;
        }
    }

    /// <summary>
    /// Checks that all records expected in master plugins actually exist in those plugins
    /// If one doesn't, the plugin is flagged as having injected records
    /// </summary>
    public async Task<bool> CheckForInjectedRecords(Action<InitializationWarning>? reportWarning, Language? language)
    {
        StartupLogger.Log($"  [{DisplayName}] Checking for injected records");
        foreach (var modKey in CorrespondingModKeys)
        {
            if (_environmentStateProvider.BaseGamePlugins.Contains(modKey) ||
                _environmentStateProvider.CreationClubPlugins.Contains(modKey))
            {
                continue;
            }

            if (!_pluginProvider.TryGetPlugin(modKey, this.CorrespondingFolderPaths.ToHashSet(),
                    out var plugin) || plugin == null)
            {
                continue;
            }

            // Lazily enumerate and check records
            try
            {
                // collect records that are either overrides or injected (modkey is not this plugin)
                var potentialInjections = Auxilliary.LazyEnumerateMajorRecords(plugin)
                    .Where(record => !record.FormKey.ModKey.Equals(plugin.ModKey))
                    .ToHashSet();

                var injectionsByModKey = potentialInjections.GroupBy(record => record.FormKey.ModKey);
                List<ModKey> missingMasters = new();
                foreach (var pluginRecords in injectionsByModKey)
                {
                    var masterModKey = pluginRecords.Key;

                    if (CorrespondingModKeys.Contains(masterModKey))
                    {
                        continue; // don't worry about checking for injection here because if the plugin is in CorrespondingModKeys, the patcher code will try to merge in its records anyway
                    }

                    var masterPlugin = _environmentStateProvider.LoadOrder?.TryGetValue(masterModKey);
                    if (masterPlugin == null)
                    {
                        missingMasters.Add(masterModKey);
                        continue;
                    }

                    foreach (var record in pluginRecords)
                    {
                        // if the record actually exists in the master, then this is an override. Otherwise, it's an injection
                        if (!_recordHandler.TryGetRecordGetterFromMod(record, masterModKey, new(),
                                RecordHandler.RecordLookupFallBack.None, out _))
                        {
                            IsPerformingBatchAction = true;
                            HandleInjectedRecords = true;
                            HandleInjectedOverridesToolTip =
                                $"This plugin has been scanned and found to contain at least one injected record ({record.Type}: {record.FormKey}). It is recommended to enable Injected Record Handling";
                            HasAlteredHandleInjectedRecordsLogic = true;
                            IsPerformingBatchAction = false;
                            return true;
                        }
                    }
                }

                if (missingMasters.Any())
                {
                    reportWarning?.Invoke(new UnscannedInjectedRecordsWarning(
                        PluginFileName: plugin.ModKey.FileName.String,
                        RequestingMod: this.DisplayName,
                        MissingMasters: missingMasters.Select(m => m.FileName.String).ToList()));
                }
            }
            catch (Exception e)
            {
                // write error log file here
                string logDirectory = Path.Combine(AppContext.BaseDirectory, "LoadingErrors");
                Directory.CreateDirectory(logDirectory);
                string safeDisplayName = Auxilliary.MakeStringPathSafe(this.DisplayName);
                string logFilePath = Path.Combine(logDirectory, $"{safeDisplayName}_InjectionCheck.txt");

                reportWarning?.Invoke(new GenericWarning(
                    $"An error occurred during mod record injection scanning for {plugin.ModKey.FileName} in {this.DisplayName}. See {logDirectory} for details."));

                string errorMessage =
                    $"An error occurred during mod scanning for {plugin.ModKey.FileName}: {Environment.NewLine}{ExceptionLogger.GetExceptionStack(e)}";

                if (File.Exists(logFilePath))
                {
                    File.AppendAllText(logFilePath,
                        Environment.NewLine + Environment.NewLine + "---" + Environment.NewLine + Environment.NewLine +
                        errorMessage);
                }
                else
                {
                    File.WriteAllText(logFilePath, errorMessage);
                }
            }
        }

        return false;
    }

    public ModStateSnapshot? GenerateSnapshot()
    {
        try
        {
            var snapshot = new ModStateSnapshot();
            var allPaths = new HashSet<string>(this.CorrespondingFolderPaths, StringComparer.OrdinalIgnoreCase);
            if (this.IsAutoGenerated)
            {
                allPaths.Add(_environmentStateProvider.DataFolderPath);
            }

            // 1. Snapshot Plugins
            foreach (var modKey in this.CorrespondingModKeys)
            {
                if (_pluginProvider.TryGetPlugin(modKey, allPaths, out _, out var path) && path != null)
                {
                    var info = new FileInfo(path);
                    snapshot.PluginSnapshots.Add(new FileSnapshot
                        { FileName = info.Name, FileSize = info.Length, LastWriteTimeUtc = info.LastWriteTimeUtc });
                }
            }

            // 2. Snapshot BSAs
            var bsaPaths = _bsaHandler
                .GetBsaPathsForPluginsInDirs(this.CorrespondingModKeys, allPaths,
                    _skyrimRelease.ToGameRelease()).Values.SelectMany(p => p).Distinct();
            foreach (var bsaPath in bsaPaths)
            {
                var info = new FileInfo(bsaPath);
                if (info.Exists)
                {
                    snapshot.BsaSnapshots.Add(new FileSnapshot
                        { FileName = info.Name, FileSize = info.Length, LastWriteTimeUtc = info.LastWriteTimeUtc });
                }
            }

            // 3. Snapshot Directories (for loose FaceGen)
            foreach (var modPath in this.CorrespondingFolderPaths)
            {
                string faceGeomPath = Path.Combine(modPath, "meshes", "actors", "character", "facegendata",
                    "facegeom");
                string faceTintPath = Path.Combine(modPath, "textures", "actors", "character", "facegendata",
                    "facetint");

                foreach (var dirPath in new[] { faceGeomPath, faceTintPath })
                {
                    var dirInfo = new DirectoryInfo(dirPath);
                    if (dirInfo.Exists)
                    {
                        snapshot.DirectorySnapshots.Add(new DirectorySnapshot
                        {
                            Path = dirInfo.FullName,
                            FileCount = Directory.EnumerateFiles(dirInfo.FullName, "*", SearchOption.AllDirectories)
                                .Count(),
                            LastWriteTimeUtc = dirInfo.LastWriteTimeUtc
                        });
                    }
                }
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to generate snapshot for {this.DisplayName}: {ExceptionLogger.GetExceptionStack(ex)}");
            return null; // Return null on failure to ensure re-analysis
        }
    }

    /// <summary>
    /// Sets the source plugin for a single NPC that has multiple potential source plugins within this ModSetting.
    /// This method directly updates NpcPluginDisambiguation and NpcSourcePluginMap.
    /// Returns true if the source was successfully updated internally.
    /// </summary>
    public bool SetSingleNpcSourcePlugin(FormKey npcKey, ModKey newSourcePlugin)
    {
        // This first check is important: if the NPC is no longer considered ambiguous 
        // (e.g., due to a background refresh or other changes), don't proceed.
        if (!AmbiguousNpcFormKeys.Contains(npcKey))
        {
            Debug.WriteLine(
                $"NPC {npcKey} ({NpcFormKeysToDisplayName.GetValueOrDefault(npcKey, "N/A")}) in ModSetting '{DisplayName}' is no longer ambiguous or choice is not needed. Ignoring SetSingleNpcSourcePlugin.");
            return false;
        }

        // Check if the chosen plugin is actually one of the CorrespondingModKeys associated with this ModSetting.
        if (!CorrespondingModKeys.Contains(newSourcePlugin))
        {
            Debug.WriteLine(
                $"Error: Plugin {newSourcePlugin.FileName} is not a valid source choice within ModSetting '{DisplayName}' because it's not in CorrespondingModKeys.");
            ScrollableMessageBox.ShowError(
                $"Cannot set {newSourcePlugin.FileName} as source for {NpcFormKeysToDisplayName.GetValueOrDefault(npcKey, npcKey.ToString())} because {newSourcePlugin.FileName} is not one of the plugins associated with the '{DisplayName}' mod entry.",
                "Invalid Source Plugin");
            return false;
        }

        // Further check: Does the newSourcePlugin *actually* contain this NPC according to our last full scan?
        // This requires having the result of the last `npcFoundInPlugins` scan or re-querying.
        // For performance, we might skip this very deep check here and rely on the initial population
        // of AvailableSourcePlugins in VM_ModsMenuMugshot to be correct.
        // If an invalid choice were somehow presented and selected, NpcSourcePluginMap would be briefly inconsistent
        // until the next full RefreshNpcLists(). This is a trade-off.

        bool disambiguationChanged = false;
        if (!NpcPluginDisambiguation.TryGetValue(npcKey, out var currentDisambiguation) ||
            currentDisambiguation != newSourcePlugin)
        {
            NpcPluginDisambiguation[npcKey] = newSourcePlugin;
            disambiguationChanged = true; // The user's preference has been recorded/changed.
            Debug.WriteLine(
                $"NpcPluginDisambiguation updated: NPC {npcKey} in ModSetting '{DisplayName}' now prefers {newSourcePlugin.FileName}.");
        }

        // If either the user's preference changed or the actual map entry changed, return true.
        // Typically, if disambiguationChanged is true, mapChanged will also be true.
        if (disambiguationChanged)
        {
            // We are intentionally NOT calling RefreshNpcLists() here to avoid lag.
            // The NpcSourcePluginMap is updated directly for this NPC.
            // Other aspects that RefreshNpcLists() handles (like re-evaluating AmbiguousNpcFormKeys
            // if this change made the NPC no longer ambiguous) will not be updated until the next full refresh.
            // This is acceptable for this specific UI interaction.
            return true;
        }
        else
        {
            Debug.WriteLine(
                $"No change made for NPC {npcKey} in ModSetting '{DisplayName}'; new source {newSourcePlugin.FileName} was already the effective one.");
            return false;
        }
    }

    /// <summary>
    /// Sets a given plugin as the source for all NPCs in this ModSetting that are found within that plugin
    /// AND are listed in AmbiguousNpcFormKeys.
    /// Updates NpcPluginDisambiguation and NpcSourcePluginMap directly.
    /// </summary>
    /// <returns>A list of FormKeys for NPCs whose source was actually changed.</returns>
    public List<FormKey> SetSourcePluginForAllApplicableNpcs(ModKey newGlobalSourcePlugin, bool showMessages = true)
    {
        var changedNpcKeys = new List<FormKey>();

        if (!CorrespondingModKeys.Contains(newGlobalSourcePlugin))
        {
            Debug.WriteLine(
                $"Error: Plugin {newGlobalSourcePlugin.FileName} is not part of this ModSetting '{DisplayName}'. Cannot set as global source.");
            if (showMessages)
            {
                ScrollableMessageBox.ShowError(
                    $"Plugin {newGlobalSourcePlugin.FileName} is not associated with the mod entry '{DisplayName}'.",
                    "Invalid Global Source Plugin");
            }

            return changedNpcKeys;
        }

        string? pluginFilePath = null;
        foreach (var dirPath in CorrespondingFolderPaths)
        {
            string potentialPath = Path.Combine(dirPath, newGlobalSourcePlugin.FileName);
            if (File.Exists(potentialPath))
            {
                pluginFilePath = potentialPath;
                break;
            }
        }

        if (pluginFilePath == null)
        {
            Debug.WriteLine(
                $"Error: Could not find plugin file {newGlobalSourcePlugin.FileName} for ModSetting '{DisplayName}'.");
            if (showMessages)
            {
                ScrollableMessageBox.ShowError(
                    $"Could not locate the file for plugin {newGlobalSourcePlugin.FileName} within the specified mod folders for '{DisplayName}'.",
                    "Plugin File Not Found");
            }

            return changedNpcKeys;
        }

        HashSet<FormKey> npcsActuallyInSelectedPlugin = new HashSet<FormKey>();
        try
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginFilePath, _skyrimRelease);
            foreach (var npcGetter in mod.Npcs)
            {
                npcsActuallyInSelectedPlugin.Add(npcGetter.FormKey);
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine(
                $"Error reading NPCs from {newGlobalSourcePlugin.FileName} for ModSetting '{DisplayName}': {e.Message}");
            if (showMessages)
            {
                ScrollableMessageBox.ShowError(
                    $"Error reading NPC data from plugin {newGlobalSourcePlugin.FileName}:\n{e.Message}",
                    "Plugin Read Error");
            }

            return changedNpcKeys;
        }

        // Iterate a copy of AmbiguousNpcFormKeys if modifications to it are possible indirectly,
        // though direct updates to NpcSourcePluginMap shouldn't affect AmbiguousNpcFormKeys here.
        foreach (FormKey ambiguousNpcKey in AmbiguousNpcFormKeys.ToList())
        {
            // If this multi-origin NPC *can* be sourced from the 'newGlobalSourcePlugin'
            if (npcsActuallyInSelectedPlugin.Contains(ambiguousNpcKey))
            {
                bool disambiguationChanged = false;
                if (!NpcPluginDisambiguation.TryGetValue(ambiguousNpcKey, out var currentDisambiguation) ||
                    currentDisambiguation != newGlobalSourcePlugin)
                {
                    NpcPluginDisambiguation[ambiguousNpcKey] = newGlobalSourcePlugin;
                    disambiguationChanged = true;
                }

                if (disambiguationChanged)
                {
                    changedNpcKeys.Add(ambiguousNpcKey);
                    Debug.WriteLine(
                        $"ModSetting '{DisplayName}': Globally set source for NPC {ambiguousNpcKey} to {newGlobalSourcePlugin.FileName} (direct map update).");
                }
            }
        }

        if (showMessages)
        {
            if (changedNpcKeys.Any())
            {
                ScrollableMessageBox.Show(
                    $"Set {newGlobalSourcePlugin.FileName} as the source for {changedNpcKeys.Count} applicable NPC(s) in '{DisplayName}'.",
                    "Global Source Updated");
            }
            else
            {
                ScrollableMessageBox.Show(
                    $"No NPC source plugin assignments were changed for '{DisplayName}'. This may be because all relevant NPCs already used {newGlobalSourcePlugin.FileName} as their source, or no ambiguous NPCs are present in that plugin.",
                    "No Changes Made");
            }
        }

        return changedNpcKeys;
    }

    /// <summary>
    /// Sets the source plugin for all applicable ambiguous NPCs and notifies the parent
    /// VM to refresh the UI. This is called by the context menu command.
    /// </summary>
    public void SetAndNotifySourcePluginForAll(ModKey newGlobalSourcePlugin)
    {
        // Call the existing logic to update the data model, but suppress the pop-up messages.
        var changedKeys = SetSourcePluginForAllApplicableNpcs(newGlobalSourcePlugin, showMessages: false);

        if (changedKeys.Any())
        {
            // Use the existing reference to the parent VM to signal that the mugshot
            // panel needs to be reloaded to reflect the data changes.
            _parentVm.NotifyMultipleNpcSourcesChanged(this);

            // Also notify the NpcSelectionBar to refresh its view, in case this
            // command was initiated from the NpcsView.
            _parentVm.RequestNpcSelectionBarRefreshView();
        }
    }

    private async Task UnlinkMugshotDataAsync()
    {
        if (!CanUnlinkMugshots) return; // Should be caught by CanExecute, but defensive check

        var mugshotDirs = MugShotFolderPaths
            .GroupBy(x => Path.GetFileName(
                    x.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var mugshotDirNames = mugshotDirs.Select(x => x.Key).ToList();

        StringBuilder sb = new();
        if (mugshotDirNames.Count() == 1)
        {
            sb.AppendLine(
                $"Are you sure you want to unlink the mugshot folder '{mugshotDirNames.First()}' from '{this.DisplayName}'?");
            sb.AppendLine($"This will create a new, separate entry for '{mugshotDirNames.First()}' (mugshots only), ");
        }
        else
        {
            sb.AppendLine(
                $"Are you sure you want to unlink the mugshot folders '{string.Join(", ", mugshotDirNames)}' from '{this.DisplayName}'?");
            sb.AppendLine(
                $"This will create new, separate entries for '{string.Join(", ", mugshotDirNames)}' (mugshots only), ");
        }

        sb.AppendLine($"and '{this.DisplayName}' will no longer have these mugshots associated.");

        // Confirm with user
        if (!ScrollableMessageBox.Confirm(sb.ToString(), "Confirm Unlink Mugshot Data"))
        {
            return;
        }

        foreach (var mugshotEntry in mugshotDirs)
        {
            // 1. Create the new "Mugshot-Only" VM
            // It will be mugshot-only by definition because it has no CorrespondingFolderPaths/CorrespondingModKeys initially
            var newMugshotOnlyVm = new VM_ModSetting(displayName: mugshotEntry.Key, mugshotPath: mugshotEntry.Value,
                parentVm: _parentVm, aux: _aux, bsaHandler: _bsaHandler, pluginProvider: _pluginProvider,
                recordHandler: _recordHandler, lazySettingsVm: _lazySettingsVm);
            // Ensure IsMugshotOnlyEntry is correctly set based on its initial state
            newMugshotOnlyVm.IsMugshotOnlyEntry = true;
            // It won't have NPC lists immediately, that will be populated if/when VM_Mods calls RefreshNpcLists on all.
            // Or, if it's purely for mugshots, NPC lists aren't relevant for *its* data.

            // 2. Add the new VM to VM_Mods and refresh
            await _parentVm.AddAndRefreshModSettingAsync(newMugshotOnlyVm);
        }

        // 3. Modify the current VM (this instance) to be "Data-Only" regarding this mugshot path
        this.MugShotFolderPaths.Clear(); // Clear the mugshot path
        // HasValidMugshots will update reactively via RecalculateMugshotValidity call below.
        // IsMugshotOnlyEntry status of 'this' vm might also change if it now has no paths at all.
        // This will be re-evaluated by VM_Mods.PopulateModSettings next time or by logic within VM_Mods
        this.IsMugshotOnlyEntry = !this.CorrespondingFolderPaths.Any() && !this.CorrespondingModKeys.Any();

        // 4. Trigger a recalculation of valid mugshots for the current (now data-only) VM
        _parentVm.RecalculateMugshotValidity(this);

        // 5. Notify selection bar to refresh appearance sources for current NPC if necessary
        _parentVm.RequestNpcSelectionBarRefreshView();
    }

    // Method executed by DeleteCommand ***
    private void Delete()
    {
        // The CanExecute already prevents this if paths/mugshot are assigned,
        // but a final check is good practice.
        if (!CanDelete)
        {
            Debug.WriteLine($"Attempted to delete VM_ModSetting '{DisplayName}' but CanDelete was false.");
            return;
        }

        // Confirm with user
        if (!ScrollableMessageBox.Confirm(
                $"Are you sure you want to permanently delete the entry for '{DisplayName}'?\n\n" +
                "This action cannot be undone.",
                "Confirm Deletion"))
        {
            return;
        }

        // Request the parent VM to remove this instance from its list
        _parentVm.RemoveModSetting(this);

        // Note: The removal from the underlying Settings model list
        // will happen when VM_Mods.SaveModSettingsToModel is called.
    }

    // [VM_ModSetting.cs] - Full Code After Modifications
    // located in NPC_Plugin_Chooser_2.View_Models/VM_ModSetting.cs

    public async Task FindPluginsWithOverrides(PluginProvider pluginProvider)
    {
        var appearanceTypesToSkip = new HashSet<Type>()
        {
            typeof(INpcGetter),
        };

        StartupLogger.Log($"  [{DisplayName}] Finding plugins with overrides");
        using (ContextualPerformanceTracer.Trace("FindPluginsWithOverrides", this.DisplayName))
        {
            _pluginsWithOverrideRecords.Clear();
            var overridesCache = _parentVm.GetOverrideCache();

            foreach (var modKey in CorrespondingModKeys)
            {
                using (ContextualPerformanceTracer.Trace("FPO.ModKeyLoop", modKey.FileName))
                {
                    if (modKey.Equals(_environmentStateProvider.AbsoluteBasePlugin))
                    {
                        continue;
                    }

                    ISkyrimModGetter? plugin;
                    string? pluginSourcePath;
                    bool pluginFound;
                    using (ContextualPerformanceTracer.Trace("FPO.TryGetPlugin", modKey.FileName))
                    {
                        pluginFound =
                            pluginProvider.TryGetPlugin(modKey, new HashSet<string>(CorrespondingFolderPaths),
                                out plugin, out pluginSourcePath) && plugin != null && pluginSourcePath != null;
                    }

                    if (!pluginFound)
                    {
                        continue;
                    }

                    var cacheKey = (pluginSourcePath: pluginSourcePath!, modKey);

                    // Use GetOrAdd to atomically get the result or create it if it doesn't exist.
                    bool hasOverrides = overridesCache.GetOrAdd(cacheKey, key =>
                    {
                        // This "value factory" code only runs if the key is not already in the cache.
                        // The expensive work is now protected from race conditions.
                        bool result = false;

                        using (ContextualPerformanceTracer.Trace("FPO.RecordLoop", key.modKey.FileName))
                        {
                            // Iterates lazily. Stops pulling records from the plugin file as soon as the break is hit.
                            try
                            {
                                foreach (var record in Auxilliary.LazyEnumerateMajorRecords(plugin,
                                             appearanceTypesToSkip))
                                {
                                    if (!CorrespondingModKeys.Contains(record.FormKey.ModKey))
                                    {
                                        result = true;
                                        break; // This now prevents further enumeration and parsing
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                // write error log file here
                                string logDirectory = Path.Combine(AppContext.BaseDirectory, "LoadingErrors");
                                Directory.CreateDirectory(logDirectory);
                                string safeDisplayName = Auxilliary.MakeStringPathSafe(DisplayName);
                                string logFilePath = Path.Combine(logDirectory, $"{safeDisplayName}.txt");

                                string errorMessage =
                                    $"An error occurred during mod scanning for {plugin.ModKey.FileName}: {Environment.NewLine}{ExceptionLogger.GetExceptionStack(e)}";

                                if (File.Exists(logFilePath))
                                {
                                    File.AppendAllText(logFilePath,
                                        Environment.NewLine + Environment.NewLine + "---" + Environment.NewLine +
                                        Environment.NewLine + errorMessage);
                                }
                                else
                                {
                                    File.WriteAllText(logFilePath, errorMessage);
                                }
                            }
                        }

                        return result;
                    });

                    if (hasOverrides)
                    {
                        _pluginsWithOverrideRecords.Add(modKey);
                    }
                }
            }
        }
    }

    private async Task RefreshAsync()
    {
        if (_parentVm == null)
        {
            Debug.WriteLine($"Cannot refresh '{DisplayName}': Parent VM is null.");
            return; // Exit if parent is not available
        }

        try
        {
            IsRefreshing = true; // Show the "Refreshing..." indicator

            // Ask the parent VM to perform the refresh and get result + reason
            var (isValid, failureReason) = await _parentVm.RefreshSingleModSettingAsync(this);
            
            if (!isValid)
            {
                // If the refresh determined the mod is no longer valid, notify the user.
                ScrollableMessageBox.Show(
                    $"The mod '{DisplayName}' no longer contains any plugins or FaceGen files.\nReason: {failureReason}\n\nIt will be removed from the appearance mods list.",
                    "Mod Removed");
            }
        }
        finally
        {
            IsRefreshing = false; // Always hide the indicator when done
        }
    }

    public async Task<List<(FormKey TargetNpc, FormKey SourceNpc, string ModDisplayName, string SourceNpcDisplayName)>>
        GetSkyPatcherImportsAsync(
            IReadOnlyDictionary<string, HashSet<FormKey>> environmentEditorIdMap,
            IReadOnlyDictionary<string, HashSet<FormKey>> modEditorIdMap)
    {
        var guestAppearances =
            new List<(FormKey TargetNpc, FormKey SourceNpc, string ModDisplayName, string SourceNpcDisplayName)>();
        var iniFiles = new List<string>();

        foreach (var modPath in CorrespondingFolderPaths)
        {
            var skyPatcherNpcDir = Path.Combine(modPath, "SKSE", "Plugins", "SkyPatcher", "npc");
            if (Directory.Exists(skyPatcherNpcDir))
            {
                iniFiles.AddRange(Directory.EnumerateFiles(skyPatcherNpcDir, "*.ini", SearchOption.AllDirectories));
            }
        }

        if (!iniFiles.Any()) return guestAppearances;

        StartupLogger.Log($"  [{DisplayName}] Parsing {iniFiles.Count} SkyPatcher INI files");

        foreach (var iniFile in iniFiles)
        {
            var lines = await File.ReadAllLinesAsync(iniFile);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (!trimmedLine.StartsWith("filterByNpcs=", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = trimmedLine.Split(':');
                if (parts.Length < 2) continue;

                var targetNpcStr = parts[0].Substring("filterByNpcs=".Length);
                if (!TryParseSkyPatcherNpc(targetNpcStr, environmentEditorIdMap, out var targetNpcKeys)) continue;

                var visualStyleAction = parts.FirstOrDefault(p =>
                    p.StartsWith("copyVisualStyle=", StringComparison.OrdinalIgnoreCase));
                if (visualStyleAction == null) continue;

                var sourceNpcStr = visualStyleAction.Substring("copyVisualStyle=".Length);
                if (!TryParseSkyPatcherNpc(sourceNpcStr, modEditorIdMap, out var sourceNpcKeys)) continue;

                // Nested loop to create all combinations
                foreach (var targetNpcKey in targetNpcKeys)
                {
                    foreach (var sourceNpcKey in sourceNpcKeys)
                    {
                        if (!NpcFormKeysToDisplayName.TryGetValue(sourceNpcKey, out var npcDisplayName))
                        {
                            npcDisplayName = "No Name";
                        }

                        guestAppearances.Add((targetNpcKey, sourceNpcKey, DisplayName, npcDisplayName));
                    }
                }
            }
        }

        return guestAppearances;
    }

    /// <summary>
    /// Lightweight variant of <see cref="GetSkyPatcherImportsAsync"/> that returns just the
    /// distinct <see cref="ModKey"/>s this mod targets via SkyPatcher INIs (the
    /// <c>filterByNpcs=...</c> side). Unlike the full importer — which only resolves targets
    /// against the load order so guest-appearance entries in <c>_settings.SelectedAppearanceMods</c>
    /// don't accumulate FormKeys that aren't reachable in-game — this method falls back to
    /// <paramref name="modEditorIdMap"/> when the env map misses, so foundations that exist
    /// on disk but aren't enabled in the load order can still be identified.
    ///
    /// That fallback is exactly what <see cref="VM_Mods.CleanupCorrespondingFolders"/> needs:
    /// the polluted state has the foundation folder attached as a VM resource, so its plugins
    /// ARE loaded and ARE in the per-VM map even though the LO doesn't include them. Without
    /// this widened lookup, SkyPatcher-template replacers (e.g. <c>t_Amalee_Replacer.esp</c>
    /// → <c>3DNPC.esp</c>) would slip through the cleanup whenever the user hasn't enabled
    /// the foundation in their LO.
    /// </summary>
    public async Task<HashSet<ModKey>> GetSkyPatcherTargetModKeysAsync(
        IReadOnlyDictionary<string, HashSet<FormKey>> environmentEditorIdMap,
        IReadOnlyDictionary<string, HashSet<FormKey>> modEditorIdMap)
    {
        var targetModKeys = new HashSet<ModKey>();
        var iniFiles = new List<string>();

        foreach (var modPath in CorrespondingFolderPaths)
        {
            var skyPatcherNpcDir = Path.Combine(modPath, "SKSE", "Plugins", "SkyPatcher", "npc");
            if (Directory.Exists(skyPatcherNpcDir))
            {
                iniFiles.AddRange(Directory.EnumerateFiles(skyPatcherNpcDir, "*.ini", SearchOption.AllDirectories));
            }
        }

        if (!iniFiles.Any()) return targetModKeys;

        foreach (var iniFile in iniFiles)
        {
            var lines = await File.ReadAllLinesAsync(iniFile);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (!trimmedLine.StartsWith("filterByNpcs=", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = trimmedLine.Split(':');
                if (parts.Length == 0) continue;

                var targetNpcStr = parts[0].Substring("filterByNpcs=".Length);

                // env first (foundation in LO), mod fallback (foundation only on disk).
                if (!TryParseSkyPatcherNpc(targetNpcStr, environmentEditorIdMap, out var targetNpcKeys))
                {
                    if (!TryParseSkyPatcherNpc(targetNpcStr, modEditorIdMap, out targetNpcKeys)) continue;
                }

                foreach (var fk in targetNpcKeys)
                {
                    targetModKeys.Add(fk.ModKey);
                }
            }
        }

        return targetModKeys;
    }

    private bool TryParseSkyPatcherNpc(string npcStr, IReadOnlyDictionary<string, HashSet<FormKey>> editorIdMap,
        out HashSet<FormKey> formKeys)
    {
        formKeys = new HashSet<FormKey>(); // Initialize to an empty set
        if (string.IsNullOrWhiteSpace(npcStr)) return false;

        var parts = npcStr.Split('|');
        if (parts.Length == 2)
        {
            // Format: Skyrim.esm|132AB
            var modName = parts[0];
            var idHex = parts[1];
            if (ModKey.TryFromFileName(modName, out var modKey) && uint.TryParse(idHex,
                    System.Globalization.NumberStyles.HexNumber, null, out var id))
            {
                formKeys.Add(new FormKey(modKey, id)); // Add the single result to the set
                return true;
            }
        }
        else if (parts.Length == 1 && !string.IsNullOrWhiteSpace(parts[0]))
        {
            // Format: EditorID
            // The TryGetValue now returns a HashSet directly
            return editorIdMap.TryGetValue(parts[0], out formKeys!);
        }

        return false;
    }

    /// <summary>
    /// Opens a dialog for the user to select which plugins should be treated as resource-only.
    /// If the selection changes, triggers a refresh of the mod setting.
    /// </summary>
    private void SetResourcePlugins()
    {
        var selectorVm = new VM_ResourcePluginSelector(CorrespondingModKeys, new HashSet<ModKey>(ResourceOnlyModKeys));
        var window = new ResourcePluginSelectorWindow
        {
            DataContext = selectorVm,
            ViewModel = selectorVm // Add this line for consistency with the working example
        };

        // Find the currently active window to set as the owner. This is more robust
        // than using Application.Current.MainWindow.
        window.Owner = System.Windows.Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive);

        window.ShowDialog(); // The dialog result is now handled by the event in the view's code-behind

        if (selectorVm.HasChanged)
        {
            ResourceOnlyModKeys = selectorVm.GetSelectedModKeys();
            RefreshCommand.Execute().Subscribe();
        }
    }

    /// <summary>
    /// Checks if a given folder should be treated as a resource folder.
    /// A folder is a resource if it contains no plugins, or if all of its plugins
    /// are masters of other plugins already in this mod setting.
    /// </summary>
    private void AutoSetResourcePlugins(string folderPath)
    {
        // Find all plugins within the new folder path.
        var pluginsInNewFolder = _aux.GetModKeysInDirectory(folderPath, new List<string>(), false);

        if (!pluginsInNewFolder.Any())
        {
            return;
        }

        // Condition B: Check if the folder's plugins are masters of existing plugins.
        var existingPluginPaths = this.CorrespondingFolderPaths.ToHashSet();
        var allExistingMasters = new HashSet<ModKey>();

        // Compile a set of all masters required by plugins already in this mod setting.
        foreach (var modKey in this.CorrespondingModKeys)
        {
            if (_pluginProvider.TryGetPlugin(modKey, existingPluginPaths, out var plugin) && plugin != null)
            {
                foreach (var masterRef in plugin.ModHeader.MasterReferences)
                {
                    allExistingMasters.Add(masterRef.Master);
                }
            }
        }

        foreach (var modKey in pluginsInNewFolder)
        {
            if (allExistingMasters.Contains(modKey))
            {
                ResourceOnlyModKeys.Add(modKey);
            }
        }

        _pluginProvider.UnloadPlugins(CorrespondingModKeys, existingPluginPaths);
    }

    /// <summary>
    /// Recomputes <see cref="ResourceOnlyModKeys"/> from folder layout and per-plugin content.
    /// The mod setting's "root" folder is the entry in <see cref="CorrespondingFolderPaths"/>
    /// whose folder name matches <see cref="DisplayName"/>. The classification is:
    ///
    ///   - Plugin lives outside the root folder (i.e. in a dependency folder pulled in by
    ///     <see cref="VM_Mods.FindAndAddMissingMasters"/>): resource-only regardless of content.
    ///     This is "guilt by association" — siblings in a dependency folder may define their own
    ///     NPC records that have nothing to do with this mod, and we must not present them as
    ///     belonging to it.
    ///   - Plugin lives in the root folder and contains no NPC records (e.g. a centralized
    ///     "Aela - Resources.esp" that hosts skins / headparts / texturesets but no NPC overrides):
    ///     resource-only.
    ///   - Plugin lives in the root folder and contains NPC records (own or override): appearance
    ///     plugin, not resource-only.
    ///
    /// Falls back to prune-only (drop entries no longer in <see cref="CorrespondingModKeys"/>)
    /// when no root folder can be identified — e.g. auto-generated entries (Base Game, Creation
    /// Club) which have no folder paths, or the rare case where the user renamed the mod folder
    /// out of sync with the mod setting.
    /// </summary>
    public void RecomputeResourceOnlyPlugins()
    {
        var dbgTag = $"[ResourceOnly:{DisplayName}]";
        if (CorrespondingModKeys == null || CorrespondingModKeys.Count == 0)
        {
            if (ResourceOnlyModKeys.Count > 0)
            {
                Debug.WriteLine($"{dbgTag} CorrespondingModKeys empty -> clearing ResourceOnlyModKeys ({ResourceOnlyModKeys.Count} entries).");
                ResourceOnlyModKeys.Clear();
            }
            return;
        }

        var rootFolder = CorrespondingFolderPaths.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p) &&
            string.Equals(
                Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                DisplayName,
                StringComparison.OrdinalIgnoreCase));

        if (rootFolder == null)
        {
            var validKeys = new HashSet<ModKey>(CorrespondingModKeys);
            var staleKeys = ResourceOnlyModKeys.Where(k => !validKeys.Contains(k)).ToList();
            if (staleKeys.Count > 0)
            {
                Debug.WriteLine($"{dbgTag} No root folder matched DisplayName; pruning {staleKeys.Count} stale entries: {string.Join(", ", staleKeys.Select(k => k.FileName))}");
                foreach (var stale in staleKeys)
                {
                    ResourceOnlyModKeys.Remove(stale);
                }
            }
            Debug.WriteLine($"{dbgTag} Final ResourceOnlyModKeys ({ResourceOnlyModKeys.Count}): {string.Join(", ", ResourceOnlyModKeys.Select(k => k.FileName))}");
            return;
        }

        var rootPlugins = _aux.GetModKeysInDirectory(rootFolder, new List<string>(), false).ToHashSet();
        Debug.WriteLine($"{dbgTag} Root folder='{rootFolder}', root plugins ({rootPlugins.Count}): {string.Join(", ", rootPlugins.Select(k => k.FileName))}");

        // Defensive: if the directory enumeration came back empty but we have keys in
        // CorrespondingModKeys, the only ways that can happen are:
        //   (a) a transient IO error inside Directory.EnumerateFiles (Vortex deploy,
        //       antivirus, file lock during system stress) — GetModKeysInDirectory
        //       swallows the exception and returns []
        //   (b) someone wiped the mod folder while NPC2 was running
        // In both cases, flipping every plugin to resource-only and persisting that
        // would silently delete the mod from the NPC tab on next launch. Preserve
        // the existing ResourceOnlyModKeys instead.
        if (rootPlugins.Count == 0 && CorrespondingModKeys.Count > 0)
        {
            Debug.WriteLine($"{dbgTag} rootFolder='{rootFolder}' enumerated to 0 plugins, " +
                            $"but CorrespondingModKeys has {CorrespondingModKeys.Count} entries. " +
                            $"Suspected transient IO error; preserving existing ResourceOnlyModKeys.");
            StartupLogger.Log($"[ResourceOnly] {DisplayName}: rootFolder enumeration returned empty " +
                              $"despite {CorrespondingModKeys.Count} CorrespondingModKeys; " +
                              $"preserving prior ResourceOnlyModKeys to avoid corruption.");
            return;
        }

        var existingPluginPaths = CorrespondingFolderPaths.ToHashSet();
        var newResourceOnly = new HashSet<ModKey>();

        foreach (var modKey in CorrespondingModKeys)
        {
            if (!rootPlugins.Contains(modKey))
            {
                newResourceOnly.Add(modKey);
                Debug.WriteLine($"{dbgTag}   {modKey.FileName}: in dependency folder -> resource-only");
                continue;
            }

            bool hasNpcRecords = false;
            if (_pluginProvider.TryGetPlugin(modKey, existingPluginPaths, out var plugin) && plugin != null)
            {
                hasNpcRecords = plugin.Npcs.Count > 0;
            }

            if (!hasNpcRecords)
            {
                newResourceOnly.Add(modKey);
                Debug.WriteLine($"{dbgTag}   {modKey.FileName}: in root folder, no NPC records -> resource-only");
            }
            else
            {
                Debug.WriteLine($"{dbgTag}   {modKey.FileName}: in root folder, has NPC records -> appearance plugin");
            }
        }

        ResourceOnlyModKeys.Clear();
        foreach (var key in newResourceOnly)
        {
            ResourceOnlyModKeys.Add(key);
        }
        Debug.WriteLine($"{dbgTag} Final ResourceOnlyModKeys ({ResourceOnlyModKeys.Count}): {string.Join(", ", ResourceOnlyModKeys.Select(k => k.FileName))}");
    }
    
    private void UpdateIsMaxNestedIntervalDepthVisible()
    {
        var effectiveMode = OverrideRecordOverrideHandlingMode ?? _lazySettingsVm.Value.SelectedRecordOverrideHandlingMode;
        // The entire row is visible when mode is not Ignore
        IsOverrideHandlingControlsVisible = effectiveMode != RecordOverrideHandlingMode.Ignore;
        // Max Nested is visible only if mode is not Ignore AND "Include All" is not checked
        IsMaxNestedIntervalDepthVisible = effectiveMode != RecordOverrideHandlingMode.Ignore && !IncludeAllOverrides;
    }
    
    /// <summary>
    /// Opens a dialog for the user to manage keywords for this mod setting.
    /// </summary>
    private void SetKeywords()
    {
        // Gather all unique keywords from other mod settings
        var otherKeywords = _parentVm.AllModSettings
            .Where(m => m != this)
            .SelectMany(m => m.Keywords)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var window = new KeywordSelectionWindow();
        window.Initialize(DisplayName, Keywords, otherKeywords);
        
        // Find the currently active window to set as the owner
        window.Owner = System.Windows.Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive);

        window.ShowDialog();

        if (window.HasChanged)
        {
            Keywords.Clear();
            foreach (var keyword in window.GetKeywords().OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                Keywords.Add(keyword);
            }
        }
    }

    #region Drag and Drop Logic

    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        // --- START DEBUGGING LOGIC ---

        // Get the source UI element (where the drag started)
        var sourceElement = dropInfo.DragInfo.VisualSource as FrameworkElement;

        // Get the target UI element (what the mouse is currently over)
        var targetElement = dropInfo.VisualTarget as FrameworkElement;

        // Get the ViewModel for each element from its DataContext
        var sourceVm = sourceElement?.DataContext as VM_ModSetting;
        var targetVm = targetElement?.DataContext as VM_ModSetting;

        if (sourceVm != null && targetVm != null)
        {
            // Print the names to the debug output window in Visual Studio
            Debug.WriteLine($"Source: {sourceVm.DisplayName} | Target: {targetVm.DisplayName}");
        }

        // --- END DEBUGGING LOGIC ---


        // The actual drop condition is based on comparing the ViewModel instances
        if (sourceVm != null && targetVm != null && sourceVm == targetVm)
        {
            // YES: The drop is on the original list. Allow it.
            dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
            dropInfo.Effects = System.Windows.DragDropEffects.Move;
        }
        else
        {
            // NO: The drop is on a DIFFERENT list. Forbid it.
            dropInfo.Effects = System.Windows.DragDropEffects.None;
        }
    }

    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        // The Gong library provides a helper to handle the collection move.
        // It handles different collection types automatically.
        DragDrop.DefaultDropHandler.Drop(dropInfo);
    }

    #endregion
}