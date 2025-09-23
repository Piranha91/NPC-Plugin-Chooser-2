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

namespace NPC_Plugin_Chooser_2.View_Models
{
    public enum LinkState { None, Automatic, Manual }

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
        public ObservableCollection<VM_FaceFinderModItem> FaceFinderMods { get; } = new();
        public ObservableCollection<VM_LocalModItem> LocalMods { get; } = new();

        public ReactiveCommand<LinkInfo, Unit> UnlinkCommand { get; }

        public VM_ModFaceFinderLinker(Settings settings, FaceFinderClient faceFinderClient, VM_Mods vmMods)
        {
            _settings = settings;
            _faceFinderClient = faceFinderClient;
            _vmMods = vmMods;

            UnlinkCommand = ReactiveCommand.Create<LinkInfo>(Unlink);
            
            _ = LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            IsLoading = true;
            
            var ffModsTask = _faceFinderClient.GetAllModNamesAsync(_settings.FaceFinderApiKey);
            var localMods = _vmMods.AllModSettings.Select(s => s.DisplayName).OrderBy(s => s).ToList();
            var ffMods = await ffModsTask;
            
            var manualMappings = new Dictionary<string, List<string>>(_settings.FaceFinderModNameMappings);
            
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
                
                // Add auto-link if the name matches and it's not already manually linked to itself
                if (ffMods.Contains(mod) && !localModVm.Links.Any(l => l.FaceFinderName == mod))
                {
                    localModVm.Links.Add(new LinkInfo(mod, LinkState.Automatic, localModVm));
                }
                LocalMods.Add(localModVm);
            }
            
            var allLinkedFfMods = new HashSet<string>(LocalMods.SelectMany(m => m.Links).Select(l => l.FaceFinderName));
            foreach (var mod in ffMods)
            {
                FaceFinderMods.Add(new VM_FaceFinderModItem(mod) { IsLinked = allLinkedFfMods.Contains(mod) });
            }
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

            bool ffModExists = FaceFinderMods.Any(f => f.Name == localMod.LocalName);
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
            
            var ffModVm = FaceFinderMods.FirstOrDefault(f => f.Name == ffModName);
            if (ffModVm != null)
            {
                ffModVm.IsLinked = LocalMods.Any(local => local.Links.Any(link => link.FaceFinderName == ffModName));
            }
        }

        private void UpdateAllFaceFinderTooltips()
        {
            var links = LocalMods
                .SelectMany(local => local.Links.Select(link => new { local.LocalName, link.FaceFinderName }))
                .GroupBy(x => x.FaceFinderName)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(i => i.LocalName).Distinct()));
            
            foreach (var ffMod in FaceFinderMods)
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