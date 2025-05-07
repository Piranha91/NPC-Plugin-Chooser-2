// View Models/VM_ModNpcMugshot.cs
using System;
using System.IO;
using System.Reactive;
using System.Windows; // For MessageBox
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Views; // For FullScreenImageView
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat; // For Locator

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_ModNpcMugshot : ReactiveObject, IHasMugshotImage
    {
        private readonly VM_Mods _parentVm;

        public string ImagePath { get; set; }
        public FormKey NpcFormKey { get; }
        public string NpcDisplayName { get; }
        [Reactive] public double ImageWidth { get; set; }
        [Reactive] public double ImageHeight { get; set; }
        public bool HasMugshot { get; } // Always true for this VM type
        public bool IsVisible { get; }  // Always true for this VM type

        public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
        public ReactiveCommand<Unit, Unit> JumpToNpcCommand { get; }

        public VM_ModNpcMugshot(string imagePath, FormKey npcFormKey, string npcDisplayName, VM_Mods parentVm)
        {
            _parentVm = parentVm;
            ImagePath = imagePath;
            NpcFormKey = npcFormKey;
            NpcDisplayName = npcDisplayName;
            HasMugshot = true; // This VM is only created if an image exists
            IsVisible = true; // Hiding isn't implemented for Mod view members

            if (File.Exists(ImagePath))
            {
                var (width, height) = ImagePacker.GetImageDimensionsInDIPs(ImagePath);
                ImageWidth = width;
                ImageHeight = height;
            }
            else
            {
                // Handle case where image file might have been deleted between scan and VM creation
                ImageWidth = 100; // Default size
                ImageHeight = 100;
                HasMugshot = false;
            }

            ToggleFullScreenCommand = ReactiveCommand.Create(ToggleFullScreen, this.WhenAnyValue(x => x.HasMugshot));
            JumpToNpcCommand = ReactiveCommand.Create(JumpToNpc);

            ToggleFullScreenCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error showing image: {ex.Message}"));
            JumpToNpcCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error jumping to NPC: {ex.Message}"));
        }

        private void ToggleFullScreen()
        {
            if (HasMugshot && File.Exists(ImagePath))
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