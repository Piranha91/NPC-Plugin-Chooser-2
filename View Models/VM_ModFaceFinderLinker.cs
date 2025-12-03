using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GongSolutions.Wpf.DragDrop;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.Generic;
using DynamicData;
using DynamicData.Binding;

namespace NPC_Plugin_Chooser_2.View_Models
{
    public enum LinkState { None, Automatic, Manual }
    public enum FilterMode { All, Linked, Unlinked }

    public class VM_FaceFinderModItem : ReactiveObject
    {
        public string Name { get; }
        [Reactive] public bool IsLinked { get; set; }
        [Reactive] public string TooltipText { get; set; }

        public VM_FaceFinderModItem(string name)
        {
            Name = name;
            TooltipText = "This mod is not currently linked.";
        }
    }

    public class LinkInfo : ReactiveObject
    {
        public VM_LocalModItem Parent { get; }
        public string FaceFinderName { get; }
        public LinkState State { get; }

        public LinkInfo(string faceFinderName, LinkState state, VM_LocalModItem parent)
        {
            FaceFinderName = faceFinderName;
            State = state;
            Parent = parent;
        }
    }

    public class VM_LocalModItem : ReactiveObject
    {
        public string LocalName { get; }
        public ObservableCollection<LinkInfo> Links { get; } = new();

        public bool IsCurrentlyLinked => Links.Any();

        public string TooltipText
        {
            get
            {
                if (!IsCurrentlyLinked) return "Not linked to any FaceFinder mods.";
                
                var manualLinks = Links.Where(l => l.State == LinkState.Manual).Select(l => $"'{l.FaceFinderName}'").ToList();
                var autoLinks = Links.Where(l => l.State == LinkState.Automatic).Select(l => $"'{l.FaceFinderName}'").ToList();

                var parts = new List<string>();
                if (manualLinks.Any()) parts.Add("Manually linked to: " + string.Join(", ", manualLinks));
                if (autoLinks.Any()) parts.Add("Automatically linked to: " + string.Join(", ", autoLinks));
                
                return string.Join(". ", parts) + ".";
            }
        }

        public VM_LocalModItem(string localName)
        {
            LocalName = localName;
            Links.CollectionChanged += (sender, args) =>
            {
                this.RaisePropertyChanged(nameof(IsCurrentlyLinked));
                this.RaisePropertyChanged(nameof(TooltipText));
            };
        }
    }
    
    public class VM_ModFaceFinderLinker : ReactiveObject, IDropTarget
    {
        private readonly Settings _settings;
        private readonly FaceFinderClient _faceFinderClient;
        private readonly VM_Mods _vmMods;

        [Reactive] public bool IsLoading { get; private set; }
        private readonly SourceList<VM_FaceFinderModItem> _faceFinderMods = new();
        private readonly ReadOnlyObservableCollection<VM_FaceFinderModItem> _filteredFaceFinderMods;
        public ReadOnlyObservableCollection<VM_FaceFinderModItem> FilteredFaceFinderMods => _filteredFaceFinderMods;

        private readonly SourceList<VM_LocalModItem> _localMods = new();
        private readonly ReadOnlyObservableCollection<VM_LocalModItem> _filteredLocalMods;
        public ReadOnlyObservableCollection<VM_LocalModItem> FilteredLocalMods => _filteredLocalMods;

        public ReactiveCommand<LinkInfo, Unit> UnlinkCommand { get; }
        
        // Filter Properties
        [Reactive] public string FaceFinderSearchText { get; set; } = string.Empty;
        [Reactive] public FilterMode SelectedFaceFinderFilter { get; set; } = FilterMode.All;
        [Reactive] public string LocalModsSearchText { get; set; } = string.Empty;
        [Reactive] public FilterMode SelectedLocalModsFilter { get; set; } = FilterMode.All;
        public IEnumerable<FilterMode> FilterModes => Enum.GetValues(typeof(FilterMode)).Cast<FilterMode>();

