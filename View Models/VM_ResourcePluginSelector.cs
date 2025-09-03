using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_ResourcePluginSelector : ReactiveObject
    {
        // --- Event for closing ---
        public event Action RequestClose = delegate { };

        // --- Properties ---
        public ObservableCollection<VM_SelectableMod> SelectablePlugins { get; }
        public bool HasChanged { get; private set; } = false;

        // --- Commands ---
        public ReactiveCommand<Unit, Unit> OKCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }

        // --- Private Fields ---
        private readonly HashSet<ModKey> _initialSelection;

        // --- Constructor ---
        public VM_ResourcePluginSelector(IEnumerable<ModKey> allModKeys, HashSet<ModKey> currentlySelectedResourceModKeys)
        {
            _initialSelection = new HashSet<ModKey>(currentlySelectedResourceModKeys);

            SelectablePlugins = new ObservableCollection<VM_SelectableMod>(
                allModKeys.Select(modKey => new VM_SelectableMod(modKey, _initialSelection.Contains(modKey)))
                          .OrderBy(vm => vm.DisplayText)
            );

            OKCommand = ReactiveCommand.Create(ExecuteOk);
            CancelCommand = ReactiveCommand.Create(ExecuteCancel);
        }

        public HashSet<ModKey> GetSelectedModKeys()
        {
            return new HashSet<ModKey>(
                SelectablePlugins.Where(vm => vm.IsSelected)
                                 .Select(vm => vm.ModKey)
            );
        }

        // --- Command Implementations ---

        private void ExecuteOk()
        {
            var finalSelection = GetSelectedModKeys();
            if (!_initialSelection.SetEquals(finalSelection))
            {
                HasChanged = true;
            }
            RequestClose?.Invoke();
        }

        private void ExecuteCancel()
        {
            HasChanged = false; // Ensure no changes are processed
            RequestClose?.Invoke();
        }
    }
}

