using System;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows; // For MessageBox
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd; // For NpcConsistencyProvider
using NPC_Plugin_Chooser_2.Models; // For Settings
using NPC_Plugin_Chooser_2.Views; // For FullScreenImageView
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat; // For Locator

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_AppearanceMod : ReactiveObject
    {
        private readonly INpcGetter _npcGetter;
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider; // To notify of selection

        public ModKey? ModKey { get; }
        public string ModName { get; }
        [Reactive] public string ImagePath { get; private set; } = string.Empty; // Use string.Empty or null for binding triggers
        [Reactive] public bool IsSelected { get; set; }

        public ReactiveCommand<Unit, Unit> SelectCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }

        public VM_AppearanceMod(string modName, ModKey? modKey, INpcGetter npcGetter, Settings settings, NpcConsistencyProvider consistencyProvider)
        {
            ModKey = modKey;
            _npcGetter = npcGetter;
            _settings = settings;
            _consistencyProvider = consistencyProvider;

            ImagePath = GenerateImagePath();

             // Update IsSelected based on the central provider
            IsSelected = _consistencyProvider.IsModSelected(npcGetter.FormKey, modName);

            SelectCommand = ReactiveCommand.Create(SelectThisMod);
            ToggleFullScreenCommand = ReactiveCommand.Create(ToggleFullScreen);

            // Handle potential errors during command execution if needed
            SelectCommand.ThrownExceptions.Subscribe(ex => MessageBox.Show($"Error selecting mod: {ex.Message}"));
            ToggleFullScreenCommand.ThrownExceptions.Subscribe(ex => MessageBox.Show($"Error showing image: {ex.Message}"));

             // Listen for changes triggered by other selections for the same NPC
            _consistencyProvider.NpcSelectionChanged
                .Where(args => args.NpcFormKey == _npcGetter.FormKey)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(args => IsSelected = (args.SelectedMod == ModName))
                .DisposeWith(Disposables); // Need a mechanism to dispose subscriptions
        }

        private readonly System.Reactive.Disposables.CompositeDisposable Disposables = new System.Reactive.Disposables.CompositeDisposable();


        private void SelectThisMod()
        {
            _consistencyProvider.SetSelectedMod(_npcGetter.FormKey, ModName);
             // IsSelected will be updated via the NpcSelectionChanged subscription
        }

        private void ToggleFullScreen()
        {
            if (!string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath))
            {
                var fullScreenVM = new VM_FullScreenImage(ImagePath);
                var fullScreenView = Locator.Current.GetService<IViewFor<VM_FullScreenImage>>() as Window; // Resolve the view

                if (fullScreenView != null)
                {
                    fullScreenView.DataContext = fullScreenVM; // Set the ViewModel
                    fullScreenView.ShowDialog(); // Show modally
                }
            }
            else
            {
                MessageBox.Show($"Mugshot not found at expected path:\n{ImagePath}", "Image Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private string GenerateImagePath()
        {
            if (string.IsNullOrWhiteSpace(_settings.MugshotsFolder) || !Directory.Exists(_settings.MugshotsFolder))
            {
                // Log this warning?
                return string.Empty;
            }

            // Sanitize ModKey filename for use in path
            string safeModName = Path.GetFileNameWithoutExtension(ModKey?.FileName); // Use only the name part

            // Use EditorID if available and valid for filenames, otherwise FormKey string
            string npcIdentifier = GetSafeFilename(_npcGetter.EditorID);
            if (string.IsNullOrWhiteSpace(npcIdentifier))
            {
                // Construct a safe filename from FormKey if EditorID is missing/invalid
                 npcIdentifier = $"Form_{_npcGetter.FormKey.IDString()}_Mod_{GetSafeFilename(ModKey?.FileName)}";
            }


            // Potential Path Combinations (Adjust based on your exact structure)
            // Option 1: MugshotsFolder / ModName / NPC_EditorID.png
            string path1 = Path.Combine(_settings.MugshotsFolder, safeModName, npcIdentifier + ".png");
            if (File.Exists(path1)) return path1;

            // Option 2: MugshotsFolder / NPC_EditorID / ModName.png (Less likely but possible)
            // string path2 = Path.Combine(_settings.MugshotsFolder, npcIdentifier, safeModName + ".png");
            // if (File.Exists(path2)) return path2;

             // Option 3: Try with .jpg
            string path3 = Path.Combine(_settings.MugshotsFolder, safeModName, npcIdentifier + ".jpg");
            if (File.Exists(path3)) return path3;

             // Option 4: Try with .bmp
            string path4 = Path.Combine(_settings.MugshotsFolder, safeModName, npcIdentifier + ".bmp");
            if (File.Exists(path4)) return path4;


            // If no image found after checking common extensions/paths
            System.Diagnostics.Debug.WriteLine($"Mugshot not found for NPC {npcIdentifier} from Mod {safeModName}. Checked path: {path1} (and variations)");
            return string.Empty; // Return empty/null if not found
        }

         // Helper to create safe filenames
        private static string GetSafeFilename(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        }

         // Implement IDisposable if using CompositeDisposable
        // public void Dispose()
        // {
        //     Disposables.Dispose();
        // }
    }
}