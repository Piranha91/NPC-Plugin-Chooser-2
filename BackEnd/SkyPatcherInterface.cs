using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class SkyPatcherInterface : OptionalUIModule
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly SurrogateNpcRegistry _surrogates;
    private Dictionary<FormKey, NpcContainer> _outputs = new();

    private class NpcContainer
    {
        public FormKey NpcFormKey { get; set; }
        public List<SkyPatcherAction> Actions { get; set; } = new();

        /// <summary>Optional provenance note emitted as a WHOLE ';' line above this
        /// NPC's directive line. It cannot be a trailing comment: SkyPatcher's key
        /// regex captures everything after '=' up to the next ':', so trailing text
        /// would be swallowed into the directive's value.</summary>
        public string? Comment { get; set; }

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

    /// <summary>
    /// What stands between two directives of one line: a COLON.
    /// <para>SkyPatcher cuts a line into <c>key=value</c> parts at each ':' and a value runs to the
    /// next ':' (parsetable.h, <c>KeyTable::parse</c>, read at commit 48d1779 = 8.0.1; the older
    /// per-key regexes were <c>key\s*=([^:]+)</c>, the same rule). A ',' only separates the items
    /// of a LIST value (<c>setFlags</c>, <c>setTemplateFlags</c>, <c>keywordsToAdd</c>).</para>
    /// <para>NPC2 used to join directives with ','. Every value then carried the directives after
    /// it as trailing text. Form identifiers and numbers happened to survive that (their parsers
    /// stop at the first character that does not fit); a list did not: <c>setFlags=female,skin=…,
    /// weight=60</c> gave SkyPatcher the "flags" <c>skin=…</c> and <c>weight=60</c>, logged as
    /// "unrecognized flag" (in-game bench, 2026-11-25).</para>
    /// </summary>
    internal const string ActionSeparator = ":";

    public SkyPatcherInterface(EnvironmentStateProvider environmentStateProvider, SurrogateNpcRegistry surrogates)
    {
        _environmentStateProvider = environmentStateProvider;
        _surrogates = surrogates;
    }

    /// <param name="clearAllGeneratedInis">
    /// Sweep EVERY ini in NPC2's SkyPatcher folder, not just the current output plugin's.
    /// Pass true on the first patching iteration: the run's directory clear deliberately
    /// spares non-asset folders (so it never touches SKSE\), which would otherwise leave a
    /// previous run's "&lt;name&gt;_2.ini"/"_3.ini" behind when this run splits into fewer
    /// plugins — stale directives that still apply in game.
    /// </param>
    public void Reinitialize(string outputRootDir, bool clearAllGeneratedInis = false)
    {
        _outputs.Clear();

        if (clearAllGeneratedInis)
        {
            ClearAllGeneratedInis(outputRootDir);
            return;
        }

        if (!ClearIni(_environmentStateProvider.OutputMod.ModKey, outputRootDir, out string exceptionStr))
        {
            AppendLog(exceptionStr);
        }
    }

    /// <summary>Deletes every ini NPC2 has ever generated in its own SkyPatcher
    /// subfolder. Only NPC2's folder is touched — external configs live elsewhere in
    /// the game's SkyPatcher tree, not in the output mod.</summary>
    private void ClearAllGeneratedInis(string outputRootFolder)
    {
        var outputDir = GetOutputDir(outputRootFolder);
        if (!Directory.Exists(outputDir)) return;

        foreach (var file in Directory.EnumerateFiles(outputDir, "*.ini"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e)
            {
                AppendLog("Failed to clear stale SkyPatcher ini at " + file + Environment.NewLine +
                          ExceptionLogger.GetExceptionStack(e), true, true);
            }
        }
    }

    /// <summary>
    /// Opens the ini line for <paramref name="targetNpcFormKey"/>, a target whose surrogate
    /// <see cref="SurrogateNpcRegistry.CreateSurrogate"/> has just built. Every directive method
    /// below except <see cref="SetOutfitStandalone"/> is a no-op until this has run for its target.
    /// A line that ends up with no directive is not written (a Recast Hybrid NPC that needs no
    /// race, outfit or keyword change).
    /// </summary>
    public void AddNpc(FormKey targetNpcFormKey)
    {
        _outputs.Add(targetNpcFormKey, new(targetNpcFormKey));
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

    // A null outfit writes NO directive. SkyPatcher has no way to say "no outfit": it skips the
    // key when the value is "none" (npc.cpp: `line.outfitDefault != "none"`), and a null FormKey
    // rendered as "Null|" is not a form identifier at all. Written anyway, it made SkyPatcher log
    // "Invalid form identifier" for the line (in-game bench 2026-11-25, a follower whose appearance
    // mod gives her no default outfit).
    public void SetOutfit(FormKey applyTo, FormKey outfitFk)
    {
        if (outfitFk.IsNull || !_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }
        npcContainer.Actions.Add(new SkyPatcherAction("outfitDefault", outfitFk));
    }

    public void SetSleepOutfit(FormKey applyTo, FormKey outfitFk)
    {
        if (outfitFk.IsNull || !_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            return;
        }
        npcContainer.Actions.Add(new SkyPatcherAction("outfitSleep", outfitFk));
    }

    /// <summary>
    /// Every FormKey any queued directive points at (directive VALUES — race=, outfitDefault=,
    /// copyVisualStyle= etc. — not the filterByNPCs targets). Records delivered only through a
    /// directive have no inbound plugin link, so the post-run orphan check needs this to know
    /// they are referenced.
    /// </summary>
    public IEnumerable<FormKey> EnumerateDirectiveFormKeys()
    {
        return _outputs.Values
            .SelectMany(c => c.Actions)
            .Where(a => a.FormKeyRef.HasValue)
            .Select(a => a.FormKeyRef!.Value);
    }

    private const string VisualStyleKey = "copyVisualStyle";
    private const string RaceKey = "race";
    private const string ClearTraitsAction = "removeTemplateFlags=traits";

    private static bool IsVisualStyle(SkyPatcherAction action) =>
        action.FormKeyRef.HasValue && action.Text == VisualStyleKey;

    private static bool IsRace(SkyPatcherAction action) =>
        action.FormKeyRef.HasValue && action.Text == RaceKey;

    /// <summary>
    /// The directives that deliver what an NPC takes from its template through the Traits template
    /// flag: the face (<c>copyVisualStyle</c>), skin, height, weight, race and sex, and the directive
    /// that switches the inheritance itself off. Outfits and keywords are not traits.
    /// </summary>
    private static bool IsTraitAction(SkyPatcherAction action) =>
        action.FormKeyRef.HasValue
            ? action.Text is VisualStyleKey or "skin" or RaceKey
            : action.Text.StartsWith("height=", StringComparison.Ordinal)
              || action.Text.StartsWith("weight=", StringComparison.Ordinal)
              || action.Text is "setFlags=female" or "removeFlags=female" or ClearTraitsAction;

    /// <summary>The NPCs whose line carries an appearance (a <c>copyVisualStyle</c> directive).</summary>
    public IEnumerable<FormKey> EnumerateAppearanceTargets() =>
        _outputs.Values.Where(c => c.Actions.Any(IsVisualStyle)).Select(c => c.NpcFormKey);

    /// <summary>
    /// Gives <paramref name="inheritingNpc"/> the appearance <paramref name="templateNpc"/>'s line
    /// delivers: the template's trait directives (see <see cref="IsTraitAction"/>) with the same
    /// values, so the same surrogate and skin, plus <c>removeTemplateFlags=traits</c>. For an NPC
    /// that takes its face from <paramref name="templateNpc"/> through its Traits template: SkyPatcher
    /// changes only the NPC a line names and follows no template link, so without a line of its own
    /// such an NPC shows the template's face mesh with its unpatched record data (bench finding N11).
    ///
    /// <para>The outfit, sleep outfit and keyword directives of the template's line are NOT repeated:
    /// the inheriting NPC keeps its own inventory. Directives already queued for it that are not
    /// traits (a republished outfit) stay.</para>
    /// </summary>
    /// <returns>False, with nothing changed, when the template has no appearance line or the
    /// inheriting NPC already has an appearance line of its own.</returns>
    public bool MirrorAppearance(FormKey templateNpc, FormKey inheritingNpc)
    {
        if (templateNpc.IsNull || inheritingNpc.IsNull || templateNpc.Equals(inheritingNpc))
        {
            return false;
        }

        if (!_outputs.TryGetValue(templateNpc, out var source) || !source.Actions.Any(IsVisualStyle))
        {
            return false;
        }

        _outputs.TryGetValue(inheritingNpc, out var target);
        if (target != null && target.Actions.Any(IsVisualStyle))
        {
            return false;
        }

        var mirrored = source.Actions.Where(a => IsTraitAction(a) && a.Text != ClearTraitsAction).ToList();
        mirrored.Add(new SkyPatcherAction(ClearTraitsAction));

        if (target == null)
        {
            target = new NpcContainer(inheritingNpc);
            _outputs[inheritingNpc] = target;
        }

        target.Actions.RemoveAll(IsTraitAction);
        target.Actions.AddRange(mirrored);
        return true;
    }

    /// <summary>
    /// Recast Hybrid's share of <see cref="MirrorAppearance"/>: the face travels through Recast, so
    /// only the <c>race</c> directive of <paramref name="templateNpc"/>'s line is repeated for
    /// <paramref name="inheritingNpc"/>, when the template's line has one.
    /// </summary>
    /// <returns>True when a race directive was queued.</returns>
    public bool MirrorRace(FormKey templateNpc, FormKey inheritingNpc)
    {
        if (templateNpc.IsNull || inheritingNpc.IsNull || templateNpc.Equals(inheritingNpc))
        {
            return false;
        }

        if (!_outputs.TryGetValue(templateNpc, out var source))
        {
            return false;
        }

        var race = source.Actions.Where(IsRace).ToList();
        if (race.Count == 0)
        {
            return false;
        }

        if (!_outputs.TryGetValue(inheritingNpc, out var target) || target == null)
        {
            target = new NpcContainer(inheritingNpc);
            _outputs[inheritingNpc] = target;
        }

        target.Actions.RemoveAll(IsRace);
        target.Actions.AddRange(race);
        return true;
    }

    /// <summary>
    /// Adds an <c>outfitDefault</c> directive for an NPC that has NO surrogate entry —
    /// i.e. a record-mode run, where nothing calls <see cref="AddNpc"/>.
    /// Used by <see cref="OutfitDistribution.ForwardedOutfitDistributor"/> to republish a
    /// forwarded wig/antler outfit past a SkyPatcher config that would otherwise
    /// overwrite the NPC record's DefaultOutfit at runtime. Creates the NPC's line on
    /// first use, so the ini can carry outfit-only entries with no appearance directives.
    /// </summary>
    /// <param name="comment">Optional provenance note, written as its own ';' line
    /// above the NPC's directive line (SkyPatcher has no trailing-comment syntax).</param>
    public void SetOutfitStandalone(FormKey applyTo, FormKey outfitFk, string? comment = null)
    {
        if (applyTo.IsNull || outfitFk.IsNull)
        {
            return;
        }

        if (!_outputs.TryGetValue(applyTo, out var npcContainer) || npcContainer == null)
        {
            npcContainer = new NpcContainer(applyTo);
            _outputs[applyTo] = npcContainer;
        }

        npcContainer.Actions.Add(new SkyPatcherAction("outfitDefault", outfitFk));
        if (!string.IsNullOrWhiteSpace(comment)) npcContainer.Comment = comment;
    }

    /// <summary>Whether any NPC line would be written. Unlike
    /// <see cref="HasSkinEntries"/>'s intent this is also true for a record-mode ini
    /// that carries only outfit directives, and false when every line is empty.</summary>
    public bool HasEntries => _outputs.Values.Any(c => c.Actions.Count > 0);
    
    public void ApplyKeywords(FormKey surrogateFk, IEnumerable<string> keywords)
    {
        if (!_surrogates.TryGetOriginalFormKey(surrogateFk, out var applyTo))
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
                if (entry.Actions.Count == 0)
                {
                    continue;
                }

                if (entry.Comment != null)
                {
                    // Whole-line comment: SkyPatcher skips lines starting with ';' but has
                    // no trailing-comment syntax (its key regex would eat the text).
                    sb.Append("; ").Append(entry.Comment.Replace('\r', ' ').Replace('\n', ' '))
                      .Append(Environment.NewLine);
                }

                string npc = FormatFormKeyForSkyPatcher(entry.NpcFormKey);
                sb.Append($"filterByNPCs={npc}:");
                sb.Append(string.Join(ActionSeparator, entry.Actions.Select(a => RenderAction(a, formKeyRemap)).Order()));
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

    private static string GetOutputDir(string outputRootFolder) =>
        Path.Combine(outputRootFolder, "SKSE", "Plugins", "SkyPatcher", "npc", "NPC Plugin Chooser");

    private (string outputDir, string outputPath) GetOutputPath(ModKey outputPlugin, string outputRootFolder)
    {
        string outputName = $"{outputPlugin.Name}.ini";
        string outputDir = GetOutputDir(outputRootFolder);
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