        public VM_ModFaceFinderLinker(Settings settings, FaceFinderClient faceFinderClient, VM_Mods vmMods)
        {
            _settings = settings;
            _faceFinderClient = faceFinderClient;
            _vmMods = vmMods;

            UnlinkCommand = ReactiveCommand.Create<LinkInfo>(Unlink);
            
            // --- FaceFinder Filter Pipeline ---
            var ffSearchFilter = this.WhenAnyValue(x => x.FaceFinderSearchText)
                .Select(text => new Func<VM_FaceFinderModItem, bool>(item => 
                    string.IsNullOrWhiteSpace(text) || item.Name.Contains(text, StringComparison.OrdinalIgnoreCase)));

            var ffLinkFilter = this.WhenAnyValue(x => x.SelectedFaceFinderFilter)
                .Select(mode => new Func<VM_FaceFinderModItem, bool>(item => 
                    mode == FilterMode.All || (mode == FilterMode.Linked && item.IsLinked) || (mode == FilterMode.Unlinked && !item.IsLinked)));
    
            _faceFinderMods.Connect()
                .AutoRefresh(item => item.IsLinked) // Re-apply filter when IsLinked changes
                .Filter(ffSearchFilter)
                .Filter(ffLinkFilter)
                .Sort(SortExpressionComparer<VM_FaceFinderModItem>.Ascending(item => item.Name))
                .Bind(out _filteredFaceFinderMods)
                .Subscribe();

            // --- Local Mods Filter Pipeline ---
            var localSearchFilter = this.WhenAnyValue(x => x.LocalModsSearchText)
                .Select(text => new Func<VM_LocalModItem, bool>(item => 
                    string.IsNullOrWhiteSpace(text) || item.LocalName.Contains(text, StringComparison.OrdinalIgnoreCase)));

            var localLinkFilter = this.WhenAnyValue(x => x.SelectedLocalModsFilter)
                .Select(mode => new Func<VM_LocalModItem, bool>(item =>
                    mode == FilterMode.All || (mode == FilterMode.Linked && item.IsCurrentlyLinked) || (mode == FilterMode.Unlinked && !item.IsCurrentlyLinked)));

            _localMods.Connect()
                .AutoRefresh(item => item.IsCurrentlyLinked) // Re-apply filter when IsCurrentlyLinked changes
                .Filter(localSearchFilter)
                .Filter(localLinkFilter)
                .Sort(SortExpressionComparer<VM_LocalModItem>.Ascending(item => item.LocalName))
                .Bind(out _filteredLocalMods)
                .Subscribe();

            _ = LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            IsLoading = true;
    
            var ffModsTask = _faceFinderClient.GetAllModNamesAsync();
            var localMods = _vmMods.AllModSettings.Select(s => s.DisplayName).OrderBy(s => s).ToList();
            var ffMods = await ffModsTask;
    
            // CHANGED: Use a case-insensitive string comparer for the dictionary.
            var manualMappings = new Dictionary<string, List<string>>(_settings.FaceFinderModNameMappings, StringComparer.OrdinalIgnoreCase);
    
            // ADDED: Create a case-insensitive HashSet for efficient 'Contains' checks.
            var ffModsSet = new HashSet<string>(ffMods, StringComparer.OrdinalIgnoreCase);
    
            var localModVms = new List<VM_LocalModItem>();
            foreach (var mod in localMods)
            {
                var localModVm = new VM_LocalModItem(mod);
                if (manualMappings.TryGetValue(mod, out var linkedNames))
                {
                    foreach (var linkedName in linkedNames.OrderBy(s => s))
                    {
                        localModVm.Links.Add(new LinkInfo(linkedName, LinkState.Manual, localModVm));
                    }
                }
        
                // CHANGED: Use the new HashSet and a case-insensitive equality check.
                if (ffModsSet.Contains(mod) && !localModVm.Links.Any(l => l.FaceFinderName.Equals(mod, StringComparison.OrdinalIgnoreCase)))
                {
                    localModVm.Links.Add(new LinkInfo(mod, LinkState.Automatic, localModVm));
                }
                localModVms.Add(localModVm);
            }
            _localMods.AddRange(localModVms);
    
            // CHANGED: Use a case-insensitive string comparer for the final link check.
            var allLinkedFfMods = new HashSet<string>(_localMods.Items.SelectMany(m => m.Links).Select(l => l.FaceFinderName), StringComparer.OrdinalIgnoreCase);
            var ffModVms = ffMods.Select(mod => new VM_FaceFinderModItem(mod) { IsLinked = allLinkedFfMods.Contains(mod) }).ToList();
            _faceFinderMods.AddRange(ffModVms);

            UpdateAllFaceFinderTooltips();

            IsLoading = false;
        }

