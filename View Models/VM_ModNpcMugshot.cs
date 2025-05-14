// View Models/VM_ModNpcMugshot.cs
using System;
using System.IO;
using System.Reactive;
using System.Windows; 
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Views; 
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat; 

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_ModNpcMugshot : ReactiveObject, IHasMugshotImage
    {
        private readonly VM_Mods _parentVm;

        public string ImagePath { get; set; } // Displayed path
        public FormKey NpcFormKey { get; }
        public string NpcDisplayName { get; }

        [Reactive] public double ImageWidth { get; set; } // Displayed width
        [Reactive] public double ImageHeight { get; set; } // Displayed height
        
        public bool HasMugshot { get; private set; } // True if ImagePath is a real image, not a placeholder
        public bool IsVisible { get; set; }  // For ModsView, always true unless future filtering is added for this view

        // --- NEW IHasMugshotImage properties ---
        public int OriginalPixelWidth { get; set; }
        public int OriginalPixelHeight { get; set; }
        public double OriginalDipWidth { get; set; }
        public double OriginalDipHeight { get; set; }
        public double OriginalDipDiagonal { get; set; }


        public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
        public ReactiveCommand<Unit, Unit> JumpToNpcCommand { get; }

        public VM_ModNpcMugshot(string imagePath, FormKey npcFormKey, string npcDisplayName, VM_Mods parentVm)
        {
            _parentVm = parentVm;
            ImagePath = imagePath; // This is always expected to be a valid image path for this VM
            NpcFormKey = npcFormKey;
            NpcDisplayName = npcDisplayName;
            IsVisible = true; // Hiding isn't typically implemented for Mod view members this way

            if (File.Exists(ImagePath))
            {
                HasMugshot = true; // This VM is only created if an image exists
                try
                {
                    var (pixelWidth, pixelHeight, dipWidth, dipHeight) = ImagePacker.GetImageDimensions(ImagePath);
                    OriginalPixelWidth = pixelWidth;
                    OriginalPixelHeight = pixelHeight;
                    OriginalDipWidth = dipWidth;
                    OriginalDipHeight = dipHeight;
                    OriginalDipDiagonal = Math.Sqrt(dipWidth * dipWidth + dipHeight * dipHeight);

                    // Initial display size can be set to original DIP size, ImagePacker will adjust it
                    ImageWidth = OriginalDipWidth;
                    ImageHeight = OriginalDipHeight;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error getting dimensions for '{ImagePath}' in VM_ModNpcMugshot: {ex.Message}");
                    // Handle case where image file might have been deleted/corrupted between scan and VM creation
                    ImageWidth = 100; // Default size
                    ImageHeight = 100;
                    HasMugshot = false; // Mark as no valid mugshot if dimensions fail
                    OriginalPixelWidth = 0; OriginalPixelHeight = 0;
                    OriginalDipWidth = 0; OriginalDipHeight = 0; OriginalDipDiagonal = 0;
                }
            }
            else
            {
                // This case should ideally not happen if VM is created only for existing images.
                // If it does, treat as placeholder/error.
                ImageWidth = 100; 
                ImageHeight = 100;
                HasMugshot = false;
                OriginalPixelWidth = 0; OriginalPixelHeight = 0;
                OriginalDipWidth = 0; OriginalDipHeight = 0; OriginalDipDiagonal = 0;
                // ImagePath might be invalid here, but keep it for debugging/tooltip.
            }

            ToggleFullScreenCommand = ReactiveCommand.Create(ToggleFullScreen, this.WhenAnyValue(x => x.HasMugshot)); // Only allow if HasMugshot is true
            JumpToNpcCommand = ReactiveCommand.Create(JumpToNpc);

            ToggleFullScreenCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error showing image: {ex.Message}"));
            JumpToNpcCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error jumping to NPC: {ex.Message}"));
        }

        private void ToggleFullScreen()
        {
            if (HasMugshot && File.Exists(ImagePath)) // Redundant File.Exists check for safety
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
                ScrollableMessageBox.ShowWarning($"Mugshot not found or path is invalid:\n{ImagePath}");
            }
        }

        private void JumpToNpc()
        {
            _parentVm.NavigateToNpc(NpcFormKey);
        }
    }
}