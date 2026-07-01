using System.Globalization;
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
        public List<SkyPatcherAction> Actions { get; set; } = new();

        public NpcContainer(FormKey npcFormKey)
        {
            NpcFormKey = npcFormKey;
        }
    }

    /// <summary>
    /// One SkyPatcher directive for an NPC line. Stored either as a fully literal
    /// "key=value" string (<see cref="FormKeyRef"/> null), or as a "key" plus a FormKey
    /// value that is formatted at write time. Keeping the FormKey structural (instead of
    /// pre-formatting it into the string) lets <see cref="WriteIni"/> remap output-plugin
    /// FormKeys after an auto-split relocates the surrogate template records from
    /// "&lt;name&gt;.esp" into "&lt;name&gt;_2.esp" etc.
    /// </summary>
    private readonly struct SkyPatcherAction
    {
        public string Text { get; }
        public FormKey? FormKeyRef { get; }

        public SkyPatcherAction(string text, FormKey? formKeyRef = null)
        {
            Text = text;
            FormKeyRef = formKeyRef;
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

    /// <summary>
    /// Creates the SkyPatcher surrogate "_Template" NPC. Its CONTENT is a deep copy of
    /// <paramref name="appearanceDonor"/> (the chosen appearance), but it is registered under
    /// <paramref name="targetNpcFormKey"/> (the NPC being patched in the user's load order). The two
    /// are deliberately decoupled so that:
    ///   - the output masters only to the donor's appearance plugins, never the target's data mods, and
    ///   - the .ini filters by the TARGET NPC (filterByNPCs=target) even when the donor is a DIFFERENT
    ///     NPC (a cross-NPC appearance swap) — something only SkyPatcher mode can do.
    /// AssetHandler likewise looks the surrogate up by the target FormKey via TryGetSurrogateFormKey.
    /// </summary>
    public Npc CreateSkyPatcherNpc(FormKey targetNpcFormKey, INpcGetter appearanceDonor)
    {
        var npcCopy = _environmentStateProvider.OutputMod.Npcs.AddNew();
        npcCopy.DeepCopyIn(appearanceDonor, out _);
        var edid = appearanceDonor.EditorID ?? "NoEditorID";
        npcCopy.EditorID = edid + "_Template";

        _keyOriginalValSurrogate.Add(targetNpcFormKey, npcCopy.FormKey);
        _keySurrogateValOrriginal.Add(npcCopy.FormKey, targetNpcFormKey);
        _outputs.Add(targetNpcFormKey, new(targetNpcFormKey));
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

    public void ApplyCoreAppearance(FormKey applyTo, INpcGetter appearanceTemplate)
    {
        ApplyFace(applyTo, appearanceTemplate.FormKey);
        if (!appearanceTemplate.WornArmor.IsNull)
        {
            ApplySkin(applyTo, appearanceTemplate.WornArmor.FormKey);
        }
        ApplyHeight(applyTo, appearanceTemplate.Height);
        ApplyWeight(applyTo, appearanceTemplate.Weight);
    }

    public void ApplyFace(FormKey applyTo, FormKey faceTemplate) // This doesn't work if the face texture isn't baked into the facegen nif. Not useful for SynthEBD.
    {
        if (applyTo.IsNull || faceTemplate.IsNull || !_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }

        npcContainer.Actions.Add(new SkyPatcherAction("copyVisualStyle", faceTemplate));
    }
    
    public void ApplySkin(FormKey applyTo, FormKey skinFk)
    {
        if (applyTo.IsNull || skinFk.IsNull || !_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }
        
        npcContainer.Actions.Add(new SkyPatcherAction("skin", skinFk));
    }
    
    public void ApplyRace(FormKey applyTo, FormKey raceFk)
    {
        if (applyTo.IsNull || raceFk.IsNull || !_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }
        
        npcContainer.Actions.Add(new SkyPatcherAction("race", raceFk));
    }
    
    public void ApplyHeight(FormKey applyTo, float heightFlt)
    {
        // Always emit with '.' as the decimal separator. SkyPatcher parses the .ini
        // culture-invariantly, so a culture-sensitive ToString() on a machine that uses
        // ',' as the decimal mark (e.g. de-DE) would write "height=0,975" and corrupt the line.
        string height = heightFlt.ToString(CultureInfo.InvariantCulture);

        if (!_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }

        npcContainer.Actions.Add(new SkyPatcherAction($"height={height}"));
    }

    public void ApplyWeight(FormKey applyTo, float weightFlt)
    {
        string weight = weightFlt.ToString(CultureInfo.InvariantCulture);

        if (!_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }

        npcContainer.Actions.Add(new SkyPatcherAction($"weight={weight}"));
    }

    public void ToggleGender(FormKey applyTo, Gender gender)
    {
        if (!_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }
        
        if (gender == Gender.Female)
        {
            npcContainer.Actions.Add(new SkyPatcherAction("setFlags=female"));
        }
        else
        {
            npcContainer.Actions.Add(new SkyPatcherAction("removeFlags=female"));
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
            npcContainer.Actions.Add(new SkyPatcherAction("setTemplateFlags=traits"));
        }
        else
        {
            npcContainer.Actions.Add(new SkyPatcherAction("removeTemplateFlags=traits"));
        }
    }

    public void SetOutfit(FormKey applyTo, FormKey outfitFk)
    {
        if (!_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }
        npcContainer.Actions.Add(new SkyPatcherAction("outfitDefault", outfitFk));
    }
    
    public void ApplyKeywords(FormKey surrogateFk, IEnumerable<string> keywords)
    {
        if (!TryGetOriginalFormKey(surrogateFk, out var applyTo))
        {
            return;
        }
        
        if (!_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }

        // Omit the directive entirely when there are no keywords. An empty "keywordsToAdd="
        // contributes nothing and an empty value can throw off the SkyPatcher parser.
        var kwStr = string.Join(",", keywords.Where(k => !string.IsNullOrWhiteSpace(k)));
        if (kwStr.Length == 0)
        {
            return;
        }

        npcContainer.Actions.Add(new SkyPatcherAction($"keywordsToAdd={kwStr}"));
    }

    /// <param name="formKeyRemap">
    /// Optional map of original output-plugin FormKeys to their post-split locations. When the
    /// output plugin was auto-split (see <see cref="Patcher"/>), surrogate template records move
    /// from "&lt;name&gt;.esp" into "&lt;name&gt;_2.esp" etc.; this map rewrites the affected FormKeys
    /// so the .ini keeps pointing at the record's true file. Non-output FormKeys (donor skins,
    /// races, outfits) are absent from the map and therefore left untouched.
    /// </param>
    public bool WriteIni(string outputRootFolder, IReadOnlyDictionary<FormKey, FormKey>? formKeyRemap = null)
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
                sb.Append(string.Join(",", entry.Actions.Select(a => RenderAction(a, formKeyRemap)).Order()));
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

    /// <summary>
    /// Renders one directive to its final ".ini" text. Literal directives pass through
    /// unchanged; FormKey-valued directives are formatted here, applying <paramref name="formKeyRemap"/>
    /// first so a split-relocated output-plugin FormKey resolves to its true file.
    /// </summary>
    private static string RenderAction(SkyPatcherAction action, IReadOnlyDictionary<FormKey, FormKey>? formKeyRemap)
    {
        if (action.FormKeyRef is FormKey fk)
        {
            if (formKeyRemap != null && formKeyRemap.TryGetValue(fk, out var mapped))
            {
                fk = mapped;
            }
            return action.Text + "=" + FormatFormKeyForSkyPatcher(fk);
        }
        return action.Text;
    }
}