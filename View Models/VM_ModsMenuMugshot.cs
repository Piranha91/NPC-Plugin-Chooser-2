// View Models/VM_ModsMenuMugshot.cs
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
using System.Reactive.Linq; // For Debug.WriteLine

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_ModsMenuMugshot : ReactiveObject, IHasMugshotImage
    {
        private readonly VM_Mods _parentVMMaster; 
        private readonly VM_ModSetting _parentVMModSetting; 

        public string ImagePath { get; set; } 
        public FormKey NpcFormKey { get; }
        public string NpcDisplayName { get; }

        [Reactive] public double ImageWidth { get; set; } 
        [Reactive] public double ImageHeight { get; set; } 
        
        // HasMugshot is true if ImagePath points to a REAL mugshot, false if it's a placeholder or invalid.
        [Reactive] public bool HasMugshot { get; private set; } 
        public bool IsVisible { get; set; }  

        public int OriginalPixelWidth { get; set; }
        public int OriginalPixelHeight { get; set; }
        public double OriginalDipWidth { get; set; }
        public double OriginalDipHeight { get; set; }
        public double OriginalDipDiagonal { get; set; }
        
        public bool IsAmbiguousSource { get; } 
        public ObservableCollection<ModKey> AvailableSourcePlugins { get; } = new();
        [Reactive] public ModKey? CurrentSourcePlugin { get; set; } 

        public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
        public ReactiveCommand<Unit, Unit> JumpToNpcCommand { get; }
        public ReactiveCommand<ModKey, Unit> SetNpcSourcePluginCommand { get; }

        // Static path for placeholder, consistent with VM_Mods
        private const string PlaceholderResourceRelativePath = @"Resources\No Mugshot.png";
        private static readonly string FullPlaceholderPath = Path.Combine(AppContext.BaseDirectory, PlaceholderResourceRelativePath);

        public VM_ModsMenuMugshot(
            string imagePath, // This can be a real image path OR FullPlaceholderPath
            FormKey npcFormKey, 
            string npcDisplayName, 
            VM_Mods parentVMMaster,
            bool isAmbiguousSource,                 
            List<ModKey> availableSourcePlugins,   
            ModKey? currentSourcePlugin,            
            VM_ModSetting parentVMModSetting)       
        {
            _parentVMMaster = parentVMMaster;
            _parentVMModSetting = parentVMModSetting; 
            ImagePath = imagePath; // Store the given path (could be real or placeholder)
            NpcFormKey = npcFormKey;
            NpcDisplayName = npcDisplayName;
            IsVisible = true; 

            IsAmbiguousSource = isAmbiguousSource;
            CurrentSourcePlugin = currentSourcePlugin;

            if (IsAmbiguousSource && availableSourcePlugins != null)
            {
                AvailableSourcePlugins = new ObservableCollection<ModKey>(availableSourcePlugins.OrderBy(k => k.FileName.String));
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
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error getting dimensions for '{ImagePath}' in VM_ModsMenuMugshot: {ex.Message}");
                    // If dimensions fail even for a placeholder, something is wrong. Clear out.
                    ImageWidth = 0; ImageHeight = 0;
                    HasMugshot = false; // Ensure this is false if image load fails
                    OriginalPixelWidth = 0; OriginalPixelHeight = 0;
                    OriginalDipWidth = 0; OriginalDipHeight = 0; OriginalDipDiagonal = 0;
                    ImagePath = string.Empty; // Prevent trying to display a broken path
                }
            }
            else // Path was null, empty, or file doesn't exist (should be rare if VM_Mods passes valid placeholder)
            {
                ImageWidth = 0; ImageHeight = 0;
                HasMugshot = false;
                OriginalPixelWidth = 0; OriginalPixelHeight = 0;
                OriginalDipWidth = 0; OriginalDipHeight = 0; OriginalDipDiagonal = 0;
                ImagePath = string.Empty; 
            }

            // ToggleFullScreenCommand can now operate on the placeholder too.
            var canToggleFullScreen = this.WhenAnyValue(x => x.ImagePath, path => !string.IsNullOrEmpty(path) && File.Exists(path));
            ToggleFullScreenCommand = ReactiveCommand.Create(ToggleFullScreen, canToggleFullScreen); 
            JumpToNpcCommand = ReactiveCommand.Create(JumpToNpc);
            
            var canSetNpcSource = this.WhenAnyValue(x => x.IsAmbiguousSource).Select(isAmbiguous => isAmbiguous); 
            SetNpcSourcePluginCommand = ReactiveCommand.Create<ModKey>(SetNpcSourcePluginInternal, canSetNpcSource);

            ToggleFullScreenCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error showing image: {ex.Message}"));
            JumpToNpcCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error jumping to NPC: {ex.Message}"));
            SetNpcSourcePluginCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error setting NPC source plugin: {ex.Message}"));
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
                ScrollableMessageBox.ShowWarning($"Mugshot image (or placeholder) not found or path is invalid:\n{ImagePath}");
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
                Debug.WriteLine($"SetNpcSourcePluginInternal called for non-ambiguous NPC {NpcFormKey}. This should not happen.");
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
                    Debug.WriteLine($"Warning: Could not re-resolve source for NPC {NpcFormKey} from NpcSourcePluginMap after setting. Displayed checkmark might be based on direct selection.");
                }
            }
            // No need to call anything on _parentVMMaster (VM_Mods) to refresh the whole panel.
        }
    }
}