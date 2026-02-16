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
    Template
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

    [Reactive] public int MaxNestedIntervalDepth { get; set; } = 2;
    [Reactive] public bool IsMaxNestedIntervalDepthVisible { get; set; }
    [Reactive] public bool IncludeAllOverrides { get; set; } = false;
    
    public HashSet<string> NpcNames { get; set; } = new();
    public HashSet<string> NpcEditorIDs { get; set; } = new();
    public HashSet<FormKey> NpcFormKeys { get; set; } = new();
    public Dictionary<FormKey, string> NpcFormKeysToDisplayName { get; set; } = new();

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
    // rather than all NPCs in the corresponding plugins should be displayed
    public bool IsFaceGenOnlyEntry { get; set; } = false;

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
        MergeInToolTip = model.MergeInToolTip;
        HandleInjectedRecords = model.HandleInjectedRecords;
        HasAlteredHandleInjectedRecordsLogic = model.HasAlteredHandleInjectedRecordsLogic;
        HandleInjectedOverridesToolTip = model.HandleInjectedOverridesToolTip;
        OverrideRecordOverrideHandlingMode = model.ModRecordOverrideHandlingMode;
        IncludeAllOverrides = model.IncludeAllOverrides;
        MaxNestedIntervalDepth = model.MaxNestedIntervalDepth;
        // AvailablePluginsForNpcs should be re-calculated on load.
        // IsMugshotOnlyEntry is set to false via chaining
        IsFaceGenOnlyEntry = model.IsFaceGenOnlyEntry;
        FaceGenOnlyNpcFormKeys = new(model.FaceGenOnlyNpcFormKeys);

        MergeInDependencyRecordsVisible = DisplayName != VM_Mods.BaseGameModSettingName &&
                                          DisplayName != VM_Mods.CreationClubModsettingName;

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
            MergeInToolTip = MergeInToolTip,
            HasAlteredMergeLogic = HasAlteredMergeLogic,
            HandleInjectedRecords = HandleInjectedRecords,
            HasAlteredHandleInjectedRecordsLogic = HasAlteredHandleInjectedRecordsLogic,
            HandleInjectedOverridesToolTip = HandleInjectedOverridesToolTip,
            ModRecordOverrideHandlingMode = OverrideRecordOverrideHandlingMode,
            MaxNestedIntervalDepth = MaxNestedIntervalDepth,
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

            using (ContextualPerformanceTracer.Trace("RefreshNpcLists.MainLoop"))
            {
                try
                {
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
                            continue;
                        }

                        foreach (var npcGetter in plugin.Npcs)
                        {
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
                                        sourcePluginRace:
                                        sourcePluginRace)) // if sourcePluginRace is null, falls back to searching the link cache
                                {
                                    string message =
                                        $"Discarded {Auxilliary.GetLogString(npcGetter, language)} from {DisplayName} because {rejectionMessage}.";
                                    //Debug.WriteLine(message);
                                    rejectionMessages.Add(message);
                                    continue;
                                }
                            }

                            FormKey currentNpcKey = npcGetter.FormKey;

                            using (ContextualPerformanceTracer.Trace("RefreshNpcLists.FaceGenCheck"))
                            {
                                // This is the cache of BSA files relevant to the *current mod setting*
                                allFaceGenBsaFiles.TryGetValue(this.DisplayName, out var currentBsaCache);

                                if (!FaceGenExists(currentNpcKey, allFaceGenLooseFiles,
                                        currentBsaCache ?? new HashSet<string>()) &&
                                    !npcGetter.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag
                                        .Traits))
                                {
                                    string message =
                                        $"Discarded {Auxilliary.GetLogString(npcGetter, language, true)} from {DisplayName} because it has no FaceGen and does not use a template.";
                                    //Debug.WriteLine(message);
                                    rejectionMessages.Add(message);
                                    continue;
                                }
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

                            using (ContextualPerformanceTracer.Trace("RefreshNpcLists.TemplateCheck"))
                            {
                                if (npcGetter.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag
                                        .Traits))
                                {
                                    string templateStr = npcGetter.Template?.FormKey.ToString() ??
                                                         "NULL TEMPLATE";

                                    NpcFormKeysToNotifications[currentNpcKey] = (
                                        IssueType: NpcIssueType.Template,
                                        IssueMessage:
                                        $"Despite having FaceGen files, this NPC from {plugin.ModKey.FileName} has the Traits flag so it inherits appearance from {templateStr}. If the selected Appearance Mod for this NPC doesn't match that of its Template, visual glitches can occur in-game.",
                                        FormKey: npcGetter.Template?.FormKey);
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

        NpcCount = NpcFormKeys.Count;
    }

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
    /// Checks the number of potential appearance records vs. total records.
    /// If most of the records are not related to NPC appearance, flag that this mod probably shouldn't be merged in.
    /// </summary>
    public void CheckMergeInSuitability(Action<string>? showMessageAction)
    {
        int appearanceRecordCount = 0;
        int nonAppearanceRecordCount = 0;
        bool isBaseGame = false;

        // Define the set of appearance-related record types to skip in the main enumeration
        var appearanceTypesToSkip = new HashSet<Type>()
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

            // Get counts of appearance records instantly (O(1) operation)
            appearanceRecordCount += plugin.Npcs.Count;
            appearanceRecordCount += plugin.Armors.Count;
            appearanceRecordCount += plugin.ArmorAddons.Count;
            appearanceRecordCount += plugin.TextureSets.Count;
            appearanceRecordCount += plugin.HeadParts.Count;
            appearanceRecordCount += plugin.Hairs.Count;
            appearanceRecordCount += plugin.Colors.Count;
            appearanceRecordCount += plugin.Eyes.Count;

            // Lazily enumerate and count ONLY the non-appearance records
            int nonAppearanceRecordCountForPlugin = 0;
            try
            {
                nonAppearanceRecordCountForPlugin =
                    Auxilliary.LazyEnumerateMajorRecords(plugin, appearanceTypesToSkip).Count();
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

            nonAppearanceRecordCount += nonAppearanceRecordCountForPlugin;
        }


        if (isBaseGame || nonAppearanceRecordCount > appearanceRecordCount)
        {
            this.HasAlteredMergeLogic = true;
            this.MergeInDependencyRecords = false;
            this.MergeInToolTip =
                $"N.P.C. has determined that the plugin(s) in {this.DisplayName} have more non-appearance records than appearance records, " +
                Environment.NewLine +
                "suggesting that it's not just an appearance replacer mod. Merge-in has been disabled by default. You can re-enable it, but be warned that " +
                Environment.NewLine +
                "merging in large plugins with a lot of non-appearance records can freeze the patcher and is completely unnecessary if the plugin is staying in your load order" +
                Environment.NewLine +
                "and you're just making sure its NPC appearances are winning conflicts." + Environment.NewLine +
                Environment.NewLine + ModSetting.DefaultMergeInTooltip;
        }
    }

    /// <summary>
    /// Checks that all records expected in master plugins actually exist in those plugins
    /// If one doesn't, the plugin is flagged as having injected records
    /// </summary>
    public async Task<bool> CheckForInjectedRecords(Action<string>? showMessageAction, Language? language)
    {
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
                    showMessageAction?.Invoke(
                        $"Warning: {plugin.ModKey.FileName} in {this.DisplayName} could not be fully scanned for injected records because its master(s) {string.Join(" and ", missingMasters)} are not in your load order. You can complete the scan by adding the master and clicking the Refresh button for {DisplayName} in the Mods Menu.");
                }
            }
            catch (Exception e)
            {
                // write error log file here
                string logDirectory = Path.Combine(AppContext.BaseDirectory, "LoadingErrors");
                Directory.CreateDirectory(logDirectory);
                string safeDisplayName = Auxilliary.MakeStringPathSafe(this.DisplayName);
                string logFilePath = Path.Combine(logDirectory, $"{safeDisplayName}_InjectionCheck.txt");

                showMessageAction?.Invoke(
                    $"An error occurred during mod record injection scanning for {plugin.ModKey.FileName} in {this.DisplayName}. See {logDirectory} for details.");

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