        private void Unlink(LinkInfo linkToUnlink)
        {
            var localMod = linkToUnlink.Parent;
            var ffName = linkToUnlink.FaceFinderName;

            if (linkToUnlink.State == LinkState.Manual)
            {
                if (_settings.FaceFinderModNameMappings.TryGetValue(localMod.LocalName, out var mappings))
                {
                    mappings.Remove(ffName);
                    if (mappings.Count == 0)
                    {
                        _settings.FaceFinderModNameMappings.Remove(localMod.LocalName);
                    }
                }
            }

            localMod.Links.Remove(linkToUnlink);

            bool ffModExists = _faceFinderMods.Items.Any(f => f.Name == localMod.LocalName);
            if (ffModExists && localMod.LocalName == ffName && !localMod.Links.Any(l => l.FaceFinderName == ffName))
            {
                localMod.Links.Add(new LinkInfo(ffName, LinkState.Automatic, localMod));
            }
            
            UpdateFaceFinderLinkStatus(ffName);
            UpdateAllFaceFinderTooltips();
        }

        private void UpdateFaceFinderLinkStatus(string? ffModName)
        {
            if (string.IsNullOrWhiteSpace(ffModName)) return;
            
            var ffModVm = _faceFinderMods.Items.FirstOrDefault(f => f.Name == ffModName);
            if (ffModVm != null)
            {
                ffModVm.IsLinked = _localMods.Items.Any(local => local.Links.Any(link => link.FaceFinderName == ffModName));
            }
        }

        private void UpdateAllFaceFinderTooltips()
        {
            var links = _localMods.Items
                .SelectMany(local => local.Links.Select(link => new { local.LocalName, link.FaceFinderName }))
                .GroupBy(x => x.FaceFinderName)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(i => i.LocalName).Distinct()));
            
            foreach (var ffMod in _faceFinderMods.Items)
            {
                if (ffMod.IsLinked && links.TryGetValue(ffMod.Name, out var linkedTo))
                {
                    ffMod.TooltipText = $"Linked to local mod(s): {linkedTo}";
                }
                else
                {
                    ffMod.TooltipText = "This mod is not currently linked.";
                }
            }
        }
        
        void IDropTarget.DragOver(IDropInfo dropInfo)
        {
            if (dropInfo.Data is VM_FaceFinderModItem && dropInfo.TargetItem is VM_LocalModItem)
            {
                dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
                dropInfo.Effects = System.Windows.DragDropEffects.Copy;
            }
        }

        void IDropTarget.Drop(IDropInfo dropInfo)
        {
            if (dropInfo.Data is VM_FaceFinderModItem sourceMod && dropInfo.TargetItem is VM_LocalModItem targetMod)
            {
                if (targetMod.Links.Any(l => l.FaceFinderName == sourceMod.Name && l.State == LinkState.Manual)) return;

                var autoLinkToReplace = targetMod.Links.FirstOrDefault(l => l.FaceFinderName == sourceMod.Name && l.State == LinkState.Automatic);
                if (autoLinkToReplace != null)
                {
                    targetMod.Links.Remove(autoLinkToReplace);
                }

                if (!_settings.FaceFinderModNameMappings.TryGetValue(targetMod.LocalName, out var mappings))
                {
                    mappings = new List<string>();
                    _settings.FaceFinderModNameMappings[targetMod.LocalName] = mappings;
                }
                mappings.Add(sourceMod.Name);

                targetMod.Links.Add(new LinkInfo(sourceMod.Name, LinkState.Manual, targetMod));

                UpdateFaceFinderLinkStatus(sourceMod.Name);
                UpdateAllFaceFinderTooltips();
            }
        }
    }
}