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
    private readonly ImagePacker _imagePacker;
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
        ImagePacker imagePacker
    )
    {
        _parentVMMaster = parentVMMaster;
        _parentVMModSetting = parentVMModSetting;
        _consistencyProvider = consistencyProvider;
        _settings = settings;
        _npcSelectionBar = npcSelectionBar;
        _faceFinderClient = faceFinderClient;
        _portraitCreator = portraitCreator;
        _imagePacker = imagePacker;
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


        // ToggleFullScreenCommand can now operate on the placeholder too.
        var canToggleFullScreen =
            this.WhenAnyValue(x => x.ImagePath, path => !string.IsNullOrEmpty(path) && File.Exists(path));
        ToggleFullScreenCommand =
            ReactiveCommand.Create(ToggleFullScreen, canToggleFullScreen).DisposeWith(_disposables);
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
        VisitModPageCommand = ReactiveCommand.Create<string>(Auxilliary.OpenUrl);

        ToggleFullScreenCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error showing image: {ex.Message}"))
            .DisposeWith(_disposables);
        JumpToNpcCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error jumping to NPC: {ex.Message}"))
            .DisposeWith(_disposables);
        SetNpcSourcePluginCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error setting NPC source plugin: {ex.Message}"))
            .DisposeWith(_disposables);

        AddToFavoritesCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error updating favorites: {ex.Message}"))
            .DisposeWith(_disposables);
        OpenFolderCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error opening folder: {ex.Message}"))
            .DisposeWith(_disposables);
        VisitModPageCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Could not open URL: {ex.Message}"))
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
            // Set HasMugshot based on whether we're loading a real image
            HasMugshot = !pathToLoad.Equals(FullPlaceholderPath, StringComparison.OrdinalIgnoreCase);

            // Load bitmap and dimensions on a background thread
            var (bitmap, dims) = await Task.Run(() =>
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
                return (bmp, dimensions);
            });
            
            // Assign results back on the UI thread
            MugshotSource = bitmap;
            OriginalPixelWidth = dims.PixelWidth;
            OriginalPixelHeight = dims.PixelHeight;
            OriginalDipWidth = dims.DipWidth;
            OriginalDipHeight = dims.DipHeight;
            OriginalDipDiagonal = Math.Sqrt(dims.DipWidth * dims.DipWidth + dims.DipHeight * dims.DipHeight);
            ImageWidth = OriginalDipWidth;
            ImageHeight = OriginalDipHeight;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in LoadInitialImageAsync for '{ImagePath}': {ex.Message}");
            HasMugshot = false;
        }
    }

    private void ToggleFullScreen()
    {
        // ImagePath now correctly points to either real image or placeholder
        if (!string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath))
        {
            var fullScreenVM = new VM_FullScreenImage(ImagePath);
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
        else
        {
            ScrollableMessageBox.ShowWarning(
                $"Mugshot image (or placeholder) not found or path is invalid:\n{ImagePath}");
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
        string saveFolder)
    {
        string finalImagePath;
        if (_settings.CacheFaceFinderImages)
        {
            var format = Image.DetectFormat(imageData);
            var extension = format?.FileExtensions.FirstOrDefault() ?? "png";
            finalImagePath = $"{baseSavePath}.{extension}";

            Directory.CreateDirectory(Path.GetDirectoryName(finalImagePath)!);
            await File.WriteAllBytesAsync(finalImagePath, imageData);
            await _faceFinderClient.WriteMetadataAsync(finalImagePath, faceData);

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

                bool needsRegen = _settings.UsePortraitCreatorFallback &&
                                  _portraitCreator.NeedsRegeneration(existingCachedFile, nifPath, _parentVMModSetting.CorrespondingFolderPaths);
                bool isStale = _settings.UseFaceFinderFallback &&
                               await _faceFinderClient.IsCacheStaleAsync(existingCachedFile, NpcFormKey,
                                   _parentVMModSetting.DisplayName, _settings.FaceFinderApiKey);

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
                var faceData = await _faceFinderClient.GetFaceDataAsync(NpcFormKey, _parentVMModSetting.DisplayName,
                    _settings.FaceFinderApiKey);
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
                        await HandleSuccessfulDownload(imageData, faceData, baseSavePath, saveFolder);
                        return; // SUCCESS: Image downloaded, exit.
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to download from FaceFinder for {NpcFormKey}: {ex.Message}");
                    }
                }
            }

            // 4. FALLBACK 2: Try NPC Portrait Creator

            var baseAutoGenFolder = string.IsNullOrWhiteSpace(_settings.MugshotsFolder)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AutoGen Mugshots")
                : _settings.MugshotsFolder;
            saveFolder = Path.Combine(baseAutoGenFolder, ParentVMModSetting.DisplayName);
            var pngSavePath = Path.Combine(saveFolder, NpcFormKey.ModKey.ToString(), $"{NpcFormKey.ID:X8}.png");

            if (_settings.UsePortraitCreatorFallback && !string.IsNullOrWhiteSpace(nifPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pngSavePath)!);
                if (await _portraitCreator.GeneratePortraitAsync(nifPath, _parentVMModSetting.CorrespondingFolderPaths,
                        pngSavePath))
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
            Debug.WriteLine($"Error loading real image for {NpcFormKey}: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
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
            Debug.WriteLine($"Error parsing {filePath}: {ex.Message}");
        }

        return (gameName, modId);
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}