using System;
using System.IO;
using System.Reactive;
using System.Windows; 
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Views; 
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat; 
using System.Collections.Generic; 
using System.Collections.ObjectModel; 
using System.Linq; 
using System.Diagnostics;
using System.Net.Http;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using SixLabors.ImageSharp; // For Debug.WriteLine

namespace NPC_Plugin_Chooser_2.View_Models;

[DebuggerDisplay("{NpcDisplayName}")]
public class VM_ModsMenuMugshot : ReactiveObject, IHasMugshotImage, IDisposable
{
    public delegate VM_ModsMenuMugshot Factory(
        string imagePath,
        FormKey npcFormKey,
        string npcDisplayName,
        VM_Mods parentVMMaster,
        bool isAmbiguousSource,
        List<ModKey> availableSourcePlugins,
        ModKey? currentSourcePlugin,
        VM_ModSetting parentVMModSetting,
        CancellationToken cancellationToken
    );

    private readonly VM_Mods _parentVMMaster;
    private readonly VM_ModSetting _parentVMModSetting;
    private readonly NpcConsistencyProvider _consistencyProvider;
    private readonly Settings _settings;
    private readonly VM_NpcSelectionBar _npcSelectionBar;
    private readonly FaceFinderClient _faceFinderClient;
    private readonly PortraitCreator _portraitCreator;
    private readonly InternalMugshotGenerator _internalMugshotGenerator;
    private readonly GeneratedMugshotTracker _tracker;
    private readonly FaceFinderCacheTracker _faceFinderTracker;
    private readonly MugshotStalenessChecker _stalenessChecker;
    private readonly ImagePacker _imagePacker;
    private readonly Func<VM_InternalMugshotPreview> _internalPreviewFactory;
    private readonly CancellationToken _cancellationToken;
    private readonly CompositeDisposable _disposables = new();

    public string ImagePath { get; set; }
    public FormKey NpcFormKey { get; }
    public string NpcDisplayName { get; }
    public string ToolTipText => $"{NpcDisplayName} ({NpcFormKey})";

    [Reactive] public double ImageWidth { get; set; }
    [Reactive] public double ImageHeight { get; set; }

    // HasMugshot is true if ImagePath points to a REAL mugshot, false if it's a placeholder or invalid.
    [Reactive] public bool HasMugshot { get; private set; }
    public bool IsVisible { get; set; }

    [Reactive] public bool IsSelected { get; set; }
    [Reactive] public SolidColorBrush BorderColor { get; set; }
    private readonly SolidColorBrush _selectedBrush = new(Colors.LimeGreen);
    private readonly SolidColorBrush _deselectedBrush = new(Colors.Gray);

    public int OriginalPixelWidth { get; set; }
    public int OriginalPixelHeight { get; set; }
    public double OriginalDipWidth { get; set; }
    public double OriginalDipHeight { get; set; }
    public double OriginalDipDiagonal { get; set; }
    [Reactive] public ImageSource? MugshotSource { get; set; }

    public bool IsAmbiguousSource { get; }
    public ObservableCollection<ModKey> AvailableSourcePlugins { get; } = new();
    [Reactive] public ModKey? CurrentSourcePlugin { get; set; }

    [Reactive] public bool IsFavorite { get; set; }

    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public double LoadingIconRadiusModifier { get; set; } = 0.2;

    [Reactive] public bool HasMissingAssets { get; set; } = false;
    [Reactive] public string MissingAssetNotificationText { get; set; } = string.Empty;

    public VM_ModSetting ParentVMModSetting => _parentVMModSetting;
    public bool CanOpenModFolder => _parentVMModSetting.CorrespondingFolderPaths.Any();
    public bool CanOpenMugshotFolder => HasMugshot;

    public string MugshotFolderPath => HasMugshot && !string.IsNullOrEmpty(ImagePath)
        ? Path.GetDirectoryName(ImagePath)
        : string.Empty;

    public ObservableCollection<ModPageInfo> ModPageUrls { get; } = new();
    [ObservableAsProperty] public bool CanVisitModPage { get; }
    [ObservableAsProperty] public bool HasSingleModPage { get; }

