// [VM_AppearanceMod.cs]
using System;
using System.Diagnostics;
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
using GongSolutions.Wpf.DragDrop; // Added
using System.Text;
using System.Windows.Media; // Added for StringBuilder in Drop logic

namespace NPC_Plugin_Chooser_2.View_Models
{
    // Add IDragSource, IDropTarget
    public class VM_AppearanceMod : ReactiveObject, IDisposable, IHasMugshotImage, IDragSource, IDropTarget
    {
        // --- Existing fields ---
        private readonly FormKey _npcFormKey;
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly VM_NpcSelectionBar _vmNpcSelectionBar; // Keep this dependency
        private readonly Lazy<VM_Mods> _lazyMods;             // Keep this dependency
        private readonly CompositeDisposable Disposables = new();

        private readonly SolidColorBrush _selectedWithDataBrush = new(Colors.LimeGreen);
        private readonly SolidColorBrush _selectedWithoutDataBrush = new(Colors.DarkMagenta);
        private readonly SolidColorBrush _deselectedWithDataBrush = new(Colors.Transparent);
        private readonly SolidColorBrush _deselectedWithoutDataBrush = new(Colors.Coral);
        

        // --- Existing properties ---
        public ModKey? ModKey { get; }
        public string ModName { get; }
        [Reactive] public string ImagePath { get;  set; } = string.Empty;
        [Reactive] public double ImageWidth { get; set; }
        [Reactive] public double ImageHeight { get; set; }
        [Reactive] public bool IsSelected { get; set; }
        [Reactive] public SolidColorBrush BorderColor { get; set; } = new(Colors.Transparent);
        [Reactive] public bool HasMugshot { get; private set; }
        [Reactive] public bool IsVisible { get; set; } = true;
        [Reactive] public bool IsSetHidden { get; set; } = false;
        [Reactive] public bool CanJumpToMod { get; set; } = false;
        public VM_ModSetting? AssociatedModSetting { get; }
        [Reactive] public string ToolTipString { get; set; } = string.Empty;

        // --- Existing Commands ---
        public ReactiveCommand<Unit, Unit> SelectCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
        public ReactiveCommand<Unit, Unit> HideCommand { get; }
        public ReactiveCommand<Unit, Unit> UnhideCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectAllFromThisModCommand { get; }
        public ReactiveCommand<Unit, Unit> HideAllFromThisModCommand { get; }
        public ReactiveCommand<Unit, Unit> UnhideAllFromThisModCommand { get; }
        public ReactiveCommand<Unit, Unit> JumpToModCommand { get; }

        // --- Placeholder Image Configuration --- (Keep as is)
        private const string PlaceholderResourceRelativePath = @"Resources\No Mugshot.png";
        private static readonly string FullPlaceholderPath = Path.Combine(AppContext.BaseDirectory, PlaceholderResourceRelativePath);
        private static readonly bool PlaceholderExists = File.Exists(FullPlaceholderPath);

