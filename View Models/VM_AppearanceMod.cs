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
        [Reactive] public string ImagePath { get; private set; } = string.Empty;
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
            ModKey = AssociatedModSetting?.CorrespondingModKey ?? overrideModeKey;

            _npcFormKey = npcFormKey;
            _settings = settings;
            _consistencyProvider = consistencyProvider;
            _vmNpcSelectionBar = vmNpcSelectionBar;


            ImagePath = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath) ? imagePath : string.Empty;
            HasMugshot = !string.IsNullOrWhiteSpace(ImagePath);
            if (HasMugshot)
            {
                try
                {
                    var (width, height) = ImagePacker.GetImageDimensionsInDIPs(ImagePath);
                    ImageWidth = width;
                    ImageHeight = height;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error getting dimensions for {ImagePath}: {ex.Message}");
                    HasMugshot = false;
                    ImagePath = string.Empty;
                }
            }

            CanJumpToMod = _vmNpcSelectionBar.CanJumpToMod(modName); // Use injected instance
            IsSelected = _consistencyProvider.IsModSelected(_npcFormKey, ModName);

            SelectCommand = ReactiveCommand.Create(SelectThisMod);
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
             _consistencyProvider.SetSelectedMod(_npcFormKey, ModName);
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