    public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
    public ReactiveCommand<Unit, Unit> Show3DPreviewCommand { get; }
    public ReactiveCommand<Unit, Unit> JumpToNpcCommand { get; }
    public ReactiveCommand<ModKey, Unit> SetNpcSourcePluginCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectSameSourcePluginWherePossibleCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToFavoritesCommand { get; }
    public ReactiveCommand<string, Unit> OpenFolderCommand { get; }
    public ReactiveCommand<string, Unit> VisitModPageCommand { get; }

    // Static path for placeholder, consistent with VM_Mods
    private const string PlaceholderResourceRelativePath = @"Resources\No Mugshot.png";

    private static readonly string FullPlaceholderPath =
        Path.Combine(AppContext.BaseDirectory, PlaceholderResourceRelativePath);

    public record ModPageInfo(string DisplayName, string Url);

    public VM_ModsMenuMugshot(
        string imagePath,
        FormKey npcFormKey,
        string npcDisplayName,
        VM_Mods parentVMMaster,
        bool isAmbiguousSource,
        List<ModKey> availableSourcePlugins,
        ModKey? currentSourcePlugin,
        VM_ModSetting parentVMModSetting,
        CancellationToken cancellationToken,
        // --- Auto-resolved by Autofac ---
        NpcConsistencyProvider consistencyProvider,
        Settings settings,
        VM_NpcSelectionBar npcSelectionBar,
        FaceFinderClient faceFinderClient,
        PortraitCreator portraitCreator,
        InternalMugshotGenerator internalMugshotGenerator,
        MugshotStalenessChecker stalenessChecker,
        ImagePacker imagePacker,
        Func<VM_InternalMugshotPreview> internalPreviewFactory,
        GeneratedMugshotTracker tracker,
        FaceFinderCacheTracker faceFinderTracker
    )
    {
        _parentVMMaster = parentVMMaster;
        _parentVMModSetting = parentVMModSetting;
        _consistencyProvider = consistencyProvider;
        _settings = settings;
        _npcSelectionBar = npcSelectionBar;
        _faceFinderClient = faceFinderClient;
        _portraitCreator = portraitCreator;
        _internalMugshotGenerator = internalMugshotGenerator;
        _stalenessChecker = stalenessChecker;
        _imagePacker = imagePacker;
        _internalPreviewFactory = internalPreviewFactory;
        _tracker = tracker;
        _faceFinderTracker = faceFinderTracker;
        _cancellationToken = cancellationToken;

        ImagePath = imagePath; // Store the given path (could be real or placeholder)

        NpcFormKey = npcFormKey;
        NpcDisplayName = npcDisplayName;
        IsVisible = true;

        IsFavorite = _settings.FavoriteFaces.Contains((this.NpcFormKey, _parentVMModSetting.DisplayName));

        // START MODIFIED SECTION
        // Set initial selection state based on the consistency provider
        IsSelected = _consistencyProvider.IsModSelected(NpcFormKey, _parentVMModSetting.DisplayName, NpcFormKey);

        // Set initial border color and subscribe to future changes in selection
        BorderColor = IsSelected ? _selectedBrush : _deselectedBrush;
        this.WhenAnyValue(x => x.IsSelected)
            .Skip(1) // Skip the initial value
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(selected => BorderColor = selected ? _selectedBrush : _deselectedBrush)
            .DisposeWith(_disposables);

        // Freeze the brushes to make them thread-safe for background creation
        if (_selectedBrush.CanFreeze) _selectedBrush.Freeze();
        if (_deselectedBrush.CanFreeze) _deselectedBrush.Freeze();

        // Subscribe to global selection changes to keep this mugshot's border up to date
        _consistencyProvider.NpcSelectionChanged
            .Where(args => args.NpcFormKey == this.NpcFormKey)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(args => IsSelected = (args.SelectedModName == _parentVMModSetting.DisplayName &&
                                             args.SourceNpcFormKey.Equals(NpcFormKey)))
            .DisposeWith(_disposables);
        // END MODIFIED SECTION

        IsAmbiguousSource = isAmbiguousSource;
        CurrentSourcePlugin = currentSourcePlugin;

        if (IsAmbiguousSource && availableSourcePlugins != null)
        {
            AvailableSourcePlugins =
                new ObservableCollection<ModKey>(availableSourcePlugins.OrderBy(k => k.FileName.String));
        }

        // Determine if the provided imagePath is a real mugshot or the placeholder
        // Compare against the static FullPlaceholderPath
        bool isActualMugshotFile = !string.IsNullOrWhiteSpace(imagePath) &&
                                   File.Exists(imagePath) &&
                                   !imagePath.Equals(FullPlaceholderPath, StringComparison.OrdinalIgnoreCase);
        HasMugshot = isActualMugshotFile; // Set HasMugshot based on this check
        
        _ = LoadInitialImageAsync();

        this.WhenAnyValue(x => x.ModPageUrls.Count)
            .Select(count => count > 0)
            .ToPropertyEx(this, x => x.CanVisitModPage)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.ModPageUrls.Count)
            .Select(count => count == 1)
            .ToPropertyEx(this, x => x.HasSingleModPage)
            .DisposeWith(_disposables);

