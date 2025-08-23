// View Models/VM_SummaryMugshot.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Windows;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Reactive.Disposables;
using System.Reactive.Concurrency;
using System.Reactive.Linq;

namespace NPC_Plugin_Chooser_2.View_Models;

    [DebuggerDisplay("{NpcDisplayName} -> {ModDisplayName}")]
    public class VM_SummaryMugshot : ReactiveObject, IHasMugshotImage, IDisposable
    {
        private readonly CompositeDisposable _disposables = new();
        private readonly Lazy<VM_MainWindow> _lazyMainWindowVm;

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

        // Display Properties
        public FormKey TargetNpcFormKey { get; }
        public string NpcDisplayName { get; }
        public string ModDisplayName { get; }
        public string SourceNpcDisplayName { get; }

        // Decorator Icon Properties
        public bool IsGuestAppearance { get; }
        public bool IsAmbiguousSource { get; }
        public bool HasIssueNotification { get; }
        public string IssueNotificationText { get; }
        public bool HasNoData { get; }
        public string NoDataNotificationText { get; }
        public bool HasMugshot { get; }

        // Commands
        public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
        public ReactiveCommand<Unit, Unit> JumpToNpcCommand { get; }
        public ReactiveCommand<Unit, Unit> JumpToModCommand { get; }

        public VM_SummaryMugshot(
            string imagePath,
            FormKey targetNpcFormKey,
            string npcDisplayName,
            string modDisplayName,
            string sourceNpcDisplayName,
            bool isGuest, bool isAmbiguous, bool hasIssue, string issueText, bool hasNoData, string noDataText, bool hasMugshot,
            Lazy<VM_MainWindow> lazyMainWindowVm,
            VM_NpcSelectionBar npcsViewModel,
            VM_Mods modsViewModel)
        {
            _lazyMainWindowVm = lazyMainWindowVm;

            ImagePath = imagePath;
            TargetNpcFormKey = targetNpcFormKey;
            NpcDisplayName = npcDisplayName;
            ModDisplayName = modDisplayName;
            SourceNpcDisplayName = sourceNpcDisplayName;

            IsGuestAppearance = isGuest;
            IsAmbiguousSource = isAmbiguous;
            HasIssueNotification = hasIssue;
            IssueNotificationText = issueText;
            HasNoData = hasNoData;
            NoDataNotificationText = noDataText;
            HasMugshot = hasMugshot;

            // Load image dimensions and source
            if (!string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath))
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

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(ImagePath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    MugshotSource = bitmap;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error loading image '{ImagePath}': {ex.Message}");
                }
            }

            // Command Implementations
            ToggleFullScreenCommand = ReactiveCommand.Create(() =>
            {
                var fullScreenVM = new VM_FullScreenImage(ImagePath);
                var fullScreenView = Locator.Current.GetService<IViewFor<VM_FullScreenImage>>() as Window;
                if (fullScreenView != null)
                {
                    fullScreenView.DataContext = fullScreenVM;
                    fullScreenView.ShowDialog();
                }
            }, this.WhenAnyValue(x => x.HasMugshot));

            JumpToNpcCommand = ReactiveCommand.Create(() =>
            {
                modsViewModel.NavigateToNpc(TargetNpcFormKey);
            });

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
            });
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

        public VM_SummaryListItem(FormKey targetNpcKey, string npcName, string modName, string sourceNpcName, bool isGuest) // <-- MODIFY SIGNATURE
        {
            TargetNpcFormKey = targetNpcKey; // <-- ADD THIS
            NpcDisplayName = npcName;
            SelectedModName = modName;
            SourceNpcDisplayName = sourceNpcName;
            IsGuestAppearance = isGuest;
        }
    }
