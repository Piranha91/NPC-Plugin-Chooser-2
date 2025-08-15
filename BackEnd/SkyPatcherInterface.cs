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
    private List<string> outputLines = new();
    private Dictionary<FormKey, FormKey> _keyOriginalValSurrogate = new(); // key: orignal NPC, Value: SkyPatcher NPC
    private Dictionary<FormKey, FormKey> _keySurrogateValOrriginal = new();

    public SkyPatcherInterface(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
    }

    public void Reinitialize(string outputRootDir)
    {
        outputLines = new List<string>();
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

    public void ApplyViaSkyPatcher(INpcGetter originalNpc, INpcGetter surrogateNpc)
    {

        ApplyFace(originalNpc.FormKey, surrogateNpc.FormKey);
        if (!surrogateNpc.WornArmor.IsNull)
        {
            ApplySkin(originalNpc.FormKey, surrogateNpc.WornArmor.FormKey);
        }
        ApplyHeight(originalNpc.FormKey, surrogateNpc.Height);
    }

    public void ApplyFace(FormKey applyTo, FormKey faceTemplate) // This doesn't work if the face texture isn't baked into the facegen nif. Not useful for SynthEBD.
    {
        if (applyTo.IsNull || faceTemplate.IsNull)
        {
            return;
        }
        
        string npc = FormatFormKeyForSkyPatcher(applyTo); 
        string template = FormatFormKeyForSkyPatcher(faceTemplate);
        
        outputLines.Add($"filterByNPCs={npc}:copyVisualStyle={template}");
    }
    
    public void ApplySkin(FormKey applyTo, FormKey skinFk)
    {
        if (applyTo.IsNull || skinFk.IsNull)
        {
            return;
        }
        
        string npc = FormatFormKeyForSkyPatcher(applyTo); 
        string skin = FormatFormKeyForSkyPatcher(skinFk);
        
        outputLines.Add($"filterByNPCs={npc}:skin={skin}");
    }
    
    public void ApplyHeight(FormKey applyTo, float heightFlt)
    {
        string npc = FormatFormKeyForSkyPatcher(applyTo); 
        string height = heightFlt.ToString();
        
        outputLines.Add($"filterByNPCs={npc}:height={height}");
    }

    public void ToggleGender(FormKey applyTo, Gender gender)
    {
        string npc = FormatFormKeyForSkyPatcher(applyTo);
        if (gender == Gender.Female)
        {
            outputLines.Add($"filterByNPCs={npc}:setFlags=female");
        }
        else
        {
            outputLines.Add($"filterByNPCs={npc}:removeFlags=female");
        }
    }

    public void ToggleTemplateTraitsStatus(FormKey applyTo, bool useTraits)
    {
        string npc = FormatFormKeyForSkyPatcher(applyTo);
        if (useTraits)
        {
            outputLines.Add($"filterByNPCs={npc}:setTemplateFlags=traits");
        }
        else
        {
            outputLines.Add($"filterByNPCs={npc}:removeTemplateFlags=traits");
        }
    }

    public void SetOutfit(FormKey applyTo, IOutfitGetter outfit)
    {
        string npc = FormatFormKeyForSkyPatcher(applyTo);
        string outfitStr = FormatFormKeyForSkyPatcher(outfit.FormKey);
        outputLines.Add($"filterByNPCs={npc}:outfitDefault={outfitStr}");
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

            File.WriteAllLines(outputPath, outputLines, new UTF8Encoding(false));
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
        return outputLines.Any();
    }
    
    public static string FormatFormKeyForSkyPatcher(FormKey FK)
    {
        return FK.ModKey.ToString() + "|" + FK.IDString().TrimStart(new Char[] { '0' });
    }
}