        // --- Constructor --- (Keep as is)
        public VM_AppearanceMod(
            string modName,
            FormKey npcFormKey,
            ModKey? overrideModeKey,
            string? imagePath,
            Settings settings,
            NpcConsistencyProvider consistencyProvider,
            VM_NpcSelectionBar vmNpcSelectionBar,
            Lazy<VM_Mods> lazyMods)
        {
            ModName = modName;
            _lazyMods = lazyMods;
            AssociatedModSetting = _lazyMods.Value?.AllModSettings.FirstOrDefault(m => m.DisplayName == modName);
            ModKey = overrideModeKey ?? AssociatedModSetting?.CorrespondingModKeys.FirstOrDefault();
            _npcFormKey = npcFormKey;
            _settings = settings;
            _consistencyProvider = consistencyProvider;
            _vmNpcSelectionBar = vmNpcSelectionBar; // Ensure this is assigned

            // --- Image Path and HasMugshot Logic --- (Keep as is)
            bool realMugshotExists = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);
            HasMugshot = realMugshotExists;
            if (realMugshotExists) { ImagePath = imagePath!; }
            else if (PlaceholderExists) { ImagePath = FullPlaceholderPath; }
            else { ImagePath = string.Empty; HasMugshot = false; }
            if (!string.IsNullOrWhiteSpace(ImagePath))
            {
                try { var (width, height) = ImagePacker.GetImageDimensionsInDIPs(ImagePath); ImageWidth = width; ImageHeight = height; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error getting dimensions for '{ImagePath}': {ex.Message}"); ImagePath = string.Empty; HasMugshot = false; ImageWidth = 0; ImageHeight = 0; }
            } else { ImageWidth = 0; ImageHeight = 0; }

            this.WhenAnyValue(x => x.IsSelected)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(isSelected => SetBorderAndTooltip(isSelected))
                .DisposeWith(Disposables);
            
            // --- Command and Property Setup --- (Keep as is)
            CanJumpToMod = _vmNpcSelectionBar.CanJumpToMod(modName);
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
            SelectCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.Show($"Error selecting mod: {ex.Message}")).DisposeWith(Disposables);
            ToggleFullScreenCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.Show($"Error showing image: {ex.Message}")).DisposeWith(Disposables);
            HideCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.Show($"Error hiding mod: {ex.Message}")).DisposeWith(Disposables);
            UnhideCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.Show($"Error unhiding mod: {ex.Message}")).DisposeWith(Disposables);
            SelectAllFromThisModCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.Show($"Error selecting all from mod: {ex.Message}")).DisposeWith(Disposables);
            HideAllFromThisModCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.Show($"Error hiding all from mod: {ex.Message}")).DisposeWith(Disposables);
            UnhideAllFromThisModCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.Show($"Error unhiding all from mod: {ex.Message}")).DisposeWith(Disposables);
            JumpToModCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.Show($"Error jumping to mod: {ex.Message}")).DisposeWith(Disposables);
            _consistencyProvider.NpcSelectionChanged
                .Where(args => args.NpcFormKey == _npcFormKey)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(args => IsSelected = (args.SelectedMod == ModName))
                .DisposeWith(Disposables);
            
            SetBorderAndTooltip(IsSelected);
        }

        // --- Existing Methods --- (Keep as is)
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

        private void SetBorderAndTooltip(bool isSelected)
        {
            bool hasData = AssociatedModSetting?.CorrespondingFolderPaths.Any() ?? false;
            if (isSelected && hasData)
            {
                BorderColor = _selectedWithDataBrush;
                ToolTipString = "Selected. Mugshot has associated Mod Data and is ready for patch generation.";
            }
            else if (isSelected && !hasData)
            {
                BorderColor = _selectedWithoutDataBrush;
                ToolTipString = "Selected but Mugshot has no associated Mod Data. Patcher run will skip this NPC until Mod Data is linked to this mugshot";
            }
            if (!isSelected && hasData)
            {
                BorderColor = _deselectedWithDataBrush;
                ToolTipString = "Not Selected. Mugshot has associated Mod Data and is ready to go if you select it.";
            }
            else if (!isSelected && !hasData)
            {
                BorderColor = _deselectedWithoutDataBrush;
                ToolTipString = "Not Selected. Mugshot has no associated Mod Data. If you select it, Patcher run will skip this NPC until Mod Data is linked to this mugshot";
            }
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
                         ScrollableMessageBox.ShowError("Could not create or resolve the FullScreenImageView.");
                     }
                 }
                 catch (Exception ex)
                 {
                     ScrollableMessageBox.ShowWarning($"Mugshot not found or path is invalid:\n{ImagePath}");
                 }
             }
             else
             {
                 ScrollableMessageBox.ShowWarning($"Mugshot not found or path is invalid:\n{ImagePath}");
             }
        }

        public void HideThisMod()
        {
            _vmNpcSelectionBar.HideSelectedMod(this);
        }
        public void Dispose() { Disposables.Dispose(); }

        // --- IDragSource Implementation ---

        public bool CanStartDrag(IDragInfo dragInfo)
        {
            // Any item can be dragged
            return true;
        }

        public void StartDrag(IDragInfo dragInfo)
        {
            // The data is this VM instance itself
            dragInfo.Data = this;
            // Set allowed effects
            dragInfo.Effects = DragDropEffects.Move | DragDropEffects.Copy; // Allow Move
            Debug.WriteLine($"VM_AppearanceMod.StartDrag: Dragging '{this.ModName}'");
        }

        public void Dropped(IDropInfo dropInfo)
        {
            // Called on the source after a successful drop occurred elsewhere
            // No action needed here for the Move/Associate logic
             Debug.WriteLine($"VM_AppearanceMod.Dropped (Source): '{this.ModName}' was dropped with effect {dropInfo.Effects}");
        }

        public void DragCancelled()
        {
            Debug.WriteLine($"VM_AppearanceMod.DragCancelled: Drag of '{this.ModName}' cancelled.");
        }

        public void DragDropOperationFinished(DragDropEffects operationResult, IDragInfo dragInfo)
        {
            Debug.WriteLine($"VM_AppearanceMod.DragDropOperationFinished: Operation for '{this.ModName}' finished with result {operationResult}.");
        }

        public bool TryMove(IDropInfo dropInfo)
        {
            // Not used for this type of operation
            return false;
        }

        public bool TryCatchOccurredException(Exception exception)
        {
            Debug.WriteLine($"ERROR VM_AppearanceMod.TryCatchOccurredException (Source): Exception during D&D for '{this.ModName}': {exception}");
            return true; // Handle (log) the exception
        }

        // --- IDropTarget Implementation ---

        void IDropTarget.DragOver(IDropInfo dropInfo)
        {
            // 'this' is the potential target item
            var sourceItem = dropInfo.Data as VM_AppearanceMod;

            // Default to no drop
            dropInfo.Effects = DragDropEffects.None;

            if (sourceItem == null || sourceItem == this)
            {
                // Can't drop onto self or if data is wrong type
                return;
            }

            // Check the core condition: One is real mugshot, the other is placeholder
            bool sourceIsRealMugshot = sourceItem.HasMugshot;
            bool targetIsRealMugshot = this.HasMugshot; // 'this' is the target

            if (!((sourceIsRealMugshot && !targetIsRealMugshot) || (!sourceIsRealMugshot && targetIsRealMugshot)))
            {
                // Not a valid pair
                return;
            }

            // Identify which is which based on HasMugshot
            var mugshotVmApp = sourceIsRealMugshot ? sourceItem : this;
            var placeholderVmApp = sourceIsRealMugshot ? this : sourceItem;

            // Get associated settings
            var mugshotModSetting = mugshotVmApp.AssociatedModSetting;
            var placeholderModSetting = placeholderVmApp.AssociatedModSetting;

            // Check validity for drop operation
            bool mugshotPathValid = mugshotModSetting != null &&
                                     !string.IsNullOrWhiteSpace(mugshotModSetting.MugShotFolderPath) &&
                                     Directory.Exists(mugshotModSetting.MugShotFolderPath);
            bool placeholderPathsValid = placeholderModSetting != null &&
                                          placeholderModSetting.CorrespondingFolderPaths.Any();

            if (mugshotPathValid && placeholderPathsValid)
            {
                // Conditions met, allow drop
                dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight; // Highlight the target item
                dropInfo.Effects = DragDropEffects.Move;
            }
             // else: Effects remain None
        }

        void IDropTarget.Drop(IDropInfo dropInfo)
        {
            // 'this' is the target item where the drop occurred
            var sourceItem = dropInfo.Data as VM_AppearanceMod;

            // --- Repeat validation checks from DragOver for safety ---
            if (sourceItem == null || sourceItem == this) return;

            bool sourceIsRealMugshot = sourceItem.HasMugshot;
            bool targetIsRealMugshot = this.HasMugshot;

            if (!((sourceIsRealMugshot && !targetIsRealMugshot) || (!sourceIsRealMugshot && targetIsRealMugshot))) return;

            var mugshotVmApp = sourceIsRealMugshot ? sourceItem : this;
            var placeholderVmApp = sourceIsRealMugshot ? this : sourceItem;

            var mugshotSourceModSetting = mugshotVmApp.AssociatedModSetting;
            var placeholderTargetModSetting = placeholderVmApp.AssociatedModSetting; // 'this' is the placeholder if targetIsRealMugshot is false

             if (mugshotSourceModSetting == null || placeholderTargetModSetting == null ||
                 string.IsNullOrWhiteSpace(mugshotSourceModSetting.MugShotFolderPath) || !Directory.Exists(mugshotSourceModSetting.MugShotFolderPath) ||
                 !placeholderTargetModSetting.CorrespondingFolderPaths.Any())
            {
                ScrollableMessageBox.ShowError("Drop conditions not met (Validation failed in Drop). Ensure mugshot provider has valid path and placeholder has mod folders.", "Drop Error");
                return;
            }
            // --- End validation checks ---

            // --- Confirmation Dialog ---
            var sb = new StringBuilder();
            sb.AppendLine($"Are you sure you want to associate the Mugshots from [{mugshotSourceModSetting.DisplayName}] with the Mod Folder(s) from [{placeholderTargetModSetting.DisplayName}]?");
            bool mugshotProviderHasGameDataFolders = mugshotSourceModSetting.CorrespondingFolderPaths.Any();

            if (mugshotProviderHasGameDataFolders)
            {
                sb.AppendLine($"\n[{placeholderTargetModSetting.DisplayName}] will now use mugshots from [{mugshotSourceModSetting.DisplayName}]. Both mod entries will remain.");
            }
            else
            {
                sb.AppendLine($"\n[{placeholderTargetModSetting.DisplayName}] will take over the mugshots from [{mugshotSourceModSetting.DisplayName}].");
                sb.AppendLine($"The separate entry for [{mugshotSourceModSetting.DisplayName}] will be removed.");
                if (!string.IsNullOrWhiteSpace(placeholderTargetModSetting.MugShotFolderPath) &&
                    !placeholderTargetModSetting.MugShotFolderPath.Equals(mugshotSourceModSetting.MugShotFolderPath, StringComparison.OrdinalIgnoreCase))
                { sb.AppendLine($"Warning: [{placeholderTargetModSetting.DisplayName}] currently points to a different mugshot path ({Path.GetFileName(placeholderTargetModSetting.MugShotFolderPath)}). This will be overwritten."); }
            }
            // --- End Confirmation Dialog ---

            string imagePath = @"Resources\Dragon Drop.png"; // Relative path from executable

            if (ScrollableMessageBox.Confirm(
                    message: sb.ToString(), 
                    title: "Confirm Dragon Drop Operation", 
                    displayImagePath: imagePath)) // Pass the relative path here
            {
                // --- Perform Association/Merge ---
                placeholderTargetModSetting.MugShotFolderPath = mugshotSourceModSetting.MugShotFolderPath;
                // Notify VM_Mods to update validity check (important for UI updates)
                _lazyMods.Value?.RecalculateMugshotValidity(placeholderTargetModSetting);

                if (!mugshotProviderHasGameDataFolders) // Case 1: Merge and Remove Source
                {
                    // Update NPC selections pointing to the old source
                    var npcKeysToUpdate = _settings.SelectedAppearanceMods
                        .Where(kvp => kvp.Value.Equals(mugshotSourceModSetting.DisplayName, StringComparison.OrdinalIgnoreCase))
                        .Select(kvp => kvp.Key).ToList();
                    foreach (var npcKey in npcKeysToUpdate) { _consistencyProvider.SetSelectedMod(npcKey, placeholderTargetModSetting.DisplayName); }

                    // Remove the source VM_ModSetting from VM_Mods
                    bool wasRemoved = _lazyMods.Value?.RemoveModSetting(mugshotSourceModSetting) ?? false;
                    if (!wasRemoved) { Debug.WriteLine($"Warning: Failed to remove mugshotSourceModSetting '{mugshotSourceModSetting.DisplayName}' via VM_Mods.RemoveModSetting."); }

                    Debug.WriteLine($"Merge complete. [{placeholderTargetModSetting.DisplayName}] now uses mugshots from the former [{mugshotSourceModSetting.DisplayName}] entry, which has been removed.");
                }
                else // Case 2: Associate, both remain
                {
                     Debug.WriteLine($"Association complete. [{placeholderTargetModSetting.DisplayName}] will now use mugshots from [{mugshotSourceModSetting.DisplayName}].");
                }

                // Trigger UI Refresh in VM_NpcSelectionBar
                // Use the injected reference
                _vmNpcSelectionBar?.RefreshAppearanceSources();
                // --- End Perform Association/Merge ---
            }
        }
    }
}