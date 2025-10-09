// View Models/VM_SummaryMugshot.cs
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Windows;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat;
using System.Net.Http;
using SixLabors.ImageSharp;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Reactive.Disposables;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.View_Models;

    [DebuggerDisplay("{NpcDisplayName} -> {ModDisplayName}")]
    public class VM_SummaryMugshot : ReactiveObject, IHasMugshotImage, IDisposable
    {
        private readonly CompositeDisposable _disposables = new();
        private readonly Lazy<VM_MainWindow> _lazyMainWindowVm;
        private readonly VM_ModSetting? _associatedModSetting;
        private readonly Settings _settings;
        private readonly FaceFinderClient _faceFinderClient;
        private readonly PortraitCreator _portraitCreator;

        // IHasMugshotImage Implementation
        public string ImagePath { get; set; }
        [Reactive] public double ImageWidth { get; set; }
        [Reactive] public double ImageHeight { get; set; }
        public int OriginalPixelWidth { get; set; }
        public int OriginalPixelHeight { get; set; }
        public double OriginalDipWidth { get; set; }
        public double OriginalDipHeight { get; set; }
        public double OriginalDipDiagonal { get; set; }
        public bool IsVisible { get; set; } = true;
        [Reactive] public ImageSource? MugshotSource { get; set; }
        [Reactive] public bool IsSelected { get; set; } // for use by Favorites menu

        // Display Properties
        public FormKey TargetNpcFormKey { get; }
        public string NpcDisplayName { get; }
        public string ModDisplayName { get; }
        public string SourceNpcDisplayName { get; }
        [Reactive] public bool IsLoading { get; private set; }
        public FormKey SourceNpcFormKey { get; }

        // Decorator Icon Properties
        public bool IsGuestAppearance { get; }
        public bool IsAmbiguousSource { get; }
        public bool HasIssueNotification { get; }
        public string IssueNotificationText { get; }
        public bool HasNoData { get; }
        public string NoDataNotificationText { get; }
        [Reactive] public bool HasMugshot { get; private set; }
        
        // --- 2. Add properties and command for Mod Page links ---
        public record ModPageInfo(string DisplayName, string Url);
        public ObservableCollection<ModPageInfo> ModPageUrls { get; } = new();
        [ObservableAsProperty] public bool CanVisitModPage { get; }
        [ObservableAsProperty] public bool HasSingleModPage { get; }

        // Commands
        public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
        public ReactiveCommand<Unit, Unit> JumpToNpcCommand { get; }
        public ReactiveCommand<Unit, Unit> JumpToModCommand { get; }
        public ReactiveCommand<string, Unit> VisitModPageCommand { get; }
        
        // --- NEW: Placeholder Image Configuration --- 
        private const string PlaceholderResourceRelativePath = @"Resources\No Mugshot.png";
        private static readonly string FullPlaceholderPath = Path.Combine(AppContext.BaseDirectory, PlaceholderResourceRelativePath);
        

        public VM_SummaryMugshot(
            string imagePath,
            FormKey targetNpcFormKey,
            FormKey sourceNpcFormKey,
            string npcDisplayName,
            string modDisplayName,
            string sourceNpcDisplayName,
            bool isGuest, bool isAmbiguous, bool hasIssue, string issueText, bool hasNoData, string noDataText, bool hasMugshot,
            Lazy<VM_MainWindow> lazyMainWindowVm,
            VM_Mods modsViewModel,
            VM_ModSetting? associatedModSetting,
            Settings settings,                   
            FaceFinderClient faceFinderClient,   
            PortraitCreator portraitCreator)
        {
            _lazyMainWindowVm = lazyMainWindowVm;
            _associatedModSetting = associatedModSetting;
            _settings = settings;
            _faceFinderClient = faceFinderClient;
            _portraitCreator = portraitCreator;

            ImagePath = imagePath;
            TargetNpcFormKey = targetNpcFormKey;
            NpcDisplayName = npcDisplayName;
            ModDisplayName = modDisplayName;
            SourceNpcDisplayName = sourceNpcDisplayName;
            SourceNpcFormKey = sourceNpcFormKey;

            IsGuestAppearance = isGuest;
            IsAmbiguousSource = isAmbiguous;
            HasIssueNotification = hasIssue;
            IssueNotificationText = issueText;
            HasNoData = hasNoData;
            NoDataNotificationText = noDataText;
            HasMugshot = hasMugshot;

            // Asynchronously load the initial image (placeholder or real) without blocking the constructor.
            _ = LoadInitialImageAsync(); 
            
            this.WhenAnyValue(x => x.ModPageUrls.Count).Select(count => count > 0).ToPropertyEx(this, x => x.CanVisitModPage).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.ModPageUrls.Count).Select(count => count == 1).ToPropertyEx(this, x => x.HasSingleModPage).DisposeWith(_disposables);
            VisitModPageCommand = ReactiveCommand.Create<string>(Auxilliary.OpenUrl);

            if (_associatedModSetting != null)
            {
                foreach (var modPath in _associatedModSetting.CorrespondingFolderPaths)
                {
                    var metaPath = Path.Combine(modPath, "meta.ini");
                    if (File.Exists(metaPath))
                    {
                        // (This assumes you have a helper method to parse the ini file)
                        var (gameName, modId) = Auxilliary.ParseMetaIni(metaPath);
                        if (!string.IsNullOrWhiteSpace(gameName) && !string.IsNullOrWhiteSpace(modId))
                        {
                            var url = $"https://www.nexusmods.com/{gameName}/mods/{modId}";
                            var folderName = Path.GetFileName(modPath.TrimEnd(Path.DirectorySeparatorChar));
                            ModPageUrls.Add(new ModPageInfo(folderName, url));
                        }
                    }
                }
            }

            // Command Implementations
            ToggleFullScreenCommand = ReactiveCommand.Create(() =>
            {
                // Prioritize the in-memory source if it exists, otherwise fall back to the path
                var fullScreenVM = MugshotSource != null 
                    ? new VM_FullScreenImage(MugshotSource) 
                    : new VM_FullScreenImage(ImagePath);

                var fullScreenView = Locator.Current.GetService<IViewFor<VM_FullScreenImage>>() as Window;
                if (fullScreenView != null)
                {
                    fullScreenView.DataContext = fullScreenVM;
                    fullScreenView.ShowDialog();
                }
            }, this.WhenAnyValue(x => x.HasMugshot)).DisposeWith(_disposables);

            JumpToNpcCommand = ReactiveCommand.Create(() =>
            {
                modsViewModel.NavigateToNpc(TargetNpcFormKey);
            }).DisposeWith(_disposables);

            JumpToModCommand = ReactiveCommand.Create(() =>
            {
                var targetMod = modsViewModel.AllModSettings.FirstOrDefault(m => m.DisplayName.Equals(ModDisplayName, StringComparison.OrdinalIgnoreCase));
                if (targetMod != null)
                {
                    _lazyMainWindowVm.Value.IsModsTabSelected = true;
                    // Give the UI time to switch tabs before signaling the scroll
                    RxApp.MainThreadScheduler.Schedule(TimeSpan.FromMilliseconds(100), () =>
                    {
                        modsViewModel.ShowMugshotsCommand.Execute(targetMod).Subscribe();
                    });
                }
            }).DisposeWith(_disposables);
        }
        
        public async Task LoadInitialImageAsync()
        {
            if (MugshotSource != null) return; // Already loaded

            string pathToLoad = (!string.IsNullOrWhiteSpace(ImagePath) && File.Exists(ImagePath))
                ? ImagePath
                : FullPlaceholderPath;

            if (!File.Exists(pathToLoad))
            {
                HasMugshot = false; // Cannot display anything
                return;
            }

            try
            {
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
                Debug.WriteLine($"Error in LoadInitialImageAsync for '{ImagePath}': {ExceptionLogger.GetExceptionStack(ex)}");
                HasMugshot = false;
            }
        }
        
        // --- RENAMED METHOD ---
        public async Task LoadRealImageAsync(CancellationToken token)
    {
        // This method's purpose is to generate a real image if one doesn't already exist.
        if (HasMugshot) return;
        
        IsLoading = true;

        try
        {
            if (token.IsCancellationRequested) return;

            // --- FaceFinder Fallback ---
            if (_settings.UseFaceFinderFallback)
            {
                var faceData = await _faceFinderClient.GetFaceDataAsync(SourceNpcFormKey, this.ModDisplayName, _settings.FaceFinderApiKey);
                if (faceData != null && !string.IsNullOrWhiteSpace(faceData.ImageUrl))
                {
                    using var client = new HttpClient();
                    var imageData = await client.GetByteArrayAsync(faceData.ImageUrl, token);
                    SetImageSourceFromMemory(imageData);
                    // (Optionally add logic here to cache the downloaded image)
                    if (!string.IsNullOrWhiteSpace(faceData.ExternalUrl) && ModPageUrls.All(p => p.Url != faceData.ExternalUrl))
                    {
                        ModPageUrls.Add(new ModPageInfo("FaceFinder", faceData.ExternalUrl));
                    }
                    return;
                }
            }
            
            if (token.IsCancellationRequested) return;

            // --- PortraitCreator Fallback ---
            if (_settings.UsePortraitCreatorFallback && _associatedModSetting != null)
            {
                var baseAutoGenFolder = string.IsNullOrWhiteSpace(_settings.MugshotsFolder)
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AutoGen Mugshots")
                    : _settings.MugshotsFolder;
                var saveFolder = Path.Combine(baseAutoGenFolder, this.ModDisplayName);
                var savePath = Path.Combine(saveFolder, SourceNpcFormKey.ModKey.ToString(), $"{SourceNpcFormKey.ID:X8}.png");

                string nifPath = await _portraitCreator.FindNpcNifPath(this.SourceNpcFormKey, _associatedModSetting);
                bool autoGenerated =
                    ModDisplayName == VM_Mods.BaseGameModSettingName || ModDisplayName == VM_Mods.CreationClubModsettingName;
                if (!string.IsNullOrWhiteSpace(nifPath) && _portraitCreator.NeedsRegeneration(savePath, nifPath, _associatedModSetting.CorrespondingFolderPaths, autoGenerated))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                    if (await _portraitCreator.GeneratePortraitAsync(nifPath, _associatedModSetting.CorrespondingFolderPaths, savePath, token))
                    {
                        SetImageSource(savePath);
                    }
                }
            }
        }
        catch (TaskCanceledException)
        {
            /* Swallow cancellation */
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error generating summary image for {SourceNpcFormKey}: {ExceptionLogger.GetExceptionStack(ex)}");
        }
        finally
        {
            IsLoading = false;
        }
    }
        
        private void SetImageSource(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            var bitmap = new BitmapImage();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();

            // Update this VM's properties to reflect the new image
            this.MugshotSource = bitmap;
            this.ImagePath = path;
            this.HasMugshot = true;
        }
    
        private void SetImageSourceFromMemory(byte[] imageData)
        {
            if (imageData == null || imageData.Length == 0) return;

            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(imageData))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // Read fully into memory
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze(); // Make it thread-safe for the UI

            // Update the UI properties
            this.MugshotSource = bitmap;
            this.ImagePath = "in-memory"; // A non-file path to indicate it's not saved
            this.HasMugshot = true;

            // We also need to update the dimensions from the in-memory data
            var info = Image.Identify(imageData);
            OriginalPixelWidth = info.Width;
            OriginalPixelHeight = info.Height;
            OriginalDipWidth = info.Width;
            OriginalDipHeight = info.Height;
            OriginalDipDiagonal = Math.Sqrt(OriginalDipWidth * OriginalDipWidth + OriginalDipHeight * OriginalDipHeight);
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }


    public class VM_SummaryListItem : ReactiveObject
    {
        public FormKey TargetNpcFormKey { get; } // <-- ADD THIS
        public string NpcDisplayName { get; }
        public string SelectedModName { get; }
        public string SourceNpcDisplayName { get; }
        public bool IsGuestAppearance { get; }
        public string SourceNpcForDisplay => IsGuestAppearance ? SourceNpcDisplayName : string.Empty;

        public VM_SummaryListItem(FormKey targetNpcKey, string npcName, string modName, string sourceNpcName, bool isGuest) // <-- MODIFY SIGNATURE
        {
            TargetNpcFormKey = targetNpcKey; // <-- ADD THIS
            NpcDisplayName = npcName;
            SelectedModName = modName;
            SourceNpcDisplayName = sourceNpcName;
            IsGuestAppearance = isGuest;
        }
    }