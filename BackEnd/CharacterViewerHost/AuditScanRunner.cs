using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autofac;
using CharacterViewer.Rendering;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Newtonsoft.Json;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// One-shot specimen scan for the outfit-rendering audit
/// (<c>Docs/OutfitRenderingAudit-2026-07.md</c>). When an <c>AuditScan.json</c>
/// file exists next to the exe, the app — after normal startup — walks every
/// NPC's default outfit in the effective load order, inspects the reachable
/// ARMO/ARMA/TXST records and (once, deduplicated) their world-model NIFs, and
/// writes <c>AuditScanReport.csv</c> listing concrete affected records/NPCs per
/// audit finding (AUD-1..AUD-7). Run it THROUGH MO2 so record resolution and
/// NIF disk resolution see the real modlist via the VFS; a direct launch scans
/// only the raw Data folder + vanilla BSAs.
///
/// Config shape (all fields optional):
/// <code>
/// {
///   "outputDirectory": "S:\\temp\\npc2-audit",
///   "exitWhenDone": true,
///   "parseNifs": true,
///   "maxRowsPerIssue": 250
/// }
/// </code>
///
/// Detectors (see the audit doc for full rationale):
///  AUD-1 outfit ARMA carries NAM0 skin TXST and its NIF has non-skin shapes
///  AUD-2 NAM0/AlternateTextures TXST authors TX03 (Glow/Detail)
///  AUD-3 shape has a slot-7 map but SLSF1_Specular is clear
///  AUD-4 glow-map shape (SLSF2_Glow_Map + slot 2) — feature gap census
///  AUD-5 eye-name census (miscased "eyes"; non-eye shapes named "Eyes")
///  AUD-6 NiAlphaProperty forced-alpha-test fallback engaged
///  AUD-7 duplicate shape names (flagged when AlternateTextures target them)
///
/// Deliberate over-collection: leveled-list entries are ALL recursed (ignoring
/// Use-All/chance semantics) because the scan wants every armor that COULD be
/// worn, not the one the preview would deterministically pick.
/// </summary>
public static class AuditScanRunner
{
    public const string TriggerFileName = "AuditScan.json";

    public static string TriggerPath => Path.Combine(AppContext.BaseDirectory, TriggerFileName);

    public static bool ConfigExists => File.Exists(TriggerPath);

    public class AuditScanConfig
    {
        public string OutputDirectory { get; set; } = "";
        public bool ExitWhenDone { get; set; } = true;
        public bool ParseNifs { get; set; } = true;
        public int MaxRowsPerIssue { get; set; } = 250;
    }

    private sealed record Row(
        string Issue, string NpcOrSource, string NpcName, string Armo, string ArmoEdid,
        string Arma, string Txst, string TxstEdid, string NifGamePath, string NifDiskPath,
        string Shape, string Ordinal, string ShaderType, string Detail);

    /// <summary>One deduplicated world-model NIF plus the record-side context
    /// accumulated from every ARMA that referenced it.</summary>
    private sealed class NifJob
    {
        public string GamePath = "";
        public string Source = "";                       // e.g. "Outfit" or "HeadPart:Eyes"
        public string ExampleNpc = "";
        public string ExampleNpcName = "";
        public string ExampleRace = "";
        // True when the recommended example NPC's race is actually SERVED by the
        // ARMA pointing at this world model — i.e. the NPC really renders THIS
        // mesh. Beast-race world models (GauntletsHeavyBeast) are served only by
        // Khajiit/Argonian; a Dunmer wearing the same Armor renders the human
        // variant, so crediting him made the AUD-1/AUD-2 specimen un-reproducible.
        public bool ExampleServesRace;
        public bool Inspected;                           // TXST/record work done once per NIF
        public string Armo = "";
        public string ArmoEdid = "";
        public string Arma = "";
        public bool HasNam0;
        public string Nam0Txst = "";
        public string Nam0TxstEdid = "";
        // (shape name, 3D index, TXST fk, TXST edid) of alt entries whose TXST authors TX03.
        public readonly List<(string Name, int Index, string Txst, string TxstEdid)> Tx03AltTargets = new();
        // Every alt-entry 3D Name (for AUD-7 grading).
        public readonly HashSet<string> AltEntryNames = new(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<bool> RunAsync(IComponentContext container)
    {
        AuditScanConfig config;
        try
        {
            config = JsonConvert.DeserializeObject<AuditScanConfig>(File.ReadAllText(TriggerPath))
                     ?? new AuditScanConfig();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "AuditScan.log"),
                $"FATAL: could not parse {TriggerFileName}: {ex.Message}");
            return true;
        }

