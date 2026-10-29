using System.IO;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>How a profile entry maps onto NPC2 mods. <see cref="Candidates"/> is in mod-list order;
/// with several, the first is the one applied. <see cref="Entry"/> is what is actually imported:
/// its FaceMod is cleared when the override was not applied. <see cref="Warning"/> explains a
/// fallback; <see cref="Error"/> is set only when nothing can be imported.</summary>
internal sealed record EasyNpcImportMatch(EasyNpcProfileEntry Entry, IReadOnlyList<ModSetting> Candidates,
    string Warning = "", string Error = "")
{
    public bool IsAmbiguous => Candidates.Count > 1;
}

/// <summary>Selection conversion without dialogs or a live game. Unsupported combinations are rejected explicitly.</summary>
internal static class EasyNpcTransfer
{
    internal static bool IsFaceGenOnly(ModSetting mod, FormKey npc) =>
        mod.IsFaceGenOnlyEntry || mod.FaceGenOnlyNpcFormKeys.Contains(npc);

    /// <summary>
    /// Mirrors EasyNPC's own FaceMod handling: a FaceMod naming a mod that already supplies the face
    /// plugin is redundant (pre-0.9.2 profiles wrote one for every NPC), and an override EasyNPC
    /// cannot find is ignored in favour of the face plugin. NPC2 additionally falls back when the
    /// override is real but unrepresentable, and says so in the warning.
    /// </summary>
    internal static EasyNpcImportMatch FindImportCandidates(EasyNpcProfileEntry entry, IEnumerable<ModSetting> mods)
    {
        var all = mods.ToList();
        var providers = all.Where(mod => ProvidesPlugin(mod, entry.NpcKey, entry.FacePlugin)).ToList();
        var withoutOverride = entry with { FaceMod = string.Empty };
        var noProvider = $"No mod provides this NPC from appearance plugin '{entry.FacePlugin.FileName}'.";
        if (string.IsNullOrEmpty(entry.FaceMod))
            return new(entry, providers, Error: providers.Count == 0 ? noProvider : "");

        string warning;
        if (!EasyNpcProfile.TryGetFaceModName(entry.FaceMod, out var name))
            warning = $"Invalid FaceGen mod locator '{entry.FaceMod}'.";
        else
        {
            var named = all.Where(mod =>
                string.Equals(mod.DisplayName, name, StringComparison.OrdinalIgnoreCase) ||
                mod.CorrespondingFolderPaths.Any(path =>
                    string.Equals(FolderName(path), name, StringComparison.OrdinalIgnoreCase))).ToList();
            var namedProviders = named.Where(providers.Contains).ToList();
            if (namedProviders.Count > 0) return new(withoutOverride, namedProviders);

            var overrides = named.Where(mod => IsFaceGenOnly(mod, entry.NpcKey) &&
                                               mod.NpcFormKeys.Contains(entry.NpcKey) &&
                                               entry.FacePlugin == entry.NpcKey.ModKey).ToList();
            if (overrides.Count > 0) return new(entry, overrides);

            warning = named.Count == 0
                ? $"FaceGen mod '{name}' was not found (no mod display name or source folder matches)."
                : $"FaceGen override from '{name}' needs the NPC's original record ({entry.NpcKey.ModKey.FileName}) " +
                  "as its appearance plugin; NPC2 cannot pair FaceGen-only mods with another plugin.";
        }
        return providers.Count == 0
            ? new(withoutOverride, providers, Error: $"{warning} {noProvider}")
            : new(withoutOverride, providers, warning + " Imported the appearance plugin without the FaceGen override.");
    }

    /// <summary>True when importing <paramref name="entry"/> into <paramref name="mod"/> changes
    /// nothing NPC2 stores: same mod and own face, same Default Plugin, same source plugin.</summary>
    internal static bool IsUnchanged(EasyNpcProfileEntry entry, ModSetting mod,
        (string? ModName, FormKey NpcFormKey) current, ModKey? currentDefault)
    {
        if (!string.Equals(current.ModName, mod.DisplayName, StringComparison.Ordinal) ||
            current.NpcFormKey != entry.NpcKey || currentDefault != entry.DefaultPlugin) return false;
        if (!ProvidesPlugin(mod, entry.NpcKey, entry.FacePlugin)) return true;
        var plugins = mod.AvailablePluginsForNpcs[entry.NpcKey];
        return plugins.Count == 1 ||
               (mod.NpcPluginDisambiguation.TryGetValue(entry.NpcKey, out var chosen) && chosen == entry.FacePlugin);
    }

    private static bool ProvidesPlugin(ModSetting mod, FormKey npc, ModKey plugin) =>
        !IsFaceGenOnly(mod, npc) && mod.CorrespondingModKeys.Contains(plugin) &&
        mod.AvailablePluginsForNpcs.TryGetValue(npc, out var plugins) &&
        plugins.Contains(plugin);

    internal static void ApplyPluginChoice(EasyNpcProfileEntry entry, ModSetting mod)
    {
        if (ProvidesPlugin(mod, entry.NpcKey, entry.FacePlugin))
            mod.NpcPluginDisambiguation[entry.NpcKey] = entry.FacePlugin;
    }

    internal static bool TryCreateEntry(FormKey npc, (string ModName, FormKey NpcFormKey) selection,
        ModSetting mod, ModKey defaultPlugin, out EasyNpcProfileEntry entry, out string error)
    {
        entry = null!;
        error = string.Empty;
        if (selection.NpcFormKey != npc)
        {
            error = $"Shared face from {selection.NpcFormKey} cannot be represented in an EasyNPC profile.";
            return false;
        }
        if (IsFaceGenOnly(mod, npc))
        {
            var folders = mod.CorrespondingFolderPaths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(FolderName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (folders.Count != 1)
            {
                error = "FaceGen-only export needs exactly one source mod folder so EasyNPC can identify the asset source.";
                return false;
            }
            entry = new(npc, defaultPlugin, npc.ModKey, folders[0]);
        }
        else if (mod.AvailablePluginsForNpcs.TryGetValue(npc, out var plugins) && plugins.Count > 0)
        {
            ModKey plugin;
            if (plugins.Count == 1) plugin = plugins[0];
            else if (!mod.NpcPluginDisambiguation.TryGetValue(npc, out plugin) || !plugins.Contains(plugin))
            {
                error = "Source plugin is ambiguous or the saved source is no longer available. Select its plugin in the Mods tab.";
                return false;
            }
            entry = new(npc, defaultPlugin, plugin);
        }
        else
        {
            error = "The selected mod has no source plugin for this NPC.";
            return false;
        }
        // Names containing delimiters cannot be escaped in EasyNPC's profile grammar.
        if (entry.FaceMod.IndexOfAny(['|', '\r', '\n']) >= 0 ||
            !EasyNpcProfile.TryParseLine(entry.Serialize(), out _, out error))
        {
            error = "The selected mod or plugin name cannot be represented in EasyNPC's profile format.";
            return false;
        }
        return true;
    }

    private static string FolderName(string path) => Path.GetFileName(path.TrimEnd('\\', '/'));
}
