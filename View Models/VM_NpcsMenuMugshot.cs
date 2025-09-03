// [VM_NpcsMenuMugshot.cs] - Full Code After Modifications
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
// System.Drawing is removed as ImagePacker now handles raw pixel dimensions internally if needed
// using System.Drawing;
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
using GongSolutions.Wpf.DragDrop;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace NPC_Plugin_Chooser_2.View_Models
{
    [DebuggerDisplay("{ModName}")]
    public class VM_NpcsMenuMugshot : ReactiveObject, IDisposable, IHasMugshotImage, IDragSource, IDropTarget
    {
// --- Existing fields ---
        private readonly FormKey _targetNpcFormKey;
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly VM_NpcSelectionBar _vmNpcSelectionBar;
        private readonly Lazy<VM_Mods> _lazyMods;
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly CompositeDisposable Disposables = new();
        private readonly SolidColorBrush _selectedWithDataBrush = new(Colors.LimeGreen);
        private readonly SolidColorBrush _selectedWithoutDataBrush = new(Colors.DarkMagenta);
        private readonly SolidColorBrush _deselectedWithDataBrush = new(Colors.Transparent);
        //private readonly SolidColorBrush _deselectedWithoutDataBrush = new(Colors.Coral); // Now handled with an overlay


        // --- Existing properties ---
        public ModKey? ModKey { get; }
        public string ModName { get; }
        public FormKey SourceNpcFormKey { get; } // The NPC that provides the appearance
        [Reactive] public string ImagePath { get; set; } = string.Empty;
        [Reactive] public double ImageWidth { get; set; } // Displayed width
        [Reactive] public double ImageHeight { get; set; } // Displayed height
        [Reactive] public bool IsSelected { get; set; }
        [Reactive] public SolidColorBrush BorderColor { get; set; } = new(Colors.Transparent);
        [Reactive] public bool HasMugshot { get; private set; }
        [Reactive] public bool HasNoData { get; private set;}
        [Reactive] public string NoDataNotificationText { get; set; } = string.Empty;
        [Reactive] public bool IsVisible { get; set; } = true;
        [Reactive] public bool IsSetHidden { get; set; } = false;
        [Reactive] public bool CanJumpToMod { get; set; } = false;
        public VM_ModSetting? AssociatedModSetting { get; }
        [Reactive] public string ToolTipString { get; set; } = string.Empty;
        [Reactive] public bool HasIssueNotification { get; set; } = false;
        [Reactive] public NpcIssueType IssueType { get; set; } = NpcIssueType.Template;
        [Reactive] public string IssueNotificationText { get; set; } = string.Empty;
        public bool IsAmbiguousSource { get; }
        public ObservableCollection<ModKey> AvailableSourcePlugins { get; } = new();
        [Reactive] public ModKey? CurrentSourcePlugin { get; set; }
        public bool IsGuestAppearance { get; } 
        public string TargetDisplayName { get; }
        public string OriginalTargetName { get; set; }
        [Reactive] public bool IsFavorite { get; set; }
        [Reactive] public bool IsShareSource { get; private set; }
        [Reactive] public bool IsSelectedByGuest { get; private set; }
        [Reactive] public string ShareSourceTooltipText { get; private set; } = string.Empty;
        

        // --- NEW IHasMugshotImage properties ---
        public int OriginalPixelWidth { get; set; }
        public int OriginalPixelHeight { get; set; }
        public double OriginalDipWidth { get; set; }
        public double OriginalDipHeight { get; set; }
        public double OriginalDipDiagonal { get; set; }
        [Reactive] public ImageSource? MugshotSource { get; set; }

        // --- NEW Property for Compare Checkbox ---
        [Reactive] public bool IsCheckedForCompare { get; set; } = false;


        // --- Existing Commands ---
        public ReactiveCommand<Unit, Unit> SelectCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
        public ReactiveCommand<Unit, Unit> HideCommand { get; }
        public ReactiveCommand<Unit, Unit> UnhideCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectAllFromThisModCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectAvailableFromThisModCommand { get; }
        public ReactiveCommand<Unit, Unit> HideAllFromThisModCommand { get; }
        public ReactiveCommand<Unit, Unit> UnhideAllFromThisModCommand { get; }
        public ReactiveCommand<Unit, Unit> JumpToModCommand { get; }
        public ReactiveCommand<ModKey, Unit> SetNpcSourcePluginCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectSameSourcePluginWherePossibleCommand { get; }
        public ReactiveCommand<Unit, Unit> ShareWithNpcCommand { get; }
        public ReactiveCommand<Unit, Unit> UnshareFromNpcCommand { get; }
        public ReactiveCommand<Unit, Unit> AddToFavoritesCommand { get; }
        

        // --- Placeholder Image Configuration --- 
        private const string PlaceholderResourceRelativePath = @"Resources\No Mugshot.png";

        private static readonly string FullPlaceholderPath =
            Path.Combine(AppContext.BaseDirectory, PlaceholderResourceRelativePath);

        private static readonly bool PlaceholderExists = File.Exists(FullPlaceholderPath);

        public VM_NpcsMenuMugshot(
            string modName,
            string npcDisplayName,
            FormKey targetNpcFormKey,
            FormKey sourceNpcFormKey,
            ModKey? overrideModeKey,
            string? imagePath, // This is the path to the *actual* mugshot if one exists for this mod/NPC combo
            Settings settings,
            NpcConsistencyProvider consistencyProvider,
            VM_NpcSelectionBar vmNpcSelectionBar,
            Lazy<VM_Mods> lazyMods,
            EnvironmentStateProvider environmentStateProvider)
        {
            ModName = modName;
            _lazyMods = lazyMods;
            AssociatedModSetting = _lazyMods.Value?.AllModSettings.FirstOrDefault(m => m.DisplayName == modName);
            ModKey = overrideModeKey ?? AssociatedModSetting?.CorrespondingModKeys.FirstOrDefault();
            _targetNpcFormKey = targetNpcFormKey;
            SourceNpcFormKey = sourceNpcFormKey;
            IsGuestAppearance = !targetNpcFormKey.Equals(sourceNpcFormKey);
            TargetDisplayName = npcDisplayName;
            OriginalTargetName = npcDisplayName;
            _settings = settings;
            _consistencyProvider = consistencyProvider;
            _vmNpcSelectionBar = vmNpcSelectionBar;
            _environmentStateProvider = environmentStateProvider;
            
            HasNoData = (AssociatedModSetting == null || (!AssociatedModSetting.CorrespondingFolderPaths.Any() && !AssociatedModSetting.IsAutoGenerated));
            IsFavorite = _settings.FavoriteFaces.Contains((this.SourceNpcFormKey, this.ModName));

            if (HasNoData)
            {
                NoDataNotificationText =
                    $"You have Mugshots installed for {AssociatedModSetting?.DisplayName ?? "this mod"} but the mod itself is not installed. {Environment.NewLine}You can still select this as a placeholder, but {npcDisplayName} won't be included in the output until the actual mod is installed.";
            }
            
            // --- NEW Ambiguous Source Initialization ---
            IsAmbiguousSource = AssociatedModSetting?.AmbiguousNpcFormKeys.Contains(_targetNpcFormKey) ?? false;
            CurrentSourcePlugin = AssociatedModSetting?.NpcPluginDisambiguation.GetValueOrDefault(_targetNpcFormKey);

            if (IsAmbiguousSource && AssociatedModSetting != null && AssociatedModSetting.AvailablePluginsForNpcs.TryGetValue(_targetNpcFormKey, out var available))
            {
                AvailableSourcePlugins = new ObservableCollection<ModKey>(available.OrderBy(k => k.FileName.String));
            }

            var canSetNpcSource = this.WhenAnyValue(x => x.IsAmbiguousSource).Select(isAmbiguous => isAmbiguous);
            SetNpcSourcePluginCommand = ReactiveCommand.Create<ModKey>(SetNpcSourcePluginInternal, canSetNpcSource);

            SelectSameSourcePluginWherePossibleCommand = ReactiveCommand.Create(() =>
                {
                    if (this.AssociatedModSetting != null && this.CurrentSourcePlugin.HasValue)
                    {
                        this.AssociatedModSetting.SetAndNotifySourcePluginForAll(this.CurrentSourcePlugin.Value);
                    }
                },
                this.WhenAnyValue(x => x.IsAmbiguousSource, x => x.CurrentSourcePlugin,
                    (ambiguous, source) => ambiguous && source.HasValue));

            SetNpcSourcePluginCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error setting NPC source plugin: {ex.Message}")).DisposeWith(Disposables);
            SelectSameSourcePluginWherePossibleCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error setting NPC source plugin: {ex.Message}")).DisposeWith(Disposables);

            // --- Image Path and HasMugshot Logic ---
            bool realMugshotExists = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);
            HasMugshot = realMugshotExists; // True if a specific mugshot was found for THIS mod/NPC combination

            string pathToLoad = string.Empty;
            if (realMugshotExists)
            {
                pathToLoad = imagePath!;
            }
            else if (PlaceholderExists)
            {
                pathToLoad = FullPlaceholderPath;
                // HasMugshot remains false, as this is a placeholder
            }

            ImagePath = pathToLoad; // Set ImagePath to actual or placeholder

            if (!string.IsNullOrWhiteSpace(ImagePath))
            {
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
                    
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(ImagePath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // Releases file lock
                    bitmap.EndInit();
                    bitmap.Freeze(); // Good practice for performance
                    this.MugshotSource = bitmap;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error getting dimensions for '{ImagePath}': {ex.Message}");
                    ImagePath = string.Empty; // Clear path if dimensions fail
                    // HasMugshot should already be false if it was a placeholder, or becomes false if real image failed
                    if (realMugshotExists) HasMugshot = false;
                    ImageWidth = 0;
                    ImageHeight = 0;
                    OriginalPixelWidth = 0;
                    OriginalPixelHeight = 0;
                    OriginalDipWidth = 0;
                    OriginalDipHeight = 0;
                    OriginalDipDiagonal = 0;
                }
            }
            else
            {
                ImageWidth = 0;
                ImageHeight = 0;
                OriginalPixelWidth = 0;
                OriginalPixelHeight = 0;
                OriginalDipWidth = 0;
                OriginalDipHeight = 0;
                OriginalDipDiagonal = 0;
            }

            this.WhenAnyValue(x => x.IsSelected)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(isSelected => SetBorderAndTooltip(isSelected))
                .DisposeWith(Disposables);

            CanJumpToMod = _vmNpcSelectionBar.CanJumpToMod(modName);
            IsSelected = _consistencyProvider.IsModSelected(_targetNpcFormKey, ModName, SourceNpcFormKey);
            SelectCommand = ReactiveCommand.Create(SelectThisMod);
            var canToggleFullScreen =
                this.WhenAnyValue(x => x.ImagePath, path => !string.IsNullOrEmpty(path) && File.Exists(path));
            ToggleFullScreenCommand = ReactiveCommand.Create(ToggleFullScreen, canToggleFullScreen);
            HideCommand = ReactiveCommand.Create(HideThisMod);
            UnhideCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.UnhideSelectedMod(this));
            SelectAllFromThisModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.SelectAllFromMod(this, false));
            SelectAvailableFromThisModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.SelectAllFromMod(this, true));
            HideAllFromThisModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.HideAllFromMod(this));
            UnhideAllFromThisModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.UnhideAllFromMod(this));
            JumpToModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.JumpToMod(this),
                this.WhenAnyValue(x => x.CanJumpToMod));
            
            // The command now sends a message containing itself.
            ShareWithNpcCommand = ReactiveCommand.Create(() => 
            {
                MessageBus.Current.SendMessage(new ShareAppearanceRequest(this));
            });
            ShareWithNpcCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error sharing NPC appearance: {ex.Message}")).DisposeWith(Disposables);
            
            UnshareFromNpcCommand = ReactiveCommand.Create(() => 
            {
                MessageBus.Current.SendMessage(new UnshareAppearanceRequest(this));
            });
            UnshareFromNpcCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error un-sharing NPC appearance: {ex.Message}")).DisposeWith(Disposables);
            
            AddToFavoritesCommand = ReactiveCommand.Create(ToggleFavorite);
            AddToFavoritesCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error updating favorites: {ex.Message}")).DisposeWith(Disposables);

            SelectCommand.ThrownExceptions
                .Subscribe(ex => ScrollableMessageBox.Show($"Error selecting mod: {ex.Message}"))
                .DisposeWith(Disposables);
            ToggleFullScreenCommand.ThrownExceptions
                .Subscribe(ex => ScrollableMessageBox.Show($"Error showing image: {ex.Message}"))
                .DisposeWith(Disposables);
            HideCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.Show($"Error hiding mod: {ex.Message}"))
                .DisposeWith(Disposables);
            UnhideCommand.ThrownExceptions
                .Subscribe(ex => ScrollableMessageBox.Show($"Error unhiding mod: {ex.Message}"))
                .DisposeWith(Disposables);
            SelectAllFromThisModCommand.ThrownExceptions
                .Subscribe(ex => ScrollableMessageBox.Show($"Error selecting all from mod: {ex.Message}"))
                .DisposeWith(Disposables);
            HideAllFromThisModCommand.ThrownExceptions
                .Subscribe(ex => ScrollableMessageBox.Show($"Error hiding all from mod: {ex.Message}"))
                .DisposeWith(Disposables);
            UnhideAllFromThisModCommand.ThrownExceptions
                .Subscribe(ex => ScrollableMessageBox.Show($"Error unhiding all from mod: {ex.Message}"))
                .DisposeWith(Disposables);
            JumpToModCommand.ThrownExceptions
                .Subscribe(ex => ScrollableMessageBox.Show($"Error jumping to mod: {ex.Message}"))
                .DisposeWith(Disposables);
            
            _consistencyProvider.NpcSelectionChanged
                .Where(args => args.NpcFormKey == _targetNpcFormKey)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(args => IsSelected = (args.SelectedModName == ModName && args.SourceNpcFormKey.Equals(this.SourceNpcFormKey)))
                .DisposeWith(Disposables);

            SetBorderAndTooltip(IsSelected);
            
            InitializeShareSourceListener();
        }

        private void SelectThisMod()
        {
            if (IsSelected)
            {
                System.Diagnostics.Debug.WriteLine($"Deselecting mod '{ModName}' for NPC '{_targetNpcFormKey}'");
                _consistencyProvider.ClearSelectedMod(_targetNpcFormKey);
            }
            else
            {
                var previousSelection = _consistencyProvider.GetSelectedMod(_targetNpcFormKey);
                
                System.Diagnostics.Debug.WriteLine($"Selecting mod '{ModName}' for NPC '{_targetNpcFormKey}'");
                _consistencyProvider.SetSelectedMod(_targetNpcFormKey, ModName, SourceNpcFormKey);
                
                if (HasIssueNotification && IssueType == NpcIssueType.Template)
                {
                    if (AssociatedModSetting != null && _lazyMods.IsValueCreated)
                    {
                        if (!_lazyMods.Value.UpdateTemplates(_targetNpcFormKey, AssociatedModSetting))
                        {
                            if (previousSelection.ModName != null)
                            {
                                _consistencyProvider.SetSelectedMod(_targetNpcFormKey, previousSelection.ModName,
                                    previousSelection.SourceNpcFormKey);
                            }
                            else
                            {
                                _consistencyProvider.ClearSelectedMod(_targetNpcFormKey);
                            }
                        }
                    }
                    else // fall back to simple analzyer
                    {
                        CheckAndHandleTemplates();
                    }
                }
            }
        }

        private void SetBorderAndTooltip(bool isSelected)
        {
            bool hasData = !HasNoData;
            
            if (isSelected && hasData)
            {
                BorderColor = _selectedWithDataBrush;
                ToolTipString = "Selected. Mugshot has associated Mod Data and is ready for patch generation.";
            }
            else if (isSelected && !hasData)
            {
                BorderColor = _selectedWithoutDataBrush;
                ToolTipString =
                    "Selected but Mugshot has no associated Mod Data. Patcher run will skip this NPC until Mod Data is linked to this mugshot";
            }

            if (!isSelected && hasData)
            {
                BorderColor = _deselectedWithDataBrush;
                ToolTipString = "Not Selected. Mugshot has associated Mod Data and is ready to go if you select it.";
            }
            else if (!isSelected && !hasData)
            {
                //BorderColor = _deselectedWithoutDataBrush; // Now handled with an overlay
                BorderColor = _deselectedWithDataBrush;
                ToolTipString =
                    "Not Selected. Mugshot has no associated Mod Data. If you select it, Patcher run will skip this NPC until Mod Data is linked to this mugshot";
            }
        }

        private void CheckAndHandleTemplates()
        {
            if (_targetNpcFormKey != null && ModKey != null)
            {
                string imagePath = @"Resources\Face Bug.png";
                
                var context = _environmentStateProvider.LinkCache
                    .ResolveAllContexts<INpc, INpcGetter>(_targetNpcFormKey)
                    .FirstOrDefault(x => x.ModKey.Equals(ModKey));

                if (context != null &&
                    context.Record.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits))
                {
                    string message = String.Empty;
                    string title = String.Empty;
                    string templateDispName = String.Empty;
                    if (context.Record.Template == null || context.Record.Template.IsNull)
                    {
                        message =
                            "The associated data for this NPC shows that it is supposed to have a template, but there is no template set. This will probably result in a bugged appearance.";
                        title = "Are you sure?";
                        if (!ScrollableMessageBox.Confirm(message, title, displayImagePath: imagePath))
                        {
                            _consistencyProvider.ClearSelectedMod(_targetNpcFormKey);
                        }
                    }
                    else if (AssociatedModSetting != null)
                    {
                        if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(context.Record.Template.FormKey,
                                out INpcGetter? templateGetter) && templateGetter.EditorID != null)
                        {
                            templateDispName = templateGetter.EditorID + " (" +
                                               context.Record.Template.FormKey.ToString() + ")";
                        }
                        else
                        {
                            templateDispName = context.Record.Template.FormKey.ToString();
                        }
                        
                        if (!AssociatedModSetting.NpcFormKeys.Contains(context.Record.Template.FormKey))
                        {
                            message =
                                "The associated data for this NPC shows that it is supposed to use " + templateDispName +
                                " as its template, but " + (ModKey?.FileName ?? AssociatedModSetting.DisplayName) +
                                " doesn't appear to contain this NPC. This may result in a bugged appearance.";
                            title = "Are you sure?";
                            if (!ScrollableMessageBox.Confirm(message, title, displayImagePath: imagePath))
                            {
                                _consistencyProvider.ClearSelectedMod(_targetNpcFormKey);
                            }
                        }
                        else if (AssociatedModSetting.NpcFormKeys.Contains(context.Record.Template.FormKey) &&
                                 !_consistencyProvider.IsModSelected(context.Record.Template.FormKey,
                                     AssociatedModSetting.DisplayName, context.Record.Template.FormKey))
                        {
                            message =
                                "The associated data for this NPC shows that it is supposed to use " +
                                templateDispName +
                                " as its template. Would you like to select " + AssociatedModSetting.DisplayName +
                                " as the Appearance Mod for " + templateDispName + "?" +
                                " Failing to do so is likely to result in a bugged appearance.";
                            title = "Auto-Select Template?";
                            if (ScrollableMessageBox.Confirm(message, title, displayImagePath: imagePath))
                            {
                                _consistencyProvider.SetSelectedMod(context.Record.Template.FormKey, AssociatedModSetting.DisplayName, context.Record.Template.FormKey);
                            }
                        }
                    }
                }
            }
        }
        
        private void SetNpcSourcePluginInternal(ModKey selectedPluginKey)
        {
            if (AssociatedModSetting == null || !IsAmbiguousSource)
            {
                Debug.WriteLine($"SetNpcSourcePluginInternal called for non-ambiguous NPC {_targetNpcFormKey}. This should not happen.");
                return;
            }
            if (selectedPluginKey.IsNull)
            {
                Debug.WriteLine($"SetNpcSourcePluginInternal called with a null/invalid ModKey for NPC {_targetNpcFormKey}.");
                return;
            }

            // Call back to the parent VM_ModSetting to handle the logic
            bool successfullyUpdated = AssociatedModSetting.SetSingleNpcSourcePlugin(_targetNpcFormKey, selectedPluginKey);

            if (successfullyUpdated)
            {
                // The parent VM_ModSetting has updated its NpcPluginDisambiguation map.
                // Now, this specific VM_NpcsMenuMugshot instance should update its own CurrentSourcePlugin
                // to reflect the new choice for the context menu checkmark.
                if (AssociatedModSetting.NpcPluginDisambiguation.TryGetValue(this._targetNpcFormKey, out var newResolvedSource))
                {
                    this.CurrentSourcePlugin = newResolvedSource;
                }
                else
                {
                    this.CurrentSourcePlugin = selectedPluginKey;
                    Debug.WriteLine($"Warning: Could not re-resolve source for NPC {_targetNpcFormKey} from NpcPluginDisambiguation map after setting. Displayed checkmark might be based on direct selection.");
                }
            }
        }

        private void ToggleFullScreen()
        {
            // Use ImagePath directly as it points to either the real mugshot or the placeholder
            if (!string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath))
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
                    // This catch might be redundant if File.Exists is reliable, but good for safety.
                    ScrollableMessageBox.ShowWarning(
                        $"Mugshot not found or path is invalid (exception during display):\n{ImagePath}\n{ex.Message}");
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
        
        private void ToggleFavorite()
        {
            var favoriteTuple = (this.SourceNpcFormKey, this.ModName);
            if (IsFavorite)
            {
                _settings.FavoriteFaces.Remove(favoriteTuple);
                IsFavorite = false;
                Debug.WriteLine($"Removed {favoriteTuple} from favorites.");
            }
            else
            {
                _settings.FavoriteFaces.Add(favoriteTuple);
                IsFavorite = true;
                Debug.WriteLine($"Added {favoriteTuple} to favorites.");
            }
        }

        private void InitializeShareSourceListener()
        {
            if (_settings.GuestAppearances == null || !_settings.GuestAppearances.Any())
            {
                IsShareSource = false;
                return;
            }

            var reverseGuestLookup = new Dictionary<(FormKey, string), List<FormKey>>();
            foreach (var entry in _settings.GuestAppearances)
            {
                var targetNpcKey = entry.Key;
                foreach (var (modName, sourceNpcKey, _) in entry.Value)
                {
                    var sourceTuple = (sourceNpcKey, modName);
                    if (!reverseGuestLookup.TryGetValue(sourceTuple, out var targets))
                    {
                        targets = new List<FormKey>();
                        reverseGuestLookup[sourceTuple] = targets;
                    }

                    targets.Add(targetNpcKey);
                }
            }

            var thisAppearanceKey = (this.SourceNpcFormKey, this.ModName);
            if (reverseGuestLookup.TryGetValue(thisAppearanceKey, out var guestTargetKeys))
            {
                IsShareSource = true;

                _consistencyProvider.NpcSelectionChanged
                    .Where(args => guestTargetKeys.Contains(args.NpcFormKey))
                    .Throttle(TimeSpan.FromMilliseconds(50))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => UpdateShareSourceStatusAndTooltip(guestTargetKeys))
                    .DisposeWith(Disposables);

                UpdateShareSourceStatusAndTooltip(guestTargetKeys);
            }
        }

        private void UpdateShareSourceStatusAndTooltip(List<FormKey> guestTargetKeys)
        {
            var selectedGuests = new List<string>();
            var unselectedGuests = new List<string>();

            var npcNameMap = _vmNpcSelectionBar.AllNpcs.ToDictionary(n => n.NpcFormKey, n => n.DisplayName);

            foreach (var guestNpcKey in guestTargetKeys)
            {
                bool isSelectedForGuest =
                    _consistencyProvider.IsModSelected(guestNpcKey, this.ModName, this.SourceNpcFormKey);
                string guestNpcName = npcNameMap.TryGetValue(guestNpcKey, out var name) ? name : guestNpcKey.ToString();

                if (isSelectedForGuest)
                {
                    selectedGuests.Add(guestNpcName);
                }
                else
                {
                    unselectedGuests.Add(guestNpcName);
                }
            }

            IsSelectedByGuest = selectedGuests.Any();

            var sb = new System.Text.StringBuilder();
            if (unselectedGuests.Any())
            {
                sb.AppendLine("Shared with: " + string.Join(", ", unselectedGuests.OrderBy(n => n)));
            }

            if (selectedGuests.Any())
            {
                sb.AppendLine("Selected for: " + string.Join(", ", selectedGuests.OrderBy(n => n)));
            }

            ShareSourceTooltipText = sb.ToString().Trim();
        }

        public void Dispose()
        {
            Disposables.Dispose();
        }

        // --- IDragSource Implementation ---

        public bool CanStartDrag(IDragInfo dragInfo)
        {
            return true;
        }

        public void StartDrag(IDragInfo dragInfo)
        {
            dragInfo.Data = this;
            dragInfo.Effects = DragDropEffects.Move | DragDropEffects.Copy;
            Debug.WriteLine($"VM_NpcsMenuMugshot.StartDrag: Dragging '{this.ModName}'");
        }

        public void Dropped(IDropInfo dropInfo)
        {
            Debug.WriteLine(
                $"VM_NpcsMenuMugshot.Dropped (Source): '{this.ModName}' was dropped with effect {dropInfo.Effects}");
        }

        public void DragCancelled()
        {
            Debug.WriteLine($"VM_NpcsMenuMugshot.DragCancelled: Drag of '{this.ModName}' cancelled.");
        }

        public void DragDropOperationFinished(DragDropEffects operationResult, IDragInfo dragInfo)
        {
            Debug.WriteLine(
                $"VM_NpcsMenuMugshot.DragDropOperationFinished: Operation for '{this.ModName}' finished with result {operationResult}.");
        }

        public bool TryMove(IDropInfo dropInfo)
        {
            return false;
        }

        public bool TryCatchOccurredException(Exception exception)
        {
            Debug.WriteLine(
                $"ERROR VM_NpcsMenuMugshot.TryCatchOccurredException (Source): Exception during D&D for '{this.ModName}': {exception}");
            return true;
        }

        // --- IDropTarget Implementation ---
        
        void IDropTarget.DragOver(IDropInfo dropInfo)
        {
            var sourceItem = dropInfo.Data as VM_NpcsMenuMugshot;
            dropInfo.Effects = DragDropEffects.None;

            if (sourceItem == null || sourceItem == this) return;

            bool sourceIsRealMugshotVm = sourceItem.HasMugshot;
            bool targetIsRealMugshotVm = this.HasMugshot;

            // NEW: Allow dropping a real mugshot onto another real mugshot.
            if (sourceIsRealMugshotVm && targetIsRealMugshotVm)
            {
                dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
                dropInfo.Effects = DragDropEffects.Move; // Using Move effect to signify an action
                return;
            }

            // Original case: Allow dropping between a real mugshot and a placeholder.
            if (!((sourceIsRealMugshotVm && !targetIsRealMugshotVm) ||
                  (!sourceIsRealMugshotVm && targetIsRealMugshotVm)))
            {
                return; // Not a valid pair (e.g., two placeholders)
            }

            var mugshotVmApp = sourceIsRealMugshotVm ? sourceItem : this;
            var placeholderVmApp = sourceIsRealMugshotVm ? this : sourceItem;

            var mugshotModSetting = mugshotVmApp.AssociatedModSetting;
            var placeholderModSetting = placeholderVmApp.AssociatedModSetting;

            bool mugshotPathValid = mugshotModSetting != null &&
                                    !string.IsNullOrWhiteSpace(mugshotVmApp.ImagePath) &&
                                    File.Exists(mugshotVmApp.ImagePath);
            bool placeholderPathsValid = placeholderModSetting != null &&
                                         (placeholderModSetting.CorrespondingFolderPaths.Any() || placeholderModSetting.IsAutoGenerated);

            if (mugshotPathValid && placeholderPathsValid)
            {
                dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
                dropInfo.Effects = DragDropEffects.Move;
            }
        }

        void IDropTarget.Drop(IDropInfo dropInfo)
        {
            var sourceItem = dropInfo.Data as VM_NpcsMenuMugshot;
            if (sourceItem == null || sourceItem == this) return;

            bool sourceIsRealMugshotVm = sourceItem.HasMugshot;
            bool targetIsRealMugshotVm = this.HasMugshot;

            // NEW: Handle drop between two real mugshots
            if (sourceIsRealMugshotVm && targetIsRealMugshotVm)
            {
                var droppedMod = sourceItem.ModName;
                var droppedNpc = sourceItem.SourceNpcFormKey;
                var targetMod = this.ModName;
                var targetNpc = this.SourceNpcFormKey;
                _vmNpcSelectionBar.MassUpdateNpcSelections(targetMod, targetNpc, droppedMod, droppedNpc);
                return; // Exit after handling this case
            }

            // Original case: Handle drop between a real mugshot and a placeholder
            if (!((sourceIsRealMugshotVm && !targetIsRealMugshotVm) ||
                  (!sourceIsRealMugshotVm && targetIsRealMugshotVm))) return;

            var mugshotVmApp = sourceIsRealMugshotVm ? sourceItem : this;
            var placeholderVmApp = sourceIsRealMugshotVm ? this : sourceItem;

            var mugshotSourceModSetting = mugshotVmApp.AssociatedModSetting;
            var placeholderTargetModSetting = placeholderVmApp.AssociatedModSetting;

            if (mugshotSourceModSetting == null || placeholderTargetModSetting == null ||
                string.IsNullOrWhiteSpace(sourceItem.ImagePath) ||
                !File.Exists(sourceItem.ImagePath) ||
                (!placeholderTargetModSetting.CorrespondingFolderPaths.Any() && !placeholderTargetModSetting.IsAutoGenerated))
            {
                ScrollableMessageBox.ShowError(
                    "Drop conditions not met (Validation failed in Drop). Ensure mugshot provider has valid path and placeholder has mod folders.",
                    "Drop Error");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(
                $"Are you sure you want to associate the Mugshots from [{mugshotSourceModSetting.DisplayName}] with the Mod Folder(s) from [{placeholderTargetModSetting.DisplayName}]?");
            bool mugshotProviderHasGameDataFolders = mugshotSourceModSetting.CorrespondingFolderPaths.Any();

            if (mugshotProviderHasGameDataFolders)
            {
                sb.AppendLine(
                    $"\n[{placeholderTargetModSetting.DisplayName}] will now use mugshots from [{mugshotSourceModSetting.DisplayName}]. Both mod entries will remain.");
            }
            else
            {
                sb.AppendLine(
                    $"\n[{placeholderTargetModSetting.DisplayName}] will take over the mugshots from [{mugshotSourceModSetting.DisplayName}].");
                sb.AppendLine($"The separate entry for [{mugshotSourceModSetting.DisplayName}] will be removed.");
            }

            string imagePath = @"Resources\Dragon Drop.png";

            if (ScrollableMessageBox.Confirm(
                    message: sb.ToString(),
                    title: "Confirm Dragon Drop Operation",
                    displayImagePath: imagePath))
            {
                placeholderTargetModSetting.MugShotFolderPaths.AddRange(mugshotSourceModSetting.MugShotFolderPaths);
                _lazyMods.Value?.RecalculateMugshotValidity(placeholderTargetModSetting);

                if (!mugshotProviderHasGameDataFolders)
                {
                    var npcKeysToUpdate = _settings.SelectedAppearanceMods
                        .Where(kvp =>
                            kvp.Value.ModName.Equals(mugshotSourceModSetting.DisplayName, StringComparison.OrdinalIgnoreCase))
                        .Select(kvp => kvp.Key).ToList();

                    // Re-assign their selection to the newly merged mod.
                    foreach (var npcKey in npcKeysToUpdate)
                    {
                        // The source of the new selection is the NPC's own FormKey.
                        _consistencyProvider.SetSelectedMod(npcKey, placeholderTargetModSetting.DisplayName, npcKey);
                    }

                    // Remove the now-redundant mugshot-only mod setting.
                    bool wasRemoved = _lazyMods.Value?.RemoveModSetting(mugshotSourceModSetting) ?? false;
                    if (!wasRemoved)
                    {
                        Debug.WriteLine(
                            $"Warning: Failed to remove mugshotSourceModSetting '{mugshotSourceModSetting.DisplayName}' via VM_Mods.RemoveModSetting.");
                    }

                    Debug.WriteLine(
                        $"Merge complete. [{placeholderTargetModSetting.DisplayName}] now uses mugshots from the former [{mugshotSourceModSetting.DisplayName}] entry, which has been removed.");
                }
                else
                {
                    Debug.WriteLine(
                        $"Association complete. [{placeholderTargetModSetting.DisplayName}] will now use mugshots from [{mugshotSourceModSetting.DisplayName}].");
                }

                _vmNpcSelectionBar?.RefreshCurrentNpcAppearanceSources();
            }
        }
    }
}