        string outDir = string.IsNullOrWhiteSpace(config.OutputDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "AuditScanOutput")
            : config.OutputDirectory;

        var log = new StringBuilder();
        log.AppendLine($"AuditScan started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        try
        {
            Directory.CreateDirectory(outDir);
            var env = container.Resolve<EnvironmentStateProvider>();
            var previewCache = container.Resolve<CharacterPreviewCache>();
            var assetResolver = container.Resolve<GameAssetResolver>();
            var bsa = container.Resolve<IBsaArchiveProvider>();

            await Task.Run(() => ScanCore(config, outDir, env, previewCache, assetResolver, bsa, log));
        }
        catch (Exception ex)
        {
            log.AppendLine("FATAL: " + ex);
        }
        finally
        {
            try { File.WriteAllText(Path.Combine(outDir, "AuditScan.log"), log.ToString()); }
            catch { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "AuditScan.log"), log.ToString()); }
        }

        return config.ExitWhenDone;
    }

    private static void ScanCore(AuditScanConfig config, string outDir,
        EnvironmentStateProvider env, CharacterPreviewCache previewCache,
        GameAssetResolver assetResolver, IBsaArchiveProvider bsa, StringBuilder log)
    {
        var linkCache = env.LinkCache;
        if (linkCache == null)
        {
            log.AppendLine("FATAL: link cache unavailable (environment not resolved).");
            return;
        }

        bsa.EnsureAllArchivesOpened();

        var rows = new List<Row>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void AddRow(Row row)
        {
            counts[row.Issue] = counts.TryGetValue(row.Issue, out var c) ? c + 1 : 1;
            if (counts[row.Issue] <= config.MaxRowsPerIssue) rows.Add(row);
        }

        // ---- Phase 1: record walk (cheap) --------------------------------
        // Every NPC's default outfit -> ARMO set (all leveled entries) -> ARMA.
        // NOT deduplicated by ARMO: InspectArma is visited for every wearer so
        // the per-NIF example can upgrade to a race-SERVING named wearer (the
        // one who actually renders that mesh). The expensive TXST/record work
        // is still done once per NIF (job.Inspected); only the cheap example
        // comparison runs per wearer. The NIF PARSE (phase 2) stays deduped.
        var jobsByPath = new Dictionary<string, NifJob>(StringComparer.OrdinalIgnoreCase);
        var raceNames = new Dictionary<FormKey, string>();
        int npcCount = 0, outfitNpcCount = 0, armoVisits = 0;

        foreach (var npc in env.LoadOrder.PriorityOrder.Npc().WinningOverrides())
        {
            npcCount++;
            if (npc.DefaultOutfit == null || npc.DefaultOutfit.IsNull) continue;
            if (!linkCache.TryResolve<IOutfitGetter>(npc.DefaultOutfit.FormKey, out var outfit)
                || outfit.Items == null) continue;
            outfitNpcCount++;

            bool female = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
            string npcName = npc.Name?.String ?? "";
            FormKey raceKey = npc.Race.FormKey;
            if (!raceNames.TryGetValue(raceKey, out var raceName))
            {
                raceName = linkCache.TryResolve<IRaceGetter>(raceKey, out var race)
                    ? (race.EditorID ?? raceKey.ToString()) : raceKey.ToString();
                raceNames[raceKey] = raceName;
            }

            var armorKeys = new HashSet<FormKey>();
            foreach (var item in outfit.Items)
                CollectArmors(item.FormKey, linkCache, armorKeys, depth: 0);

            foreach (var armoKey in armorKeys)
            {
                if (!linkCache.TryResolve<IArmorGetter>(armoKey, out var armo)
                    || armo.Armature == null) continue;

                foreach (var armaLink in armo.Armature)
                {
                    if (!linkCache.TryResolve<IArmorAddonGetter>(armaLink.FormKey, out var arma)) continue;
                    armoVisits++;
                    InspectArma(arma, armo, female, raceKey, raceName, linkCache,
                        jobsByPath, npc.FormKey, npcName, log);
                }
            }
        }
        log.AppendLine($"Record walk: {npcCount} NPCs, {outfitNpcCount} with outfits, " +
                       $"{armoVisits} ARMA visits, {jobsByPath.Count} unique world-model NIFs.");

        // Eye headparts for the AUD-5 census (few dozen NIFs, cheap).
        int eyeHeadParts = 0;
        foreach (var hp in env.LoadOrder.PriorityOrder.HeadPart().WinningOverrides())
        {
            if (hp.Type != HeadPart.TypeEnum.Eyes) continue;
            string? file = hp.Model?.File.GivenPath;
            if (string.IsNullOrWhiteSpace(file)) continue;
            string gamePath = PrefixMeshes(file);
            if (!jobsByPath.ContainsKey(gamePath))
            {
                jobsByPath[gamePath] = new NifJob
                {
                    GamePath = gamePath,
                    Source = "HeadPart:Eyes",
                    ExampleNpcName = hp.EditorID ?? hp.FormKey.ToString(),
                };
                eyeHeadParts++;
            }
        }
        log.AppendLine($"Eye headparts added: {eyeHeadParts} NIFs.");

        // ---- Phase 2: NIF pass (deduplicated, parse-only, no GL) ---------
        int parsed = 0, unresolved = 0, failed = 0;
        if (config.ParseNifs)
        {
            var meshBuilder = previewCache.MeshBuilder;
            foreach (var job in jobsByPath.Values)
            {
                string? diskPath = null;
                try { diskPath = assetResolver.ResolveAssetSource(job.GamePath).ResolvedDiskPath; }
                catch { /* counted below */ }
                if (diskPath == null) { unresolved++; continue; }

                List<NifMeshBuilder.BuiltMesh> built;
                try
                {
                    built = meshBuilder.BuildFromFile(diskPath, null, null, bipedBodyPart: null);
                    parsed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.AppendLine($"parse failed: {diskPath} — {ex.Message}");
                    continue;
                }

                RunNifDetectors(job, diskPath, built, AddRow);
            }
        }
        log.AppendLine($"NIF pass: parsed={parsed}, unresolved={unresolved}, failed={failed}.");

        // ---- Report ------------------------------------------------------
        string csvPath = Path.Combine(outDir, "AuditScanReport.csv");
        using (var w = new StreamWriter(csvPath, append: false))
        {
            w.WriteLine("# NPC2 outfit-rendering audit scan — see Docs/OutfitRenderingAudit-2026-07.md");
            w.WriteLine("# generated " + DateTime.Now.ToString("o"));
            w.WriteLine("# " + log.ToString().Replace("\r", "").Replace("\n", " | "));
            foreach (var kv in counts.OrderBy(k => k.Key))
                w.WriteLine($"# {kv.Key}: {kv.Value} hit(s)" +
                            (kv.Value > config.MaxRowsPerIssue ? $" (rows capped at {config.MaxRowsPerIssue})" : ""));
            w.WriteLine("issue,npc_or_source,npc_name,armo,armo_edid,arma,txst,txst_edid,nif_game_path,nif_disk_path,shape,ordinal,shader_type,detail");
            foreach (var r in rows.OrderBy(r => r.Issue, StringComparer.Ordinal))
            {
                w.WriteLine(string.Join(",",
                    Csv(r.Issue), Csv(r.NpcOrSource), Csv(r.NpcName), Csv(r.Armo), Csv(r.ArmoEdid),
                    Csv(r.Arma), Csv(r.Txst), Csv(r.TxstEdid), Csv(r.NifGamePath), Csv(r.NifDiskPath),
                    Csv(r.Shape), Csv(r.Ordinal), Csv(r.ShaderType), Csv(r.Detail)));
            }
        }
        log.AppendLine($"Report written: {csvPath} ({rows.Count} rows).");
        foreach (var kv in counts.OrderBy(k => k.Key))
            log.AppendLine($"  {kv.Key}: {kv.Value}");
    }

    /// <summary>Record-side ARMA inspection: NAM0 presence, TX03-bearing TXSTs,
    /// alt-entry names; registers/updates the world-model NIF job. The example
    /// wearer is chosen race-aware — an NPC only "renders this mesh" if the
    /// ARMA's Race/AdditionalRaces serve the NPC's race (mirrors the resolver's
    /// IsArmatureForRace); a race-serving named wearer beats a race-mismatched
    /// one, so beast-mesh jobs get a beast-race specimen. The heavier TXST work
    /// runs once per NIF (job.Inspected).</summary>
    private static void InspectArma(IArmorAddonGetter arma, IArmorGetter armo, bool female,
        FormKey npcRaceKey, string npcRaceName, ILinkCache linkCache,
        Dictionary<string, NifJob> jobsByPath,
        FormKey exampleNpc, string exampleNpcName, StringBuilder log)
    {
        var model = female ? arma.WorldModel?.Female : arma.WorldModel?.Male;
        model ??= female ? arma.WorldModel?.Male : arma.WorldModel?.Female; // sex fallback
        string? file = model?.File.GivenPath;
        if (string.IsNullOrWhiteSpace(file)) return;
        string gamePath = PrefixMeshes(file);

        if (!jobsByPath.TryGetValue(gamePath, out var job))
        {
            job = new NifJob { GamePath = gamePath, Source = "Outfit" };
            jobsByPath[gamePath] = job;
        }

        // Example selection (runs for every wearer, cheap). Prefer a wearer the
        // ARMA's race actually serves, then a named one.
        bool servesRace = ArmaServesRace(arma, npcRaceKey);
        bool better =
            job.ExampleNpc.Length == 0
            || (!job.ExampleServesRace && servesRace)
            || (job.ExampleServesRace == servesRace
                && job.ExampleNpcName.Length == 0 && exampleNpcName.Length > 0);
        if (better)
        {
            job.ExampleNpc = exampleNpc.ToString();
            job.ExampleNpcName = exampleNpcName;
            job.ExampleRace = npcRaceName;
            job.ExampleServesRace = servesRace;
        }

        // Heavy record inspection once per NIF.
        if (job.Inspected) return;
        job.Inspected = true;

        job.Armo = armo.FormKey.ToString();
        job.ArmoEdid = armo.EditorID ?? "";
        job.Arma = arma.FormKey.ToString();

        // NAM0 (flat skin TXST) — AUD-1 candidate; its TXST also checked for TX03 (AUD-2).
        var skinLink = female ? arma.SkinTexture?.Female : arma.SkinTexture?.Male;
        if (skinLink != null && !skinLink.IsNull)
        {
            job.HasNam0 = true;
            if (linkCache.TryResolve<ITextureSetGetter>(skinLink.FormKey, out var nam0Txst))
            {
                job.Nam0Txst = nam0Txst.FormKey.ToString();
                job.Nam0TxstEdid = nam0Txst.EditorID ?? "";
                if (!string.IsNullOrWhiteSpace(nam0Txst.GlowOrDetailMap?.DataRelativePath.Path))
                    job.Tx03AltTargets.Add(("(NAM0 — applies to all shapes)", -1,
                        nam0Txst.FormKey.ToString(), nam0Txst.EditorID ?? ""));
            }
        }

        // AlternateTextures entries: collect names (AUD-7) + TX03-bearing TXSTs (AUD-2).
        var alts = model?.AlternateTextures;
        if (alts == null) return;
        foreach (var alt in alts)
        {
            if (alt == null) continue;
            string name = alt.Name ?? "";
            if (name.Length > 0) job.AltEntryNames.Add(name);
            if (alt.NewTexture.IsNull) continue;
            if (!linkCache.TryResolve<ITextureSetGetter>(alt.NewTexture.FormKey, out var txst)) continue;
            if (!string.IsNullOrWhiteSpace(txst.GlowOrDetailMap?.DataRelativePath.Path))
                job.Tx03AltTargets.Add((name, alt.Index, txst.FormKey.ToString(), txst.EditorID ?? ""));
        }
    }

    /// <summary>NIF-side detectors, run once per deduplicated world-model.</summary>
    private static void RunNifDetectors(NifJob job, string diskPath,
        List<NifMeshBuilder.BuiltMesh> built, Action<Row> addRow)
    {
        // Name column carries the race + a marker when the recommended wearer's
        // race is NOT served by this mesh (no true in-game specimen found — the
        // finding is record-side only, e.g. a beast mesh nobody in the load
        // order wears).
        string nameCol = job.ExampleNpcName
            + (job.ExampleRace.Length > 0 ? " [" + job.ExampleRace + "]" : "")
            + (job.ExampleNpc.Length > 0 && !job.ExampleServesRace ? " (race-mismatch: no wearer renders this mesh)" : "");
        Row Mk(string issue, string shape, string ordinal, string shaderType, string detail,
               string txst = "", string txstEdid = "") =>
            new(issue, job.ExampleNpc.Length > 0 ? job.ExampleNpc : job.Source, nameCol,
                job.Armo, job.ArmoEdid, job.Arma, txst, txstEdid, job.GamePath, diskPath,
                shape, ordinal, shaderType, detail);

        bool IsSkin(NifMeshBuilder.BuiltMesh b) => b.ShaderType == 4 || b.ShaderType == 5;

        // AUD-1: NAM0 present + at least one non-skin shape -> the flat merge
        // would restyle armor material with the skin TXST.
        if (job.HasNam0)
        {
            var nonSkin = built.Where(b => !IsSkin(b)).ToList();
            if (nonSkin.Count > 0)
            {
                addRow(Mk("AUD-1",
                    string.Join(";", nonSkin.Take(6).Select(b => b.ShapeName)),
                    string.Join(";", nonSkin.Take(6).Select(b => b.ShapeOrdinal)),
                    string.Join(";", nonSkin.Take(6).Select(b => b.ShaderType)),
                    $"NAM0 skin TXST would be merged onto {nonSkin.Count} non-skin shape(s); " +
                    $"skin shapes present: {built.Count(IsSkin)}",
                    job.Nam0Txst, job.Nam0TxstEdid));
            }
        }

        // AUD-2: TX03-bearing TXSTs — grade against the target shape's shader type.
        foreach (var (name, index, txst, txstEdid) in job.Tx03AltTargets)
        {
            NifMeshBuilder.BuiltMesh? target = null;
            if (index == -1) // NAM0: applies to all; report the first skin shape if any
                target = built.FirstOrDefault(IsSkin);
            else
                target = built.FirstOrDefault(b => string.Equals(b.ShapeName, name, StringComparison.OrdinalIgnoreCase))
                      ?? built.FirstOrDefault(b => b.ShapeOrdinal == index);
            string grade = target == null
                ? "target shape not found in mesh"
                : IsSkin(target)
                    ? "CONFIRMED: TX03 would overwrite the skin/SSS sampler (slot 2) on a skin-shader shape"
                    : $"record-side only (target shader type {target.ShaderType}; slot-2 write is inert for non-skin)";
            addRow(Mk("AUD-2", target?.ShapeName ?? name,
                (target?.ShapeOrdinal ?? index).ToString(),
                target?.ShaderType.ToString() ?? "",
                grade, txst, txstEdid));
        }

        foreach (var b in built)
        {
            // AUD-3: slot-7 map present, SLSF1_Specular clear.
            if (b.TexturePaths.ContainsKey(7) && (b.ShaderFlags1 & (1u << 0)) == 0)
                addRow(Mk("AUD-3", b.ShapeName, b.ShapeOrdinal.ToString(), b.ShaderType.ToString(),
                    $"slot 7 = {b.TexturePaths[7]} but SLSF1_Specular is clear (flags1=0x{b.ShaderFlags1:X8})"));

            // AUD-4: glow-map census (feature gap).
            if ((b.ShaderFlags2 & (1u << 6)) != 0 && b.TexturePaths.ContainsKey(2) && !IsSkin(b))
                addRow(Mk("AUD-4", b.ShapeName, b.ShapeOrdinal.ToString(), b.ShaderType.ToString(),
                    $"SLSF2_Glow_Map with slot 2 = {b.TexturePaths[2]}" +
                    ((b.ShaderFlags1 & (1u << 29)) != 0 ? "; SLSF1_External_Emittance also set" : "")));

            // AUD-5: eye-name census.
            bool namedEyesExact = b.ShapeName.Contains("Eyes", StringComparison.Ordinal);
            bool namedEyesAnyCase = b.ShapeName.Contains("eyes", StringComparison.OrdinalIgnoreCase);
            if (namedEyesAnyCase && !namedEyesExact)
                addRow(Mk("AUD-5", b.ShapeName, b.ShapeOrdinal.ToString(), b.ShaderType.ToString(),
                    "miscased eye-ish name — misses the case-sensitive \"Eyes\" heuristic" +
                    (job.Source == "HeadPart:Eyes" ? " on an actual eye headpart mesh" : "")));
            else if (namedEyesExact && b.ShaderType != 16 && job.Source != "HeadPart:Eyes")
                addRow(Mk("AUD-5", b.ShapeName, b.ShapeOrdinal.ToString(), b.ShaderType.ToString(),
                    "non-eye-shader shape named \"Eyes\" — would be treated as an eye (eyeCubemapScale, AO opt-out)"));

            // AUD-6: builder's forced alpha-test fallback engaged (property
            // present, neither enable bit authored). Uses BuiltMesh.AlphaFlagsRaw
            // — keep the CV.R PackageReference in sync (bump/publish) before a
            // fresh clone builds against the NuGet package rather than the
            // sibling source. Returned 0 hits in the 2026-07 scans (AUD-6
            // closed) but kept for future modlists.
            if (b.HasAlphaTest && (b.AlphaFlagsRaw & 0x0201) == 0)
                addRow(Mk("AUD-6", b.ShapeName, b.ShapeOrdinal.ToString(), b.ShaderType.ToString(),
                    $"NiAlphaProperty flags=0x{b.AlphaFlagsRaw:X4} (no enable bits) -> alpha test forced; " +
                    $"threshold={b.AlphaThreshold:F3}" +
                    (b.AlphaThreshold > 0f ? " — NONZERO threshold, engine would draw opaque" : "")));
        }

        // AUD-7: duplicate shape names, graded by whether alt entries target them.
        foreach (var dupe in built.GroupBy(b => b.ShapeName, StringComparer.OrdinalIgnoreCase)
                                  .Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            bool targeted = job.AltEntryNames.Contains(dupe.Key);
            addRow(Mk("AUD-7", dupe.Key,
                string.Join(";", dupe.Select(b => b.ShapeOrdinal)),
                string.Join(";", dupe.Select(b => b.ShaderType)),
                targeted
                    ? "CONFIRMED: duplicate shape name IS targeted by an AlternateTextures entry (name match fans out)"
                    : "latent: duplicate names present, no alt entry currently targets them"));
        }
    }

    /// <summary>All-entries leveled recursion (deliberate over-collection; see class remarks).</summary>
    private static void CollectArmors(FormKey itemKey, ILinkCache linkCache,
        HashSet<FormKey> armors, int depth)
    {
        if (depth > 10) return;
        if (linkCache.TryResolve<IArmorGetter>(itemKey, out _)) { armors.Add(itemKey); return; }
        if (!linkCache.TryResolve<ILeveledItemGetter>(itemKey, out var lvli) || lvli.Entries == null) return;
        foreach (var entry in lvli.Entries)
        {
            var refLink = entry?.Data?.Reference;
            if (refLink == null || refLink.IsNull) continue;
            CollectArmors(refLink.FormKey, linkCache, armors, depth + 1);
        }
    }

    /// <summary>Mirror of the resolver's IsArmatureForRace: does this ARMA's
    /// Race / AdditionalRaces serve <paramref name="npcRaceKey"/>? Determines
    /// whether an NPC of that race actually renders the ARMA's world model.</summary>
    private static bool ArmaServesRace(IArmorAddonGetter arma, FormKey npcRaceKey)
    {
        if (!arma.Race.IsNull && arma.Race.FormKey.Equals(npcRaceKey)) return true;
        if (arma.AdditionalRaces != null)
            foreach (var r in arma.AdditionalRaces)
                if (!r.IsNull && r.FormKey.Equals(npcRaceKey)) return true;
        return false;
    }

    private static string PrefixMeshes(string path)
        => path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)
            ? path : "meshes\\" + path;

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
