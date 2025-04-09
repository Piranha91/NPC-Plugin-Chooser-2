// [VM_AppearanceMod.cs] - Updated
using System;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows; // For MessageBox
using Mutagen.Bethesda.Plugins;
// using Mutagen.Bethesda.Skyrim; // INpcGetter no longer directly needed here
using NPC_Plugin_Chooser_2.BackEnd; // For NpcConsistencyProvider
using NPC_Plugin_Chooser_2.Models; // For Settings
using NPC_Plugin_Chooser_2.Views; // For FullScreenImageView
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat; // For Locator

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_AppearanceMod : ReactiveObject, IDisposable
    {
        private readonly FormKey _npcFormKey; // Store NPC FormKey instead of Getter
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider; // To notify of selection
        private readonly CompositeDisposable Disposables = new();

        public ModKey? ModKey { get; }
        public string ModName { get; } // Display name (can be from ModKey or Mugshot folder)
        [Reactive] public string ImagePath { get; private set; } = string.Empty;
        [Reactive] public bool IsSelected { get; set; }
        [Reactive] public bool HasMugshot { get; private set; } // Flag if a specific mugshot was assigned

        public ReactiveCommand<Unit, Unit> SelectCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }

        // Updated Constructor
        public VM_AppearanceMod(
            string modName, // Use provided name (might be from ModKey or Mugshot folder)
            ModKey? modKey, // The actual ModKey associated with this appearance source (can be null for fallback?)
            FormKey npcFormKey, // NPC identifier
            string? imagePath, // Explicitly provided image path (optional)
            Settings settings,
            NpcConsistencyProvider consistencyProvider)
        {
            ModName = modName; // Use the provided name
            ModKey = modKey;
            _npcFormKey = npcFormKey;
            _settings = settings;
            _consistencyProvider = consistencyProvider;

            // Use provided image path if valid, otherwise empty
            ImagePath = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath) ? imagePath : string.Empty;
            HasMugshot = !string.IsNullOrWhiteSpace(ImagePath);

            // Update IsSelected based on the central provider (using ModName for comparison now)
            IsSelected = _consistencyProvider.IsModSelected(_npcFormKey, ModName);

            SelectCommand = ReactiveCommand.Create(SelectThisMod);
            // Only enable fullscreen if there's an image
            var canToggleFullScreen = this.WhenAnyValue(x => x.ImagePath, path => !string.IsNullOrEmpty(path) && File.Exists(path));
            ToggleFullScreenCommand = ReactiveCommand.Create(ToggleFullScreen, canToggleFullScreen);


            SelectCommand.ThrownExceptions.Subscribe(ex => MessageBox.Show($"Error selecting mod: {ex.Message}")).DisposeWith(Disposables);
            ToggleFullScreenCommand.ThrownExceptions.Subscribe(ex => MessageBox.Show($"Error showing image: {ex.Message}")).DisposeWith(Disposables);

            // Listen for changes triggered by other selections for the same NPC
            _consistencyProvider.NpcSelectionChanged
                .Where(args => args.NpcFormKey == _npcFormKey)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(args => IsSelected = (args.SelectedMod == ModName)) // Compare using ModName
                .DisposeWith(Disposables);
        }

        private void SelectThisMod()
        {
            // Use ModName for selection consistency, as ModKey might not perfectly align
            // if the appearance mod comes purely from a mugshot entry.
             _consistencyProvider.SetSelectedMod(_npcFormKey, ModName);
             // IsSelected will be updated via the NpcSelectionChanged subscription
        }

        private void ToggleFullScreen()
        {
            if (HasMugshot) // Already checked path validity
            {
                var fullScreenVM = new VM_FullScreenImage(ImagePath);
                var fullScreenView = Locator.Current.GetService<IViewFor<VM_FullScreenImage>>() as Window; // Resolve the view

                if (fullScreenView != null)
                {
                    fullScreenView.DataContext = fullScreenVM; // Set the ViewModel
                    fullScreenView.ShowDialog(); // Show modally
                }
                else
                {
                     MessageBox.Show($"Could not create FullScreenImageView.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show($"Mugshot not found or path is invalid:\n{ImagePath}", "Image Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Removed GenerateImagePath()

        // Implement IDisposable
        public void Dispose()
        {
            Disposables.Dispose();
        }
    }
}