using System.IO;
using System.Text;
using Microsoft.Win32;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Views;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class EasyNpcTranslator
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly NpcConsistencyProvider _consistencyProvider;
    private readonly Lazy<VM_NpcSelectionBar> _lazyNpcSelectionBar;
    private readonly Lazy<VM_Mods> _lazyModListVM;
    private readonly Settings _settings;
    private readonly Lazy<VM_Settings> _lazySettingsVM;

    // Table statuses, in the order rows are listed: problems first, unchanged last.
    private const string StatusUnreadable = "Unreadable line";
    private const string StatusSkipped = "Skipped";
    private const string StatusWarning = "Imported with warning";
    private const string StatusFirstMatch = "First match used";
    private static readonly string[] StatusOrder =
        [StatusUnreadable, StatusSkipped, StatusWarning, StatusFirstMatch];

    /// <summary>One NPC2 selection converted for export: <see cref="Entry"/> on success, else <see cref="Error"/>.</summary>
    private sealed record ExportResult(FormKey Npc, string ModName, EasyNpcProfileEntry? Entry, string Error);

    public EasyNpcTranslator(EnvironmentStateProvider environmentStateProvider,
        NpcConsistencyProvider consistencyProvider,
        Lazy<VM_NpcSelectionBar> lazyNpcSelectionBar, Lazy<VM_Mods> lazyModListVM,
        Settings settings, Lazy<VM_Settings> lazySettingsVM)
    {
        _environmentStateProvider = environmentStateProvider;
        _consistencyProvider = consistencyProvider;
        _lazyNpcSelectionBar = lazyNpcSelectionBar;
        _lazyModListVM = lazyModListVM;
        _settings = settings;
        _lazySettingsVM = lazySettingsVM;
    }

    public void ImportEasyNpc() => Import(autosave: false);
    public void ImportEasyNpcAutosave() => Import(autosave: true);

    private void Import(bool autosave)
    {
        var dialog = new OpenFileDialog
        {
            Filter = autosave ? "EasyNPC Autosave (*.log)|*.log|All files (*.*)|*.*"
                : "EasyNPC Profile (*.txt)|*.txt|All files (*.*)|*.*",
            Title = autosave ? "Read EasyNPC / NEXT Autosave" : "Select EasyNPC / NEXT Profile",
            CheckFileExists = true
        };
        if (autosave)
        {
            dialog.FileName = "Profile.log";
            dialog.InitialDirectory = Auxilliary.GetSafeInitialDirectory(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyNPC"));
        }
        if (dialog.ShowDialog() != true) return;

        var issues = new List<EasyNpcIssue>();
        List<EasyNpcProfileEntry> entries;
        try
        {
            entries = EasyNpcProfile.Parse(EasyNpcProfile.ReadLines(dialog.FileName), autosave, issues);
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"Could not read '{dialog.FileName}':\n{ex.Message}", "File Read Error");
            return;
        }
        if (entries.Count == 0 && issues.Count == 0)
        {
            ScrollableMessageBox.Show("No NPC selections were found in this file. Current choices were kept.", "Import Empty");
            return;
        }

        var mods = SnapshotMods();
        var names = NpcNames();
        var rows = issues.Select(issue => IssueRow(issue, names)).ToList();
        var pending = new List<(EasyNpcProfileEntry Entry, ModSetting Mod, bool Ambiguous)>();
        foreach (var entry in entries)
        {
            var match = EasyNpcTransfer.FindImportCandidates(entry, mods);
            var current = _consistencyProvider.GetSelectedMod(entry.NpcKey);
            var currentText = DescribeSelection(current.ModName, current.SourceNpcFormKey, entry.NpcKey, names);
            if (match.Candidates.Count == 0)
            {
                rows.Add(new(StatusSkipped, NpcName(entry.NpcKey, names), entry.NpcKey.ToString(), currentText, "",
                    entry.FacePlugin.FileName, FaceGenName(entry), entry.DefaultPlugin.FileName, match.Error));
                continue;
            }

            var mod = match.Candidates[0];
            ModKey? currentDefault = _settings.EasyNpcDefaultPlugins.TryGetValue(entry.NpcKey, out var d) ? d : (ModKey?)null;
            var notes = new List<string>();
            if (match.Warning.Length > 0) notes.Add(match.Warning);
            if (match.IsAmbiguous)
                notes.Add("Also provided by: " + string.Join(", ", match.Candidates.Skip(1).Select(m => m.DisplayName)) + ".");
            bool unchanged = notes.Count == 0 &&
                             EasyNpcTransfer.IsUnchanged(match.Entry, mod, current, currentDefault);
            string status = match.IsAmbiguous ? StatusFirstMatch
                : match.Warning.Length > 0 ? StatusWarning
                : unchanged ? "Unchanged" : "Will change";
            rows.Add(new(status, NpcName(entry.NpcKey, names), entry.NpcKey.ToString(), currentText, mod.DisplayName,
                match.Entry.FacePlugin.FileName, FaceGenName(match.Entry), match.Entry.DefaultPlugin.FileName,
                string.Join(" ", notes))
            {
                IsApplicable = true, IsUnchanged = unchanged, IsAmbiguous = match.IsAmbiguous
            });
            pending.Add((match.Entry, mod, match.IsAmbiguous));
        }

        var notesText = new List<string> { "NPCs absent from this file, and skipped rows, keep their current choices." };
        if (autosave)
            notesText.Insert(0, "This reads EasyNPC's automatic history without changing it. NEXT exclusions are not part of this history.");
        var options = new EasyNpcTransferWindowOptions(
            autosave ? "Import from EasyNPC Autosave" : "Import from EasyNPC Profile",
            "Import", "will change", "Current NPC2 Mod", "Imported Mod", notesText);
        if (!EasyNpcTransferWindow.Confirm(options, SortRows(rows), out var useFirstMatch)) return;

        int applied = 0;
        foreach (var (entry, mod, ambiguous) in pending)
        {
            if (ambiguous && !useFirstMatch) continue;
            _settings.EasyNpcDefaultPlugins[entry.NpcKey] = entry.DefaultPlugin;
            // Snapshots share this dictionary with the VM; exact plugin choices survive even
            // when the selected mod is unchanged. Persist them before the throttled settings save.
            EasyNpcTransfer.ApplyPluginChoice(entry, mod);
            _consistencyProvider.SetSelectedMod(entry.NpcKey, mod.DisplayName, entry.NpcKey, suppressSaveRequest: true);
            applied++;
        }
        _lazyModListVM.Value.SaveModSettingsToModel();
        _lazySettingsVM.Value.RequestThrottledSave();
        _lazyNpcSelectionBar.Value.ApplyFilter(false);
        _lazyNpcSelectionBar.Value.RefreshCurrentNpcAppearanceSources();
        ScrollableMessageBox.Show($"Imported {applied} NPC selections.", "Import Complete");
    }

    public void ExportEasyNpc()
    {
        if (!HasSelections()) return;
        var results = BuildExportResults(null, addMissing: true);
        var entries = results.Where(r => r.Entry != null).ToList();
        if (results.Any(r => r.Entry == null))
        {
            var names = NpcNames();
            var rows = results.Select(r => r.Entry == null
                ? new EasyNpcTransferRow(StatusSkipped, NpcName(r.Npc, names), r.Npc.ToString(), "", r.ModName,
                    "", "", "", r.Error)
                : EntryRow("Will export", r, names, "") with { IsApplicable = true }).ToList();
            var options = new EasyNpcTransferWindowOptions("Export to EasyNPC Profile", "Save", "will be exported",
                null, "NPC2 Mod", ["Skipped NPCs are left out of the exported profile."]);
            if (!EasyNpcTransferWindow.Confirm(options, SortRows(rows), out _)) return;
        }
        if (entries.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Filter = "EasyNPC Profile (*.txt)|*.txt",
            DefaultExt = ".txt",
            Title = "Save EasyNPC / NEXT Profile As...",
            FileName = "EasyNPC_Profile_Export.txt"
        };
        if (dialog.ShowDialog() != true || !CanWriteProfile(dialog.FileName)) return;
        WriteProfile(dialog.FileName, entries.Select(r => r.Entry!.Serialize()),
            $"Exported {entries.Count} NPC selections.");
    }

    public Task UpdateEasyNpcProfile(bool addMissingNPCs)
    {
        UpdateProfile(addMissingNPCs);
        return Task.CompletedTask;
    }

    private void UpdateProfile(bool addMissing)
    {
        if (!HasSelections()) return;
        var open = new OpenFileDialog
        {
            Filter = "EasyNPC Profile (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Select EasyNPC / NEXT Profile to Update",
            CheckFileExists = true
        };
        if (open.ShowDialog() != true || !CanWriteProfile(open.FileName)) return;
        List<string> original;
        List<EasyNpcProfileEntry> existing;
        try
        {
            original = EasyNpcProfile.ReadLines(open.FileName);
            // Line-level problems are reported by UpdateLines, which knows what it left unchanged.
            existing = EasyNpcProfile.Parse(original, autosave: false, new List<EasyNpcIssue>());
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"Could not read '{open.FileName}':\n{ex.Message}", "File Read Error");
            return;
        }

        var results = BuildExportResults(existing.ToDictionary(e => e.NpcKey), addMissing);
        var issues = new List<EasyNpcIssue>();
        var changes = new List<EasyNpcLineChange>();
        var updated = EasyNpcProfile.UpdateLines(original,
            results.Where(r => r.Entry != null).ToDictionary(r => r.Npc, r => r.Entry!), addMissing, issues,
            out var updatedCount, out var addedCount, changes);

        var names = NpcNames();
        var byNpc = results.ToDictionary(r => r.Npc);
        var rows = issues.Select(issue => IssueRow(issue, names)).ToList();
        rows.AddRange(results.Where(r => r.Entry == null).Select(r =>
            new EasyNpcTransferRow(StatusSkipped, NpcName(r.Npc, names), r.Npc.ToString(), "", r.ModName,
                "", "", "", r.Error)));
        foreach (var change in changes)
        {
            var result = byNpc[change.After.NpcKey] with { Entry = change.After };
            var status = change.Kind switch
            {
                EasyNpcLineChangeKind.Added => "Will add",
                EasyNpcLineChangeKind.Updated => "Will update",
                _ => "Unchanged"
            };
            rows.Add(EntryRow(status, result, names, change.Before == null ? "(not in profile)" : DescribeEntry(change.Before))
                with { IsApplicable = true, IsUnchanged = change.Kind == EasyNpcLineChangeKind.Unchanged });
        }

        var options = new EasyNpcTransferWindowOptions("Update EasyNPC Profile", "Save", "will change",
            "Profile Before", "NPC2 Mod",
            ["Default Plugins already in the profile are kept. Profile lines not listed here are saved unchanged."]);
        if (!EasyNpcTransferWindow.Confirm(options, SortRows(rows), out _)) return;

        var save = new SaveFileDialog
        {
            Filter = "EasyNPC Profile (*.txt)|*.txt",
            DefaultExt = ".txt",
            Title = "Save Updated EasyNPC / NEXT Profile As...",
            FileName = Path.GetFileName(open.FileName),
            InitialDirectory = Auxilliary.GetSafeInitialDirectory(Path.GetDirectoryName(open.FileName))
        };
        if (save.ShowDialog() != true || !CanWriteProfile(save.FileName)) return;
        WriteProfile(save.FileName, updated, $"Updated {updatedCount} NPC entries and added {addedCount}.");
    }

    private bool HasSelections()
    {
        if (_settings.SelectedAppearanceMods.Count > 0) return true;
        ScrollableMessageBox.Show("No NPC appearances have been selected.", "Nothing to Export");
        return false;
    }

    private List<ExportResult> BuildExportResults(
        IReadOnlyDictionary<FormKey, EasyNpcProfileEntry>? existing, bool addMissing)
    {
        var results = new List<ExportResult>();
        var mods = SnapshotMods().ToDictionary(mod => mod.DisplayName);
        var exclusions = new HashSet<ModKey>(_lazySettingsVM.Value.ExclusionSelectorViewModel.SaveToModel());
        foreach (var (npc, choice) in _settings.SelectedAppearanceMods)
        {
            if (string.IsNullOrEmpty(choice.ModName)) continue;
            ExportResult Fail(string error) => new(npc, choice.ModName, null, error);
            if (existing != null && !existing.ContainsKey(npc) && !addMissing)
            {
                results.Add(Fail("Absent from profile; Add Missing NPCs is disabled."));
                continue;
            }
            if (!mods.TryGetValue(choice.ModName, out var mod))
            {
                results.Add(Fail($"Selected mod '{choice.ModName}' was not found."));
                continue;
            }
            ModKey defaultPlugin;
            if (existing != null && existing.TryGetValue(npc, out var saved)) defaultPlugin = saved.DefaultPlugin;
            else if (!TryGetDefaultPlugin(npc, exclusions, out defaultPlugin, out var defaultError))
            {
                results.Add(Fail(defaultError!));
                continue;
            }
            // TryCreateEntry also rejects shared faces (a donor NPC EasyNPC cannot express).
            results.Add(EasyNpcTransfer.TryCreateEntry(npc, choice, mod, defaultPlugin, out var entry, out var error)
                ? new(npc, choice.ModName, entry, "")
                : Fail(error));
        }
        return results;
    }

    /// <summary>Mod entries in mod-list order (the order "first match" refers to), detached from
    /// the VMs except for the collections shared by reference.</summary>
    private List<ModSetting> SnapshotMods() => _lazyModListVM.Value.AllModSettings
        .Select(vm => new ModSetting
        {
            DisplayName = vm.DisplayName,
            CorrespondingModKeys = vm.CorrespondingModKeys.ToList(),
            CorrespondingFolderPaths = vm.CorrespondingFolderPaths.ToList(),
            IsFaceGenOnlyEntry = vm.IsFaceGenOnlyEntry,
            FaceGenOnlyNpcFormKeys = vm.FaceGenOnlyNpcFormKeys,
            NpcFormKeys = vm.NpcFormKeys,
            AvailablePluginsForNpcs = vm.AvailablePluginsForNpcs,
            NpcPluginDisambiguation = vm.NpcPluginDisambiguation
        }).ToList();

    private Dictionary<FormKey, string> NpcNames()
    {
        var names = new Dictionary<FormKey, string>();
        foreach (var npc in _lazyNpcSelectionBar.Value.AllNpcs) names.TryAdd(npc.NpcFormKey, npc.DisplayName);
        return names;
    }

    private static string NpcName(FormKey npc, IReadOnlyDictionary<FormKey, string> names) =>
        names.TryGetValue(npc, out var name) ? name : "(not in NPC list)";

    private static string DescribeSelection(string? modName, FormKey source, FormKey npc,
        IReadOnlyDictionary<FormKey, string> names)
    {
        if (string.IsNullOrEmpty(modName)) return "(unassigned)";
        return source == npc ? modName : $"{modName} (face of {NpcName(source, names)})";
    }

    private static string FaceGenName(EasyNpcProfileEntry entry) =>
        EasyNpcProfile.TryGetFaceModName(entry.FaceMod, out var name) ? name : entry.FaceMod;

    private static string DescribeEntry(EasyNpcProfileEntry entry) =>
        entry.FacePlugin.FileName + (entry.FaceMod.Length > 0 ? $" + FaceGen: {FaceGenName(entry)}" : "");

    private static EasyNpcTransferRow EntryRow(string status, ExportResult result,
        IReadOnlyDictionary<FormKey, string> names, string current) =>
        new(status, NpcName(result.Npc, names), result.Npc.ToString(), current, result.ModName,
            result.Entry!.FacePlugin.FileName, FaceGenName(result.Entry), result.Entry.DefaultPlugin.FileName, "");

    private static EasyNpcTransferRow IssueRow(EasyNpcIssue issue, IReadOnlyDictionary<FormKey, string> names) =>
        issue.Npc is { } npc
            ? new(StatusSkipped, NpcName(npc, names), npc.ToString(), "", "", "", "", "",
                issue.Line is { } line ? $"Line {line}: {issue.Message}" : issue.Message)
            : new(StatusUnreadable, $"Line {issue.Line}", "", "", "", "", "", "", issue.Message);

    private static List<EasyNpcTransferRow> SortRows(IEnumerable<EasyNpcTransferRow> rows) => rows
        .OrderBy(r => Array.IndexOf(StatusOrder, r.Status) is var i and >= 0 ? i : StatusOrder.Length)
        .ThenBy(r => r.IsUnchanged)
        .ThenBy(r => r.Npc, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private static bool CanWriteProfile(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase)) return true;
        ScrollableMessageBox.Show("EasyNPC autosave logs are read-only here. Use Import from EasyNPC Autosave to read one, " +
                                  "or save an exported text profile and load it in EasyNPC.", "Autosave Is Read-Only");
        return false;
    }

    private static void WriteProfile(string path, IEnumerable<string> lines, string summary)
    {
        try
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            ScrollableMessageBox.Show($"{summary}\n{path}", "EasyNPC Profile Saved");
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"Could not save '{path}':\n{ex.Message}", "File Save Error");
        }
    }

    private bool TryGetDefaultPlugin(FormKey npcFormKey, ICollection<ModKey> excludedDefaultPlugins, out ModKey defaultPlugin, out string? errorMessage)
    {
        defaultPlugin = default;
        errorMessage = null;
        string formString = $"{npcFormKey.ModKey.FileName}#{npcFormKey.IDString()}";

        if (_settings.EasyNpcDefaultPlugins.TryGetValue(npcFormKey, out var presetDefaultPlugin))
        {
            defaultPlugin = presetDefaultPlugin;
        }
        else
        {
            if (_environmentStateProvider.LinkCache == null)
            {
                errorMessage = $"NPC {formString}: Cannot determine default plugin because Link Cache is not available.";
                return false;
            }

            var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcFormKey);
            if (!contexts.Any())
            {
                errorMessage = $"NPC {formString}: Cannot determine default plugin because no context found in Link Cache.";
                return false;
            }

            foreach (var context in contexts)
            {
                if (!excludedDefaultPlugins.Contains(context.ModKey))
                {
                    defaultPlugin = context.ModKey;
                    break;
                }
            }
        }

        if (defaultPlugin.IsNull)
        {
            errorMessage = $"NPC {formString}: Could not determine a non-excluded Default Plugin.";
            return false;
        }

        return true;
    }
}
