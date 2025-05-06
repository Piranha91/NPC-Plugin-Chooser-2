// [VM_AppearanceMod.cs] - Refactored for Delegate Factory
using System;
using System.IO;
using System.Drawing;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat;
using System.Linq;

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_AppearanceMod : ReactiveObject, IDisposable, IHasMugshotImage
    {
        private readonly FormKey _npcFormKey;
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly VM_NpcSelectionBar _vmNpcSelectionBar; // Keep this dependency
        private readonly Lazy<VM_Mods> _lazyMods;             // Keep this dependency
        private readonly CompositeDisposable Disposables = new();

        public ModKey? ModKey { get; }
        public string ModName { get; }
        [Reactive] public string ImagePath { get;  set; } = string.Empty;
        [Reactive] public double ImageWidth { get; set; }
        [Reactive] public double ImageHeight { get; set; }
        [Reactive] public bool IsSelected { get; set; }
        [Reactive] public bool HasMugshot { get; private set; }
        [Reactive] public bool IsVisible { get; set; } = true;
        [Reactive] public bool IsSetHidden { get; set; } = false;
        [Reactive] public bool CanJumpToMod { get; set; } = false;
        public VM_ModSetting? AssociatedModSetting { get; }

        public ReactiveCommand<Unit, Unit> SelectCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
        public ReactiveCommand<Unit, Unit> HideCommand { get; }
        public ReactiveCommand<Unit, Unit> UnhideCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectAllFromThisModCommand { get; }
        public ReactiveCommand<Unit, Unit> HideAllFromThisModCommand { get; }
        public ReactiveCommand<Unit, Unit> UnhideAllFromThisModCommand { get; }
        public ReactiveCommand<Unit, Unit> JumpToModCommand { get; }
        
        // --- Placeholder Image Configuration ---
        // Relative path from the application's base directory
        private const string PlaceholderResourceRelativePath = @"Resources\No Mugshot.png";
        // Calculate the full path once
        private static readonly string FullPlaceholderPath = Path.Combine(AppContext.BaseDirectory, PlaceholderResourceRelativePath);
        // Check if the placeholder file actually exists at runtime
        private static readonly bool PlaceholderExists = File.Exists(FullPlaceholderPath);
        // --- End Placeholder Configuration ---

        // *** Constructor is public again ***
        // Parameters that vary per instance are listed first by convention
        public VM_AppearanceMod(
            string modName,             // Varies
            FormKey npcFormKey,         // Varies
            ModKey? overrideModeKey,    // Varies - Only supply if modName is the source plugin name
            string? imagePath,          // Varies
            // ---- Dependencies Resolved by Autofac ----
            Settings settings,
            NpcConsistencyProvider consistencyProvider,
            VM_NpcSelectionBar vmNpcSelectionBar, // Autofac injects the singleton instance
            Lazy<VM_Mods> lazyMods)             // Autofac injects the singleton instance
        {
            ModName = modName;
            _lazyMods = lazyMods;
            AssociatedModSetting = _lazyMods.Value?.AllModSettings.FirstOrDefault(m => m.DisplayName == modName);
            // The overrideModeKey should represent the *specific* plugin providing this appearance data.
            // If it's null, we fall back to the *first* key in the associated setting, which might not be correct.
            // The logic in CreateAppearanceModViewModels needs to ensure overrideModeKey is passed correctly.
            ModKey = overrideModeKey ?? AssociatedModSetting?.CorrespondingModKeys.FirstOrDefault();

            _npcFormKey = npcFormKey;
            _settings = settings;
            _consistencyProvider = consistencyProvider;
            _vmNpcSelectionBar = vmNpcSelectionBar;

            // --- Image Path and HasMugshot Logic ---
            // 1. Check if a *real* mugshot path was provided and exists
            bool realMugshotExists = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);
            HasMugshot = realMugshotExists; // Set based on *real* mugshot presence

            // 2. Assign ImagePath: Prioritize real mugshot, then placeholder, then empty
            if (realMugshotExists)
            {
                ImagePath = imagePath!; // Use the valid real mugshot path
            }
            else if (PlaceholderExists)
            {
                ImagePath = FullPlaceholderPath; // Use the placeholder path
                 // HasMugshot remains false because it's not a *real* mugshot for this mod
            }
            else
            {
                ImagePath = string.Empty; // Fallback: No real mugshot AND no placeholder found
                HasMugshot = false;       // Ensure HasMugshot is false
            }

            // 3. Try to get dimensions if *any* image path was set (real or placeholder)
            if (!string.IsNullOrWhiteSpace(ImagePath))
            {
                try
                {
                    var (width, height) = ImagePacker.GetImageDimensionsInDIPs(ImagePath);
                    ImageWidth = width;
                    ImageHeight = height;
                }
                catch (Exception ex)
                {
                    // Log error if dimensions couldn't be read (e.g., invalid image file, permissions)
                    System.Diagnostics.Debug.WriteLine($"Error getting dimensions for '{ImagePath}': {ex.Message}");
                    // Reset state if the image (even placeholder) is unusable
                    ImagePath = string.Empty;
                    HasMugshot = false; // Can't display it, so treat as no mugshot
                    ImageWidth = 0;
                    ImageHeight = 0;
                }
            }
            else // No image path set (no real, no placeholder)
            {
                ImageWidth = 0; // Ensure dimensions are zero
                ImageHeight = 0;
            }
            // --- End Image Path Logic ---


            CanJumpToMod = _vmNpcSelectionBar.CanJumpToMod(modName); // Use injected instance
            IsSelected = _consistencyProvider.IsModSelected(_npcFormKey, ModName);

            SelectCommand = ReactiveCommand.Create(SelectThisMod);
            // Can toggle full screen if *any* image path is valid (real or placeholder)
            var canToggleFullScreen = this.WhenAnyValue(x => x.ImagePath, path => !string.IsNullOrEmpty(path) && File.Exists(path));
            ToggleFullScreenCommand = ReactiveCommand.Create(ToggleFullScreen, canToggleFullScreen);
            HideCommand = ReactiveCommand.Create(HideThisMod);
            UnhideCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.UnhideSelectedMod(this));
            SelectAllFromThisModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.SelectAllFromMod(this));
            HideAllFromThisModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.HideAllFromMod(this));
            UnhideAllFromThisModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.UnhideAllFromMod(this));
            JumpToModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.JumpToMod(this), this.WhenAnyValue(x => x.CanJumpToMod));

            SelectCommand.ThrownExceptions.Subscribe(ex => MessageBox.Show($"Error selecting mod: {ex.Message}")).DisposeWith(Disposables);
            ToggleFullScreenCommand.ThrownExceptions.Subscribe(ex => MessageBox.Show($"Error showing image: {ex.Message}")).DisposeWith(Disposables);
            HideCommand.ThrownExceptions.Subscribe(ex => MessageBox.Show($"Error hiding mod: {ex.Message}")).DisposeWith(Disposables);
            UnhideCommand.ThrownExceptions.Subscribe(ex => MessageBox.Show($"Error unhiding mod: {ex.Message}")).DisposeWith(Disposables);
            SelectAllFromThisModCommand.ThrownExceptions.Subscribe(ex => MessageBox.Show($"Error selecting all from mod: {ex.Message}")).DisposeWith(Disposables);
            HideAllFromThisModCommand.ThrownExceptions.Subscribe(ex => MessageBox.Show($"Error hiding all from mod: {ex.Message}")).DisposeWith(Disposables);
            UnhideAllFromThisModCommand.ThrownExceptions.Subscribe(ex => MessageBox.Show($"Error unhiding all from mod: {ex.Message}")).DisposeWith(Disposables);
            JumpToModCommand.ThrownExceptions.Subscribe(ex => MessageBox.Show($"Error jumping to mod: {ex.Message}")).DisposeWith(Disposables);


            _consistencyProvider.NpcSelectionChanged
                .Where(args => args.NpcFormKey == _npcFormKey)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(args => IsSelected = (args.SelectedMod == ModName))
                .DisposeWith(Disposables);
        }

        // Static Create method removed

        private void SelectThisMod()
        {
            // Check the *current* state before deciding action
            if (IsSelected)
            {
                // If it's already selected, clear the selection for this NPC
                System.Diagnostics.Debug.WriteLine($"Deselecting mod '{ModName}' for NPC '{_npcFormKey}'");
                _consistencyProvider.ClearSelectedMod(_npcFormKey);
            }
            else
            {
                // If it's not selected, select this mod
                System.Diagnostics.Debug.WriteLine($"Selecting mod '{ModName}' for NPC '{_npcFormKey}'");
                _consistencyProvider.SetSelectedMod(_npcFormKey, ModName);
            }
            // The IsSelected property will be updated reactively via the
            // _consistencyProvider.NpcSelectionChanged subscription in the constructor.
        }

        private void ToggleFullScreen()
        {
             if (HasMugshot && !string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath))
             {
                 try
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
                          MessageBox.Show("Could not create or resolve the FullScreenImageView.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                     }
                 }
                 catch (Exception ex)
                 {
                      MessageBox.Show($"Error displaying full screen image:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                 }
             }
             else
             {
                 MessageBox.Show($"Mugshot not found or path is invalid:\n{ImagePath}", "Image Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
             }
        }

        public void HideThisMod()
        {
            _vmNpcSelectionBar.HideSelectedMod(this);
        }

        public void Dispose()
        {
            Disposables.Dispose();
        }
    }
}