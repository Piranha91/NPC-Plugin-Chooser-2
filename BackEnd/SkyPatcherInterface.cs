using System.IO;
using System.Text;
using System.Windows.Forms;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class SkyPatcherInterface : OptionalUIModule
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private Dictionary<FormKey, NpcContainer> _outputs = new();
    private Dictionary<FormKey, FormKey> _keyOriginalValSurrogate = new(); // key: orignal NPC, Value: SkyPatcher NPC
    private Dictionary<FormKey, FormKey> _keySurrogateValOrriginal = new();

    private class NpcContainer
    {
        public FormKey NpcFormKey { get; set; }
        public List<string> ActionStrings { get; set; } = new();

        public NpcContainer(FormKey npcFormKey)
        {
            NpcFormKey = npcFormKey;
        }
    }

    public SkyPatcherInterface(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
    }

    public void Reinitialize(string outputRootDir)
    {
        _outputs.Clear();
        _keyOriginalValSurrogate.Clear();
        _keySurrogateValOrriginal.Clear();
        if (!ClearIni(_environmentStateProvider.OutputMod.ModKey, outputRootDir, out string exceptionStr))
        {
            AppendLog(exceptionStr);
        }
    }

    public Npc CreateSkyPatcherNpc(INpcGetter npcGetter)
    {
        var npcCopy = _environmentStateProvider.OutputMod.Npcs.AddNew();
        npcCopy.DeepCopyIn(npcGetter, out _);
        var edid = npcGetter.EditorID ?? "NoEditorID";
        npcCopy.EditorID = edid + "_Template";
        
        _keyOriginalValSurrogate.Add(npcGetter.FormKey, npcCopy.FormKey);
        _keySurrogateValOrriginal.Add(npcCopy.FormKey, npcGetter.FormKey);
        _outputs.Add(npcGetter.FormKey, new(npcGetter.FormKey));
        return npcCopy;
    }

    public bool TryGetSurrogateFormKey(FormKey originalNpcFormKey, out FormKey surrogateNpcFormKey)
    {
        return _keyOriginalValSurrogate.TryGetValue(originalNpcFormKey, out surrogateNpcFormKey);
    }

    public bool TryGetOriginalFormKey(FormKey surrogateNpcFormKey, out FormKey originalNpcFormKey)
    {
        return _keySurrogateValOrriginal.TryGetValue(surrogateNpcFormKey, out originalNpcFormKey);
    }

    public void ApplyViaSkyPatcher(FormKey applyTo, INpcGetter appearanceTemplate)
    {
        ApplyFace(applyTo, appearanceTemplate.FormKey);
        if (!appearanceTemplate.WornArmor.IsNull)
        {
            ApplySkin(applyTo, appearanceTemplate.WornArmor.FormKey);
        }
        ApplyHeight(applyTo, appearanceTemplate.Height);
        
    }

    public void ApplyFace(FormKey applyTo, FormKey faceTemplate) // This doesn't work if the face texture isn't baked into the facegen nif. Not useful for SynthEBD.
    {
        if (applyTo.IsNull || faceTemplate.IsNull || !_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }

        string template = FormatFormKeyForSkyPatcher(faceTemplate);
        
        npcContainer.ActionStrings.Add($"copyVisualStyle={template}");
    }
    
    public void ApplySkin(FormKey applyTo, FormKey skinFk)
    {
        if (applyTo.IsNull || skinFk.IsNull || !_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }
        
        string skin = FormatFormKeyForSkyPatcher(skinFk);
        
        npcContainer.ActionStrings.Add($"skin={skin}");
    }
    
    public void ApplyRace(FormKey applyTo, FormKey raceFk)
    {
        if (applyTo.IsNull || raceFk.IsNull || !_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }
        
        string skin = FormatFormKeyForSkyPatcher(raceFk);
        
        npcContainer.ActionStrings.Add($"race={skin}");
    }
    
    public void ApplyHeight(FormKey applyTo, float heightFlt)
    {
        string height = heightFlt.ToString();
        
        if (!_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }
        
        npcContainer.ActionStrings.Add($"height={height}");
    }

    public void ToggleGender(FormKey applyTo, Gender gender)
    {
        if (!_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }
        
        if (gender == Gender.Female)
        {
            npcContainer.ActionStrings.Add("setFlags=female");
        }
        else
        {
            npcContainer.ActionStrings.Add("removeFlags=female");
        }
    }

    public void ToggleTemplateTraitsStatus(FormKey applyTo, bool useTraits)
    {
        if (!_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }
        
        if (useTraits)
        {
            npcContainer.ActionStrings.Add("setTemplateFlags=traits");
        }
        else
        {
            npcContainer.ActionStrings.Add("removeTemplateFlags=traits");
        }
    }

    public void SetOutfit(FormKey applyTo, FormKey outfitFk)
    {
        if (!_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }
        string outfitStr = FormatFormKeyForSkyPatcher(outfitFk);
        npcContainer.ActionStrings.Add($"outfitDefault={outfitStr}");
    }

    public bool WriteIni(string outputRootFolder)
    {
        var outputPlugin = _environmentStateProvider.OutputMod.ModKey;
        (var outputDir, var outputPath) = GetOutputPath(outputPlugin, outputRootFolder);
        
        try
        {
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }
            
            StringBuilder sb = new();

            foreach (var entry in _outputs.Values)
            {
                string npc = FormatFormKeyForSkyPatcher(entry.NpcFormKey);
                sb.Append($"filterByNPCs={npc}:");
                sb.Append(string.Join(",", entry.ActionStrings.Order()));
                sb.Append(Environment.NewLine);
            }

            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
            AppendLog("Saved SkyPatcher Ini File to " + outputPath, false, true);
            return true;
        }
        catch (Exception ex)
        {
            // Handle potential file writing errors (permissions, disk full, etc.).
            var exceptionStr = "Failed to write SkyPatcher ini to: " + outputPath + Environment.NewLine +
                           ExceptionLogger.GetExceptionStack(ex);
            AppendLog(exceptionStr, true, true);
            return false;
        }
    }

    private (string outputDir, string outputPath) GetOutputPath(ModKey outputPlugin, string outputRootFolder)
    {
        string outputName = $"{outputPlugin.Name}.ini";
        string outputDir = Path.Combine(outputRootFolder, "SKSE", "Plugins", "SkyPatcher", "npc", "NPC Plugin Chooser");
        string outputPath = Path.Combine(outputDir, outputName);
        return (outputDir, outputPath);
    }

    private bool ClearIni(ModKey? outputPlugin, string outputRootFolder, out string exceptionStr)
    {
        exceptionStr = string.Empty;
        if (outputPlugin == null || outputPlugin.Value.IsNull)
        {
            return true;
        }
        (var outputDir, var outputPath) = GetOutputPath(outputPlugin.Value, outputRootFolder);
        if (File.Exists(outputPath))
        {
            try
            {
                File.Delete(outputPath);
                return true;
            }
            catch (Exception e)
            {
                exceptionStr = "Failed to clear SkyPatcher ini at" + outputPath + Environment.NewLine + ExceptionLogger.GetExceptionStack(e);
                return false;
            }
        }
        else
        {
            return true;
        }
    }

    public bool HasSkinEntries()
    {
        return _outputs.Any();
    }
    
    public static string FormatFormKeyForSkyPatcher(FormKey FK)
    {
        return FK.ModKey.ToString() + "|" + FK.IDString().TrimStart(new Char[] { '0' });
    }
}