        // --- NEW: Parse meta.ini files ---
        foreach (var modPath in _parentVMModSetting.CorrespondingFolderPaths)
        {
            var metaPath = Path.Combine(modPath, "meta.ini");
            if (File.Exists(metaPath))
            {
                var (gameName, modId) = ParseMetaIni(metaPath);
                if (!string.IsNullOrWhiteSpace(gameName) && !string.IsNullOrWhiteSpace(modId))
                {
                    var url = $"https://www.nexusmods.com/{gameName}/mods/{modId}";
                    var folderName = Path.GetFileName(modPath.TrimEnd(Path.DirectorySeparatorChar));
                    ModPageUrls.Add(new ModPageInfo(folderName, url));
                }
            }
        }


        // ToggleFullScreenCommand can now operate on the placeholder too. Also
        // accept an in-memory MugshotSource (auto-generated tiles populate it
        // without going through ImagePath/File.Exists).
        var canToggleFullScreen =
            this.WhenAnyValue(x => x.ImagePath, x => x.MugshotSource,
                (path, src) => src != null
                    || (!string.IsNullOrWhiteSpace(path) && File.Exists(path)));
        ToggleFullScreenCommand =
            ReactiveCommand.Create(ToggleFullScreen, canToggleFullScreen).DisposeWith(_disposables);

        // 3D preview: any non-mugshot-only entry. Base Game ships with empty
        // CorrespondingFolderPaths (records + assets come from the vanilla
        // data folder and BSAs, which the renderer's vanilla scope already
        // covers), so we don't gate on folder count here.
        var canShow3DPreview = Observable.Return(
            !_parentVMModSetting.IsMugshotOnlyEntry
            && !npcFormKey.IsNull);
        Show3DPreviewCommand =
            ReactiveCommand.Create(Show3DPreview, canShow3DPreview).DisposeWith(_disposables);

        JumpToNpcCommand = ReactiveCommand.Create(JumpToNpc).DisposeWith(_disposables);

        var canSetNpcSource = this.WhenAnyValue(x => x.IsAmbiguousSource).Select(isAmbiguous => isAmbiguous);
        SetNpcSourcePluginCommand = ReactiveCommand.Create<ModKey>(SetNpcSourcePluginInternal, canSetNpcSource)
            .DisposeWith(_disposables);

        SelectSameSourcePluginWherePossibleCommand = ReactiveCommand.Create(() =>
            {
                if (this.CurrentSourcePlugin.HasValue)
                {
                    _parentVMModSetting.SetAndNotifySourcePluginForAll(this.CurrentSourcePlugin.Value);
                }
            },
            this.WhenAnyValue(x => x.IsAmbiguousSource, x => x.CurrentSourcePlugin,
                (ambiguous, source) => ambiguous && source.HasValue)).DisposeWith(_disposables);

        AddToFavoritesCommand = ReactiveCommand.Create(ToggleFavorite).DisposeWith(_disposables);

        OpenFolderCommand = ReactiveCommand.Create<string>(Auxilliary.OpenFolder).DisposeWith(_disposables);
        VisitModPageCommand = ReactiveCommand.Create<string>(Auxilliary.OpenUrl).DisposeWith(_disposables);

        ToggleFullScreenCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error showing image: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        JumpToNpcCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error jumping to NPC: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        SetNpcSourcePluginCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error setting NPC source plugin: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);

        AddToFavoritesCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error updating favorites: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        OpenFolderCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error opening folder: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        VisitModPageCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Could not open URL: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
    }
    
    // --- NEW METHOD: Loads the initial on-disk or placeholder image asynchronously ---
    public async Task LoadInitialImageAsync()
    {
        if (MugshotSource != null) return; // Already loaded

        string pathToLoad = (!string.IsNullOrWhiteSpace(ImagePath) && File.Exists(ImagePath))
            ? ImagePath
            : FullPlaceholderPath;

        if (!File.Exists(pathToLoad))
        {
            HasMugshot = false;
            return;
        }

        try
        {
            // An image is only considered a "real" mugshot if it's not the placeholder AND was not auto-generated.
            // Auto-generated images are treated as placeholders that need staleness checks.
            bool isRealFile = !pathToLoad.Equals(FullPlaceholderPath, StringComparison.OrdinalIgnoreCase);
            HasMugshot = isRealFile && !_portraitCreator.IsAutoGenerated(pathToLoad);

            // Read PNG metadata too if this is an Internal-renderer auto-generated PNG, so the
            // missing-asset overlay survives across app restarts. Decoupled from
            // the bitmap result so a malformed JSON doesn't prevent the image
            // from showing.
            bool tryReadAssetMeta = isRealFile && _portraitCreator.IsAutoGenerated(pathToLoad);

            // Load bitmap and dimensions on a background thread
            var loadResult = await Task.Run(() =>
            {
                var bmp = new BitmapImage();
                using (var stream = new FileStream(pathToLoad, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                }
                bmp.Freeze();

                var dimensions = ImagePacker.GetImageDimensions(pathToLoad);

                List<string> meshes = new();
                List<string> textures = new();
                if (tryReadAssetMeta)
                {
                    var json = MugshotPngMetadata.TryRead(pathToLoad);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        InternalMugshotMetadata.TryReadMissingAssets(json, out meshes, out textures);
                    }
                }

                return (bmp, dimensions, meshes, textures);
            });

            // Always apply (even with empty lists) so a re-load of a tile whose
            // PNG was regenerated without missing assets clears any stale
            // overlay state from the in-memory VM.
            if (tryReadAssetMeta)
            {
                ApplyMissingAssetNotifications(loadResult.meshes, loadResult.textures);
            }

            // Assign results back on the UI thread
            var bitmap = loadResult.bmp;
            var dims = loadResult.dimensions;
            MugshotSource = bitmap;
            OriginalPixelWidth = dims.PixelWidth;
            OriginalPixelHeight = dims.PixelHeight;
            OriginalDipWidth = dims.DipWidth;
            OriginalDipHeight = dims.DipHeight;
            OriginalDipDiagonal = Math.Sqrt(dims.DipWidth * dims.DipWidth + dims.DipHeight * dims.DipHeight);
            // MODIFICATION: Only set display dimensions if they haven't been set externally (e.g., by the packer)
            if (ImageWidth == 0 && ImageHeight == 0)
            {
                ImageWidth = OriginalDipWidth;
                ImageHeight = OriginalDipHeight;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in LoadInitialImageAsync for '{ImagePath}': {ExceptionLogger.GetExceptionStack(ex)}");
            HasMugshot = false;
        }
    }
    
    private void ToggleFullScreen()
    {
        if (MugshotSource == null && (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath)))
        {
            ScrollableMessageBox.ShowWarning("Mugshot image (or placeholder) not found or path is invalid.");
            return;
        }

        // Prioritize the in-memory source, otherwise fall back to the path.
        var fullScreenVM = MugshotSource != null
            ? new VM_FullScreenImage(MugshotSource)
            : new VM_FullScreenImage(ImagePath);

        var fullScreenView = Locator.Current.GetService<IViewFor<VM_FullScreenImage>>() as Window;
        if (fullScreenView != null)
        {
            fullScreenView.DataContext = fullScreenVM;
            fullScreenView.ShowDialog();
        }
        else
        {
            ScrollableMessageBox.ShowError("Could not create FullScreenImageView.");
        }
    }

    /// <summary>
    /// Launches the per-tile 3D preview popup scoped to this tile's source
    /// mod (records + assets resolved against
    /// <see cref="_parentVMModSetting"/>'s plugins / folders rather than
    /// the user's currently-selected appearance mod). The popup hosts
    /// <see cref="UC_InternalMugshotPreview"/> in a fresh
    /// <see cref="VM_InternalMugshotPreview"/> instance — its own GL
    /// context, independent of the Settings-panel preview, so the two can
    /// coexist without trampling each other's scene state.
    /// </summary>
    private void Show3DPreview()
    {
        try
        {
            var inner = _internalPreviewFactory();
            var modSetting = _parentVMModSetting.SaveToModel();
            var title = $"3D Preview — {NpcDisplayName} ({_parentVMModSetting.DisplayName})";
            var fsVm = new VM_FullScreen3DPreview(inner, _settings, title);

            if (Locator.Current.GetService<IViewFor<VM_FullScreen3DPreview>>() is not Window window)
            {
                ScrollableMessageBox.ShowError("Could not create FullScreen3DPreviewView.");
                return;
            }
            window.DataContext = fsVm;
            // Trigger the load AFTER the window's UC has been initialized so
            // its GLWpfControl is attached and ready to consume the queued
            // scene rebuild. Fire-and-forget — exceptions surface in the
            // inner VM's StatusText, no need to block here.
            window.Loaded += async (_, _) =>
            {
                try { await inner.LoadAsync(NpcFormKey, modSetting); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Show3DPreview: LoadAsync failed: {ExceptionLogger.GetExceptionStack(ex)}");
                }
            };
            window.ShowDialog();
            // The inner VM's Dispose tears down its VM_CharacterViewer +
            // GL state. Without this the renderer thread holds resources
            // until the GC runs, which could collide with a subsequent
            // Show3DPreview from the same tile.
            inner.Dispose();
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError(
                $"Failed to open 3D preview:\n{ExceptionLogger.GetExceptionStack(ex)}");
        }
    }

    private void ToggleFavorite()
    {
        var favoriteTuple = (this.NpcFormKey, _parentVMModSetting.DisplayName);
        if (IsFavorite)
        {
            _settings.FavoriteFaces.Remove(favoriteTuple);
            IsFavorite = false;
            Debug.WriteLine($"Removed {favoriteTuple} from favorites.");
        }
        else
        {
            _settings.FavoriteFaces.Add(favoriteTuple);
            IsFavorite = true;
            Debug.WriteLine($"Added {favoriteTuple} to favorites.");
        }
    }

    private void JumpToNpc()
    {
        _parentVMMaster.NavigateToNpc(NpcFormKey);
    }

    private void SetNpcSourcePluginInternal(ModKey selectedPluginKey)
    {
        if (!IsAmbiguousSource)
        {
            Debug.WriteLine(
                $"SetNpcSourcePluginInternal called for non-ambiguous NPC {NpcFormKey}. This should not happen.");
            return;
        }

        if (selectedPluginKey.IsNull)
        {
            Debug.WriteLine($"SetNpcSourcePluginInternal called with a null/invalid ModKey for NPC {NpcFormKey}.");
            return;
        }

        // Call back to the parent VM_ModSetting to handle the logic
        // It returns true if the underlying data was actually changed and RefreshNpcLists was called.
        bool successfullyUpdated = _parentVMModSetting.SetSingleNpcSourcePlugin(NpcFormKey, selectedPluginKey);

        if (successfullyUpdated)
        {
            // The parent VM_ModSetting has updated its NpcSourcePluginMap.
            // Now, this specific VM_ModsMenuMugshot instance should update its own CurrentSourcePlugin
            // to reflect the new choice for the context menu checkmark.
            // We can re-fetch it from the parent's map.
            if (_parentVMModSetting.NpcPluginDisambiguation.TryGetValue(this.NpcFormKey, out var newResolvedSource))
            {
                this.CurrentSourcePlugin = newResolvedSource;
            }
            else
            {
                // This case should be rare if SetSingleNpcSourcePlugin succeeded and RefreshNpcLists ran.
                // It implies the NPC might have been removed or is no longer ambiguous after the refresh.
                // For safety, set to null or the passed key.
                this.CurrentSourcePlugin = selectedPluginKey;
                Debug.WriteLine(
                    $"Warning: Could not re-resolve source for NPC {NpcFormKey} from NpcSourcePluginMap after setting. Displayed checkmark might be based on direct selection.");
            }
        }
        // No need to call anything on _parentVMMaster (VM_Mods) to refresh the whole panel.
    }

    private async Task HandleSuccessfulDownload(byte[] imageData, FaceFinderResult faceData, string baseSavePath,
        string saveFolder, CancellationToken token)
    {
        string finalImagePath;
        if (_settings.CacheFaceFinderImages)
        {
            var format = Image.DetectFormat(imageData);
            var extension = format?.FileExtensions.FirstOrDefault() ?? "png";
            finalImagePath = $"{baseSavePath}.{extension}";

            Directory.CreateDirectory(Path.GetDirectoryName(finalImagePath)!);
            try
            {
                await File.WriteAllBytesAsync(finalImagePath, imageData, token);
                // WriteMetadataAsync also adds the path to CachedFaceFinderPaths
                // on its own, but goes via a bare HashSet.Add — wrap with the
                // FaceFinder tracker afterwards so the addition fires
                // RequestThrottledSave for crash-safe persistence.
                await _faceFinderClient.WriteMetadataAsync(finalImagePath, faceData);
                _faceFinderTracker.Track(finalImagePath);
            }
            catch
            {
                // Partial-write defense scoped to the FaceFinder cache (NOT
                // GeneratedMugshotPaths — these two cache buckets are
                // deliberately disjoint so "Delete All Auto-Generated" and
                // "Delete Cached FaceFinder Images" don't cross-delete).
                _faceFinderTracker.TrackIfFileExists(finalImagePath);
                throw;
            }

            SetImageSource(finalImagePath, isPlaceholder: false);
            Debug.WriteLine($"Downloaded and cached mugshot for {NpcFormKey} as .{extension}");
        }
        else
        {
            finalImagePath = "in-memory";
            SetImageSourceFromMemory(imageData);
            Debug.WriteLine($"Downloaded mugshot for {NpcFormKey} into memory (no cache).");
        }

        await UpdateUIAfterSuccess(finalImagePath, saveFolder,
            _settings.CacheFaceFinderImages); // don't set the mugshot folder if image wasn't cached
    }

    private async Task UpdateUIAfterSuccess(string imagePath, string saveFolder, bool addToMugshotFolders)
    {
        _npcSelectionBar.UpdateMugshotCache(this.NpcFormKey, _parentVMModSetting.DisplayName, imagePath);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _parentVMModSetting.HasValidMugshots = true;
            if (addToMugshotFolders && !_parentVMModSetting.MugShotFolderPaths.Contains(saveFolder))
            {
                _parentVMModSetting.MugShotFolderPaths.Add(saveFolder);
            }
        });
    }

    public async Task LoadRealImageAsync()
    {
        try
        {
            IsLoading = true;
            if (_cancellationToken.IsCancellationRequested) return;

            // 1. SETUP: Define paths and find any existing local file
            // Snippet from LoadRealImageAsync in VM_ModsMenuMugshot.cs

            // 1. SETUP: Define paths and find any existing local file
            var baseCacheFolder = string.IsNullOrWhiteSpace(_settings.MugshotsFolder)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FaceFinder Cache")
                : _settings.MugshotsFolder;
            var saveFolder = Path.Combine(baseCacheFolder, _parentVMModSetting.DisplayName);
            var baseSavePath = Path.Combine(saveFolder, NpcFormKey.ModKey.ToString(), $"{NpcFormKey.ID:X8}");

            var existingCachedFile = Auxilliary.FindExistingCachedImage(baseSavePath);
            string nifPath = await _portraitCreator.FindNpcNifPath(NpcFormKey, _parentVMModSetting);

            // 2. CHECK LOCAL: See if the existing file is valid and up-to-date
            if (existingCachedFile != null)
            {
                var metadata = await _faceFinderClient.ReadMetadataAsync(existingCachedFile);
                if (metadata?.ExternalUrl != null && ModPageUrls.All(p => p.Url != metadata.ExternalUrl))
                {
                    ModPageUrls.Add(new ModPageInfo("FaceFinder", metadata.ExternalUrl));
                }
                
                bool autoGenerated =
                    ParentVMModSetting.DisplayName == VM_Mods.BaseGameModSettingName || ParentVMModSetting.DisplayName == VM_Mods.CreationClubModsettingName;

                bool needsRegen = _settings.UsePortraitCreatorFallback &&
                                  _stalenessChecker.NeedsRegeneration(existingCachedFile, NpcFormKey, nifPath,
                                      _parentVMModSetting.CorrespondingFolderPaths, autoGenerated);
                bool isStale = _settings.UseFaceFinderFallback &&
                               await _faceFinderClient.IsCacheStaleAsync(existingCachedFile, NpcFormKey,
                                   _parentVMModSetting.DisplayName);

                if (!needsRegen && !isStale)
                {
                    Debug.WriteLine($"Using valid cached mugshot: {Path.GetFileName(existingCachedFile)}");
                    if (ImagePath != existingCachedFile)
                    {
                        SetImageSource(existingCachedFile, isPlaceholder: false);
                    }

                    return; // HAPPY PATH: Valid local file found, exit early.
                }
            }

            // 3. FALLBACK 1: Try FaceFinder API
            if (_settings.UseFaceFinderFallback)
            {
                var faceData = await _faceFinderClient.GetFaceDataAsync(NpcFormKey, _parentVMModSetting.DisplayName);
                if (faceData != null && !string.IsNullOrWhiteSpace(faceData.ImageUrl))
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(faceData.ExternalUrl) &&
                            ModPageUrls.All(p => p.Url != faceData.ExternalUrl))
                        {
                            ModPageUrls.Add(new ModPageInfo("FaceFinder", faceData.ExternalUrl));
                        }

                        using var client = new HttpClient();
                        var imageData = await client.GetByteArrayAsync(faceData.ImageUrl, _cancellationToken);
                        await HandleSuccessfulDownload(imageData, faceData, baseSavePath, saveFolder, _cancellationToken);
                        return; // SUCCESS: Image downloaded, exit.
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to download from FaceFinder for {NpcFormKey}: {ExceptionLogger.GetExceptionStack(ex)}");
                    }
                }
            }

            // 4. FALLBACK 2: Try NPC Portrait Creator

            var baseAutoGenFolder = string.IsNullOrWhiteSpace(_settings.MugshotsFolder)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AutoGen Mugshots")
                : _settings.MugshotsFolder;
            saveFolder = Path.Combine(baseAutoGenFolder, ParentVMModSetting.DisplayName);
            var pngSavePath = Path.Combine(saveFolder, NpcFormKey.ModKey.ToString(), $"{NpcFormKey.ID:X8}.png");

            if (_settings.UsePortraitCreatorFallback)
            {
                bool autoGen =
                    ParentVMModSetting.DisplayName == VM_Mods.BaseGameModSettingName ||
                    ParentVMModSetting.DisplayName == VM_Mods.CreationClubModsettingName;

                // Skip regeneration if there's already a fresh PNG at the AutoGen path
                // for the active renderer; pick up the existing file instead.
                if (File.Exists(pngSavePath) &&
                    !_stalenessChecker.NeedsRegeneration(pngSavePath, NpcFormKey, nifPath,
                        _parentVMModSetting.CorrespondingFolderPaths, autoGen))
                {
                    SetImageSource(pngSavePath, isPlaceholder: false);
                    await UpdateUIAfterSuccess(pngSavePath, saveFolder, true);
                    return;
                }

                bool generated = false;

                if (_settings.SelectedRenderer == MugshotRenderer.Internal)
                {
                    // Tile's source mod — every tile must render its own mod's
                    // appearance (not the user's currently-selected mod).
                    var sourceMod = _settings.ModSettings.FirstOrDefault(m => m.DisplayName == _parentVMModSetting.DisplayName);
                    var missingMeshes = new List<string>();
                    var missingTextures = new List<string>();
                    generated = await _internalMugshotGenerator.GenerateAsync(
                        NpcFormKey, sourceMod, pngSavePath, _cancellationToken,
                        missingMeshes, missingTextures);
                    ApplyMissingAssetNotifications(missingMeshes, missingTextures);
                }
                else if (!string.IsNullOrWhiteSpace(nifPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pngSavePath)!);
                    generated = await _portraitCreator.GeneratePortraitAsync(nifPath, _parentVMModSetting.CorrespondingFolderPaths,
                        pngSavePath, _cancellationToken);
                }

                if (generated)
                {
                    Debug.WriteLine($"Generated mugshot for {NpcFormKey}.");
                    SetImageSource(pngSavePath, isPlaceholder: false);
                    await UpdateUIAfterSuccess(pngSavePath, saveFolder, true);
                    return; // SUCCESS: Image generated, exit.
                }
            }
        }
        catch (TaskCanceledException)
        {
            /* Swallow cancellation */
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading real image for {NpcFormKey}: {ExceptionLogger.GetExceptionStack(ex)}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Sets the unified missing-asset overlay state from the two
    /// lists the internal mugshot generator populated. Both empty clears
    /// the overlay; otherwise the overlay shows and the tooltip lists
    /// each kind under its own heading, omitting any section with no
    /// entries so the tooltip stays compact.</summary>
    private void ApplyMissingAssetNotifications(
        IReadOnlyList<string> missingMeshes,
        IReadOnlyList<string> missingTextures)
    {
        bool hasMeshes = missingMeshes != null && missingMeshes.Count > 0;
        bool hasTextures = missingTextures != null && missingTextures.Count > 0;
        if (!hasMeshes && !hasTextures)
        {
            HasMissingAssets = false;
            MissingAssetNotificationText = string.Empty;
            return;
        }

        var sb = new System.Text.StringBuilder();
        if (hasMeshes)
        {
            sb.Append("The following expected mesh paths could not be found:");
            foreach (var p in missingMeshes) sb.Append('\n').Append(p);
        }
        if (hasTextures)
        {
            if (hasMeshes) sb.Append("\n\n");
            sb.Append("The following expected texture paths could not be found:");
            foreach (var p in missingTextures) sb.Append('\n').Append(p);
        }

        HasMissingAssets = true;
        MissingAssetNotificationText = sb.ToString();
    }

    private void SetImageSource(string path, bool isPlaceholder)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        // This is the key to updating the UI from a background thread.
        // We create and freeze the BitmapImage, which makes it thread-safe.
        var bitmap = new BitmapImage();
        // Load the image via a FileStream to bypass WPF's URI caching.
        // This ensures that if the file is overwritten on disk, we load the new version.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // needed to release the file lock after loading
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }

        bitmap.Freeze(); // IMPORTANT: Makes the image cross-thread accessible
        this.MugshotSource = bitmap;
        this.ImagePath = path;
        this.HasMugshot = !isPlaceholder;
        
        var (pixelWidth, pixelHeight, dipWidth, dipHeight) = ImagePacker.GetImageDimensions(path);
        OriginalPixelWidth = pixelWidth;
        OriginalPixelHeight = pixelHeight;
        OriginalDipWidth = dipWidth;
        OriginalDipHeight = dipHeight;
        OriginalDipDiagonal = Math.Sqrt(dipWidth * dipWidth + dipHeight * dipHeight);
        ImageWidth = OriginalDipWidth;
        ImageHeight = OriginalDipHeight;
        
    }

    private void SetImageSourceFromMemory(byte[] imageData)
    {
        if (imageData == null || imageData.Length == 0) return;

        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(imageData))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }

        bitmap.Freeze();

        this.MugshotSource = bitmap;
        this.ImagePath = "in-memory";
        this.HasMugshot = true; // A real image was loaded

        var info = Image.Identify(imageData);
        OriginalPixelWidth = info.Width;
        OriginalPixelHeight = info.Height;
        OriginalDipWidth = info.Width;
        OriginalDipHeight = info.Height;
        OriginalDipDiagonal = Math.Sqrt(OriginalDipWidth * OriginalDipWidth + OriginalDipHeight * OriginalDipHeight);
        

    }

    private (string? gameName, string? modId) ParseMetaIni(string filePath)
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
            Debug.WriteLine($"Error parsing {filePath}: {ExceptionLogger.GetExceptionStack(ex)}");
        }

        return (gameName, modId);
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}