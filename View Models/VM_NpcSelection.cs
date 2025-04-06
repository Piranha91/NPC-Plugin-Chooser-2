using System.Collections.ObjectModel;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;

namespace NPC_Plugin_Chooser_2.View_Models;

public class VM_NpcSelection
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    public VM_NpcSelection(INpcGetter npcGetter, EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
        Initialize(npcGetter);
    }
    
    public string DisplayName { get; set; }
    public ObservableCollection<ModKey> AppearanceMods { get; set; } = new();
    public ModKey SelectedAppearanceMod { get; set; }

    private void Initialize(INpcGetter npcGetter)
    {
        if (npcGetter.Name != null & npcGetter.Name?.String != null)
        {
            DisplayName = npcGetter.Name?.String ?? string.Empty;
        }

        if (DisplayName == string.Empty)
        {
            DisplayName = npcGetter.EditorID ?? "No Name or EditorID";
        }
        
        var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts(npcGetter);
        if (!contexts.Any())
        {
            return;
        }

        var baseContext = contexts.Last();
        
        if (baseContext == null)
        {
            return;
        }

        SelectedAppearanceMod = baseContext.ModKey;
        
        foreach (var context in contexts)
        {
            var baseNpc = baseContext.Record as Npc;
            var currentNpc = context.Record as Npc;
            if (baseNpc == null || currentNpc == null)
            {
                continue;
            }
            if (ModifiesAppearance(baseNpc, currentNpc))
            {
                AppearanceMods.Add(baseNpc.FormKey.ModKey);
            }
        }
    }

    private bool ModifiesAppearance(Npc baseRecord, Npc currentRecord)
    {
        Npc.TranslationMask appearanceMask = new Npc.TranslationMask(defaultOn: false)
        {
            FaceMorph = true,
            FaceParts = true,
            HairColor = true,
            //HeadParts = true, // HeadParts equality testing is not currently working in Mutagen. Test explicitly
            HeadTexture = true,
            TextureLighting = true,
            TintLayers = true,
            WornArmor = true
        };

        bool headPartsAreEqual = baseRecord.HeadParts.Count() == currentRecord.HeadParts.Count();
        if (headPartsAreEqual)
        {
            foreach (var headPart in currentRecord.HeadParts)
            {
                if (!baseRecord.HeadParts.Contains(headPart))
                {
                    headPartsAreEqual = false;
                    break;
                }
            }
        }
            
            
        if (baseRecord.Equals(currentRecord, appearanceMask) && headPartsAreEqual)
        {
            return true;
        }  
        
        return false;
    }
}