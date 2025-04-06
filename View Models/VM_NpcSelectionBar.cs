using System.Collections.ObjectModel;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;

namespace NPC_Plugin_Chooser_2.View_Models;

public class VM_NpcSelectionBar
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Auxilliary _auxilliary;
    
    public VM_NpcSelectionBar(EnvironmentStateProvider environmentStateProvider, Auxilliary auxilliary)
    {
        _environmentStateProvider = environmentStateProvider;
        _auxilliary = auxilliary;
        
        Initialize();
    }
    
    public ObservableCollection<VM_NpcSelection> Npcs { get; set; } = new();
    public INpcGetter SelectedNpcGetter { get; set; }

    public void Initialize()
    {
        Npcs.Clear();
        foreach (var npc in _environmentStateProvider.LoadOrder.PriorityOrder
                     .WinningOverrides<INpcGetter>()
                     .OrderBy(x => _auxilliary.FormKeyStringToFormIDString(x.FormKey.ToString())).ToArray())
        {
            var npcSelector = new VM_NpcSelection(npc, _environmentStateProvider);

            if (npcSelector.AppearanceMods.Any())
            {
                Npcs.Add(npcSelector);
            }
        }
    }
}