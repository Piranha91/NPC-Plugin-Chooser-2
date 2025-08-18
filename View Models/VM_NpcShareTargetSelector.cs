// [VM_NpcShareTargetSelector.cs] - New File in View_Models/
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Windows;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models
{
    public enum ShareReturn
    {
        ShareAndSelect,
        Share,
        Cancel
    }
    
    public class VM_NpcShareTargetSelector : ReactiveObject
    {
        private readonly List<VM_NpcsMenuSelection> _allNpcs;

        [Reactive] public string SearchText { get; set; } = string.Empty;
        [Reactive] public NpcSearchType SearchType { get; set; } = NpcSearchType.Name;
        public ObservableCollection<VM_NpcsMenuSelection> FilteredNpcs { get; } = new();
        [Reactive] public VM_NpcsMenuSelection? SelectedNpc { get; set; }
        
        public Array AvailableSearchTypes => Enum.GetValues(typeof(NpcSearchType))
            .Cast<NpcSearchType>()
            .Where(e => e == NpcSearchType.Name || e == NpcSearchType.EditorID || e == NpcSearchType.FormKey)
            .ToArray();

        public ReactiveCommand<Window, Unit> ShareAndSelectCommand { get; }
        public ReactiveCommand<Window, Unit> ShareCommand { get; }
        public ReactiveCommand<Window, Unit> CancelCommand { get; }

        public ShareReturn ReturnStatus { get; set; } = ShareReturn.Cancel;

        public VM_NpcShareTargetSelector(List<VM_NpcsMenuSelection> allNpcs)
        {
            _allNpcs = allNpcs;

            this.WhenAnyValue(x => x.SearchText, x => x.SearchType)
                .Throttle(TimeSpan.FromMilliseconds(200))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ApplyFilter());

            var canOk = this.WhenAnyValue(x => x.SelectedNpc)
                .Select(npc => npc != null);
            
            ShareAndSelectCommand = ReactiveCommand.Create<Window>(window =>
            {
                ReturnStatus = ShareReturn.ShareAndSelect;
                window.Close();
            }, canOk);
            
            ShareCommand = ReactiveCommand.Create<Window>(window =>
            {
                ReturnStatus = ShareReturn.Share;
                window.Close();
            }, canOk);
            
            CancelCommand = ReactiveCommand.Create<Window>(window =>
            {
                 ReturnStatus = ShareReturn.Cancel;
                 window.Close();
            });
            
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            IEnumerable<VM_NpcsMenuSelection> results = _allNpcs;

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                results = SearchType switch
                {
                    NpcSearchType.Name => results.Where(n => n.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)),
                    NpcSearchType.EditorID => results.Where(n => n.NpcEditorId.Contains(SearchText, StringComparison.OrdinalIgnoreCase)),
                    NpcSearchType.FormKey => results.Where(n => n.NpcFormKeyString.Contains(SearchText, StringComparison.OrdinalIgnoreCase)),
                    _ => results
                };
            }

            FilteredNpcs.Clear();
            foreach (var item in results.OrderBy(n => n.DisplayName))
            {
                FilteredNpcs.Add(item);
            }
        }
    }
}