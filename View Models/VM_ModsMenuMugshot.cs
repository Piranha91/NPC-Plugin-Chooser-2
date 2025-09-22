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
using NPC_Plugin_Chooser_2.Models; // For Debug.WriteLine

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
    public string MugshotFolderPath => HasMugshot && !string.IsNullOrEmpty(ImagePath) ? Path.GetDirectoryName(ImagePath) : string.Empty;

    public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
    public ReactiveCommand<Unit, Unit> JumpToNpcCommand { get; }
    public ReactiveCommand<ModKey, Unit> SetNpcSourcePluginCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectSameSourcePluginWherePossibleCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToFavoritesCommand { get; }
    public ReactiveCommand<string, Unit> OpenFolderCommand { get; }

    // Static path for placeholder, consistent with VM_Mods
    private const string PlaceholderResourceRelativePath = @"Resources\No Mugshot.png";

    private static readonly string FullPlaceholderPath =
        Path.Combine(AppContext.BaseDirectory, PlaceholderResourceRelativePath);

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
        PortraitCreator portraitCreator
    )
    {
        _parentVMMaster = parentVMMaster;
        _parentVMModSetting = parentVMModSetting;
        _consistencyProvider = consistencyProvider;
        _settings = settings;
        _npcSelectionBar = npcSelectionBar;
        _faceFinderClient = faceFinderClient;
        _portraitCreator = portraitCreator;
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

        // Load dimensions for the ImagePath (whether it's real or placeholder)
        if (!string.IsNullOrWhiteSpace(ImagePath) && File.Exists(ImagePath))
        {
            try
            {
                var (pixelWidth, pixelHeight, dipWidth, dipHeight) = ImagePacker.GetImageDimensions(ImagePath);
                OriginalPixelWidth = pixelWidth;
                OriginalPixelHeight = pixelHeight;
                OriginalDipWidth = dipWidth;
                OriginalDipHeight = dipHeight;
                OriginalDipDiagonal = Math.Sqrt(dipWidth * dipWidth + dipHeight * dipHeight);
                ImageWidth = OriginalDipWidth;
                ImageHeight = OriginalDipHeight;

                // --- ADD THIS INITIALIZATION LOGIC ---
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(ImagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // Releases file lock
                bitmap.EndInit();
                bitmap.Freeze(); // Good practice for performance
                this.MugshotSource = bitmap;
                // --- END OF ADDED LOGIC ---
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting dimensions for '{ImagePath}' in VM_ModsMenuMugshot: {ex.Message}");
                // If dimensions fail even for a placeholder, something is wrong. Clear out.
                ImageWidth = 0;
                ImageHeight = 0;
                HasMugshot = false; // Ensure this is false if image load fails
                OriginalPixelWidth = 0;
                OriginalPixelHeight = 0;
                OriginalDipWidth = 0;
                OriginalDipHeight = 0;
                OriginalDipDiagonal = 0;
                ImagePath = string.Empty; // Prevent trying to display a broken path
            }
        }
        else // Path was null, empty, or file doesn't exist (should be rare if VM_Mods passes valid placeholder)
        {
            ImageWidth = 0;
            ImageHeight = 0;
            HasMugshot = false;
            OriginalPixelWidth = 0;
            OriginalPixelHeight = 0;
            OriginalDipWidth = 0;
            OriginalDipHeight = 0;
            OriginalDipDiagonal = 0;
            ImagePath = string.Empty;
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
    
    public async Task LoadRealImageAsync()
    {
        // This method contains the full fallback logic, running in the background.

        bool hasRealMugshot = ImagePath != VM_Mods.FullPlaceholderPath;
        string nifPath = _portraitCreator.FindNpcNifPath(NpcFormKey, _parentVMModSetting.CorrespondingFolderPaths); 

        if (hasRealMugshot && !_portraitCreator.NeedsRegeneration(ImagePath, nifPath))
        {
            return;
        }
        
        try
        {
            IsLoading = true;
            
            if (_cancellationToken.IsCancellationRequested) return;
            // --- 1. Check local Mugshots folder ---

            var expectedFileName = $"{NpcFormKey.ID:X8}.png";
            
            var saveFolder = Path.Combine(_settings.MugshotsFolder,
                _parentVMModSetting.DisplayName);
            var savePath = Path.Combine(saveFolder,
                NpcFormKey.ModKey.ToString(),
                expectedFileName);

            // --- 2. Fallback to FaceFinder API ---
            if (_settings.UseFaceFinderFallback)
            {
                // Check cache first if enabled
                if (_settings.CacheFaceFinderImages && File.Exists(savePath))
                {
                    bool isStale =
                        await _faceFinderClient.IsCacheStaleAsync(savePath, NpcFormKey, _settings.FaceFinderApiKey);
                    if (!isStale)
                    {
                        Debug.WriteLine($"Using cached mugshot for {NpcFormKey} from FaceFinder.");
                        SetImageSource(savePath, isPlaceholder: false);
                        _npcSelectionBar.UpdateMugshotCache(this.NpcFormKey, _parentVMModSetting.DisplayName, savePath);
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                            _parentVMModSetting.HasValidMugshots = true);
                        return; // Exit early
                    }
                }

                var faceData = await _faceFinderClient.GetFaceDataAsync(NpcFormKey, _settings.FaceFinderApiKey);
                bool downloadSuccessful = false;
                if (faceData != null && !string.IsNullOrWhiteSpace(faceData.ImageUrl))
                {
                    try
                    {
                        using var client = new HttpClient();
                        var imageData = await client.GetByteArrayAsync(faceData.ImageUrl);
                        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                        await File.WriteAllBytesAsync(savePath, imageData);
                        Debug.WriteLine($"Downloaded mugshot for {NpcFormKey} from FaceFinder.");
                        downloadSuccessful = true;

                        // Write metadata if caching is enabled
                        if (_settings.CacheFaceFinderImages)
                        {
                            await _faceFinderClient.WriteMetadataAsync(savePath, faceData);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to download from FaceFinder for {NpcFormKey}: {ex.Message}");
                    }
                }

                if (downloadSuccessful)
                {
                    SetImageSource(savePath, isPlaceholder: false);
                    _npcSelectionBar.UpdateMugshotCache(this.NpcFormKey, _parentVMModSetting.DisplayName, savePath);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _parentVMModSetting.HasValidMugshots = true;
                        if (!_parentVMModSetting.MugShotFolderPaths.Contains(
                                Path.GetDirectoryName(Path.GetDirectoryName(savePath))))
                        {
                            _parentVMModSetting.MugShotFolderPaths.Add(
                                Path.GetDirectoryName(Path.GetDirectoryName(savePath))!);
                        }
                    });
                    return;
                }
            }

            // --- 3. Fallback to NPC Portrait Creator ---
            if (_settings.UsePortraitCreatorFallback && _portraitCreator.NeedsRegeneration(savePath, nifPath))
            {
                bool generationSuccessful = false;
                
                if (!string.IsNullOrWhiteSpace(nifPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                    if (await _portraitCreator.GeneratePortraitAsync(nifPath, _parentVMModSetting.CorrespondingFolderPaths, savePath))
                    {
                        Debug.WriteLine($"Generated mugshot for {NpcFormKey}.");
                        generationSuccessful = true;
                    }
                }

                if (generationSuccessful) // Or if (generationSuccessful) for the second block
                {
                    // This is safe because it freezes the bitmap before assigning it
                    SetImageSource(savePath, isPlaceholder: false);
                    _npcSelectionBar.UpdateMugshotCache(this.NpcFormKey, _parentVMModSetting.DisplayName, savePath);

                    // Dispatch the property and collection changes to the UI thread
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _parentVMModSetting.HasValidMugshots = true;
                        if (!_parentVMModSetting.MugShotFolderPaths.Contains(saveFolder))
                        {
                            _parentVMModSetting.MugShotFolderPaths.Add(saveFolder);
                        }
                    });
                    return;
                }
            }
        }

        catch (TaskCanceledException) { /* Swallow cancellation */ }

        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading real image for {NpcFormKey}: {ex.Message}");
        }
        
        finally
        {
            IsLoading = false; // Signal that loading has finished
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
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}