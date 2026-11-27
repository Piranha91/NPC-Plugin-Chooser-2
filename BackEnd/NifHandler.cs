using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class NifHandler
{
    /// <summary>Loads a NIF, or returns null with <paramref name="code"/> non-zero when it is
    /// missing, unreadable or malformed. Nifly reports a bad header or block with a non-zero
    /// result but can also throw while linking the parsed blocks, so both count as a failed load,
    /// as a non-zero result did with the old library. The file is read in one go, which releases
    /// it before any save back to the same path, into a pooled buffer because Load copies what it
    /// keeps and a fresh array per NIF is large-object-heap garbage. Texture paths are normalized as
    /// the old library's load normalized them (<see cref="NifEdit.TrimTexturePaths"/>), so every
    /// read sees, and every save writes, the paths it always did.</summary>
    private static NifFile? TryLoad(string path, out int code)
    {
        byte[]? buffer = null;
        try
        {
            int length;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                length = (int)file.Length;
                buffer = ArrayPool<byte>.Shared.Rent(length);
                file.ReadExactly(buffer, 0, length);
            }
            return TryLoad(new MemoryStream(buffer, 0, length, writable: false), out code);
        }
        catch (Exception)
        {
            code = 1;
            return null;
        }
        finally
        {
            if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static NifFile? TryLoad(Stream stream, out int code)
    {
        try
        {
            using (stream)
            {
                var nif = new NifFile();
                code = nif.Load(stream);
                if (code != 0) return null;
                NifEdit.TrimTexturePaths(nif);
                return nif;
            }
        }
        catch (Exception)
        {
            code = 1;
            return null;
        }
    }


    private static NifFile? TryLoad(string path) => TryLoad(path, out _);

    /// <summary>What the read-only lookups report about one version of a NIF. A patch run asks
    /// several of them about the same file, and each answer is small next to the parse it would
    /// repeat. Each part keeps its own failure, so one that throws doesn't fail the others.</summary>
    private sealed class NifReadSummary
    {
        private readonly Part<HashSet<string>> _textureSetPaths;
        private readonly Part<HashSet<string>> _physicsXmlPaths;
        private readonly Part<IReadOnlyList<NifShapeTextureInfo>> _shapeDetails;

        public NifReadSummary(NifFile? nif)
        {
            _textureSetPaths = new Part<HashSet<string>>(() => ReadTextureSetPaths(nif));
            _physicsXmlPaths = new Part<HashSet<string>>(() => ReadPhysicsXmlPaths(nif));
            _shapeDetails = new Part<IReadOnlyList<NifShapeTextureInfo>>(() => ReadShapeTextureDetails(nif));
        }

        public HashSet<string> TextureSetPaths => _textureSetPaths.Value;
        public HashSet<string> PhysicsXmlPaths => _physicsXmlPaths.Value;
        public IReadOnlyList<NifShapeTextureInfo> ShapeDetails => _shapeDetails.Value;

        private sealed class Part<T>
        {
            private readonly T? _value;
            private readonly ExceptionDispatchInfo? _error;

            public Part(Func<T> read)
            {
                try { _value = read(); }
                catch (Exception ex) { _error = ExceptionDispatchInfo.Capture(ex); }
            }

            public T Value
            {
                get
                {
                    _error?.Throw();
                    return _value!;
                }
            }
        }
    }

    private sealed record CachedRead(long Length, DateTime LastWriteUtc, Lazy<NifReadSummary> Summary);

    private static readonly ConcurrentDictionary<string, CachedRead> ReadCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static long _readLookups;
    private static long _readParses;

    /// <summary>Lookups served and NIFs parsed since the last <see cref="ClearReadCache"/>.</summary>
    public static (long Lookups, long Parses) ReadCacheStats =>
        (Interlocked.Read(ref _readLookups), Interlocked.Read(ref _readParses));

    /// <summary>Forgets the summaries. Called at the end of a patch run, whose NIFs are the only
    /// ones this cache holds; counters are kept so a harness can report them afterwards.</summary>
    public static void ClearReadCache() => ReadCache.Clear();

    public static void ResetReadCacheStats()
    {
        Interlocked.Exchange(ref _readLookups, 0);
        Interlocked.Exchange(ref _readParses, 0);
    }

    /// <summary>The summary of the file as it is now: an entry only answers for the length and
    /// write time it was read at, because patching edits copied NIFs between lookups. Lazy makes
    /// parallel lookups of one file share a parse. A file that can't be statted isn't cached.</summary>
    private static NifReadSummary ReadSummary(string path)
    {
        Interlocked.Increment(ref _readLookups);
        long length;
        DateTime lastWriteUtc;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new NifReadSummary(null);
            length = info.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
        }
        catch (Exception)
        {
            return new NifReadSummary(null);
        }

        CachedRead Fresh() => new(length, lastWriteUtc, new Lazy<NifReadSummary>(() =>
        {
            Interlocked.Increment(ref _readParses);
            return new NifReadSummary(TryLoad(path));
        }));
        var entry = ReadCache.AddOrUpdate(path, _ => Fresh(),
            (_, cached) => cached.Length == length && cached.LastWriteUtc == lastWriteUtc ? cached : Fresh());
        return entry.Summary.Value;
    }

    /// <summary>Saves the way the old library's default <c>Save</c> did: nifly's
    /// <c>NifSaveOptions</c> default to <c>optimize</c> (recompute every shape's bounds and drop
    /// unreferenced blocks) and <c>sortBlocks</c>. Both optimize steps run here instead of in
    /// Nifly's save: its bounds can leave vertices outside the sphere
    /// (<see cref="NifEdit.UpdateBounds"/>), and its unreferenced-block sweep is quadratic
    /// (<see cref="NifEdit.RemoveUnreferencedBlocks"/>).</summary>
    private static int Save(NifFile nif, string path)
    {
        ReadCache.TryRemove(path, out _);
        NifEdit.UpdateBounds(nif);
        NifEdit.RemoveUnreferencedBlocks(nif);
        return nif.Save(path, new NifFileSaveOptions { RemoveUnreferencedBlocks = false, SortBlocks = true, UpdateBounds = false });
    }

    public static HashSet<string> GetExtraTexturesFromNif(string nifPath)
        => new HashSet<string>(ReadSummary(nifPath).TextureSetPaths);

    private static HashSet<string> ReadTextureSetPaths(NifFile? nif)
    {
        HashSet<string> uniqueTextures = new HashSet<string>();
        if (nif == null) return uniqueTextures;

        foreach (var textureSet in nif.Blocks.OfType<BSShaderTextureSet>())
        {
            if (textureSet.Textures == null) continue;
            foreach (var tex in textureSet.Textures)
            {
                string? path = tex?.Content;
                if (!string.IsNullOrEmpty(path))
                {
                    uniqueTextures.Add(path);
                }
            }
        }
        return uniqueTextures;
    }

    /// <summary>
    /// The texture paths each shape references, grouped by shape.
    /// <see cref="GetExtraTexturesFromNif"/> flattens the whole file into one set, which cannot
    /// answer "is this shape textureless?" — a mod routinely ships a NIF where one slot of one
    /// shape is missing (an absent mouth subsurface map is near-universal) and that is harmless,
    /// while a shape whose textures are ALL missing renders untextured and is worth reporting.
    ///
    /// <para>Slots are read through <c>GetTexturePathByIndex</c> (0-8, the BSShaderTextureSet
    /// slot range) rather than by walking BSShaderTextureSet blocks directly, because one texture
    /// set can be shared by several shapes and the block walk loses which shape used it. Empty
    /// slots are omitted; shapes with no shader property (SMP collision/virtual meshes) yield an
    /// empty list and are still returned, so callers can tell "no textures" from "not present".</para>
    ///
    /// <para><c>DrawnInGame</c> is false for a shape the engine cannot put on screen no matter what
    /// its textures resolve to — see <see cref="ShapeIsDrawnInGame"/>. A caller reporting texture
    /// problems must skip those, or it warns about geometry nobody can see.</para>
    /// </summary>
    public static IReadOnlyList<(string ShapeName, IReadOnlyList<string> TexturePaths, bool DrawnInGame)>
        GetTexturesByShape(string nifPath)
        => GetShapeTextureDetails(nifPath)
            .Select(s => (s.ShapeName,
                (IReadOnlyList<string>)s.Slots.Select(t => t.Path).ToList(),
                s.DrawnInGame))
            .ToList();

    /// <summary>One non-empty texture slot of a shape's BSShaderTextureSet.
    /// Slot is the BSShaderTextureSet index (0 diffuse, 1 normal, 2 glow/
    /// subsurface, 3 detail/greyscale palette, 4 environment cubemap, 5
    /// environment mask, 6 face tint / inner layer, 7 specular/backlight).</summary>
    public readonly record struct NifSlotTexture(int Slot, string Path);

    /// <summary>Sentinel for <see cref="NifShapeTextureInfo.ShaderType"/> when the
    /// shape has no readable BSLightingShaderProperty — callers must treat the
    /// shader configuration as unknown and judge slots conservatively.</summary>
    public const uint UnknownShaderType = uint.MaxValue;

    /// <summary>Texture/shader survey of one shape. ShaderType is the raw
    /// BSLightingShaderProperty.bslspShaderType value (1 = environment map,
    /// 4 = face tint, 16 = eye envmap, …); ShaderFlags1 the raw SLSF1 bits.
    /// PartitionSlots are the BSDismemberSkinInstance biped partition IDs
    /// (30 = head, 32 = body, …) — empty for shapes without dismember data —
    /// so reports can say WHICH body slot a broken shape occupies.</summary>
    public sealed record NifShapeTextureInfo(
        string ShapeName,
        bool DrawnInGame,
        uint ShaderType,
        uint ShaderFlags1,
        IReadOnlyList<NifSlotTexture> Slots,
        IReadOnlyList<int> PartitionSlots);

    /// <summary>
    /// Slot-preserving variant of <see cref="GetTexturesByShape"/>: which SLOT a
    /// path occupies decides whether the engine ever samples it (the face-tint
    /// slot is engine-managed, the environment slots are flag-gated), so callers
    /// judging missing textures need the index and the shape's shader
    /// configuration, not just the flat path list.
    /// </summary>
    public static IReadOnlyList<NifShapeTextureInfo> GetShapeTextureDetails(string nifPath)
        => ReadSummary(nifPath).ShapeDetails;

    /// <summary>As <see cref="GetShapeTextureDetails(string)"/>, for a path the renderer's
    /// resolver produced: a BSA asset it serves from memory has no file at that path, so the
    /// NIF is read through <paramref name="resolver"/>.</summary>
    public static IReadOnlyList<NifShapeTextureInfo> GetShapeTextureDetails(string nifPath,
        SkyRender.GameAssetResolver? resolver)
    {
        if (resolver == null) return GetShapeTextureDetails(nifPath);
        Stream stream;
        try { stream = resolver.OpenResolvedRead(nifPath); }
        catch (Exception) { return new List<NifShapeTextureInfo>(); }
        return ReadShapeTextureDetails(TryLoad(stream, out _));
    }

    private static IReadOnlyList<NifShapeTextureInfo> ReadShapeTextureDetails(NifFile? nif)
    {
        var results = new List<NifShapeTextureInfo>();
        if (nif == null) return results;

        foreach (var shape in NifEdit.GetShapes(nif))
        {
            string name = shape.Name?.String ?? string.Empty;
            var slots = new List<NifSlotTexture>();
            for (int slot = 0; slot < 9; slot++)
            {
                string tex = NifEdit.GetTexturePathByIndex(nif, shape, slot);
                if (!string.IsNullOrWhiteSpace(tex)) slots.Add(new NifSlotTexture(slot, tex));
            }
            var (shaderType, flags1) = ReadShaderInfo(nif, shape);
            results.Add(new NifShapeTextureInfo(name, ShapeIsDrawnInGame(nif, shape),
                shaderType, flags1, slots, ReadPartitionSlots(nif, shape)));
        }
        return results;
    }

    /// <summary>BSDismemberSkinInstance partition IDs of a shape (30 head,
    /// 32 body, 33 hands, …), empty when the shape has no dismember data.
    /// Same recipe as CV.R's NifMeshBuilder.ReadDismemberPartitions.</summary>
    private static IReadOnlyList<int> ReadPartitionSlots(NifFile nif, INiShape shape)
    {
        try
        {
            if (nif.GetBlock<BSDismemberSkinInstance>(shape.SkinInstanceRef) is not { } dismember)
                return Array.Empty<int>();
            if (dismember.Partitions == null) return Array.Empty<int>();
            var ids = new List<int>(dismember.Partitions.Count);
            foreach (var partition in dismember.Partitions) ids.Add((int)partition.BodyPart);
            return ids;
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    /// <summary>Reads (bslspShaderType, shaderFlags1) from the shape's
    /// BSLightingShaderProperty; (<see cref="UnknownShaderType"/>, 0) when
    /// absent or unreadable, so slot-relevance callers fail conservative.
    /// Flags1 is the raw word of the layout the block was read as (Skyrim, or FO4 from
    /// stream 130), as the old library's single <c>shaderFlags1</c> field held it.</summary>
    private static (uint ShaderType, uint Flags1) ReadShaderInfo(NifFile nif, INiShape shape)
    {
        try
        {
            if (nif.GetBlock<BSLightingShaderProperty>(shape.ShaderPropertyRef) is not { } shader)
                return (UnknownShaderType, 0);
            uint flags1 = shader.Type == NiflySharp.Helpers.ShaderHelper.ShaderGameType.FO4
                ? (uint)shader.ShaderFlags_F4SPF1
                : (uint)shader.ShaderFlags_SSPF1;
            return ((uint)shader.ShaderType_SK_FO4, flags1);
        }
        catch
        {
            return (UnknownShaderType, 0);
        }
    }

    /// <summary>
    /// Whether the engine can render this shape at all, judged from its material alpha
    /// (<c>BSLightingShaderProperty.alpha</c>) together with its <c>NiAlphaProperty</c>.
    ///
    /// <para>A material alpha of 0 is how an author neutralises hair geometry left baked into a
    /// FaceGen head: the shape stays in the NIF but contributes nothing, and the visible hair comes
    /// from a worn wig instead. Specimen — High Poly NPC Overhaul builds every NPC as bald head part
    /// plus wig, so 3021 of its 3025 FaceGen heads carry no hair at all; the four that do (Adrianne
    /// Avenicci's <c>00013bb9.nif</c> keeps <c>0Victorian</c>/<c>0VictorianHL</c>, textured from a
    /// hair the mod no longer ships) are export leftovers hidden this way, while the wig actually
    /// worn is a different hair entirely. Rare, not a convention: a 35-NIF sample across six other
    /// appearance mods contained no alpha-0 shape at all.</para>
    ///
    /// <para>Alpha 0 only hides a shape when an NiAlphaProperty makes the engine consult alpha in
    /// the first place, so both flavours count and both must be checked: with alpha BLEND the shape
    /// composites at zero opacity; with alpha TEST the tested value is material alpha times the
    /// texture's alpha, so 0 falls under any threshold (including the GREATER-than-0 case) and every
    /// fragment is discarded. With neither flag the shape draws in the opaque pass and its material
    /// alpha is ignored — that is a visible shape, so it is reported as drawn.</para>
    ///
    /// <para>Reads fail open: anything unreadable is treated as drawn, because the cost of a missed
    /// suppression is one extra warning line while the cost of a wrong suppression is silence about
    /// a real untextured mesh.</para>
    /// </summary>
    private static bool ShapeIsDrawnInGame(NifFile nif, INiShape shape)
    {
        const float invisible = 0.001f;
        try
        {
            if (nif.GetBlock<BSLightingShaderProperty>(shape.ShaderPropertyRef) is not { } shader) return true;
            if (shader.Alpha > invisible) return true;

            if (!shape.HasAlphaProperty) return true;
            if (shape.AlphaPropertyRef is null || nif.GetBlock(shape.AlphaPropertyRef) is not { } alphaProp)
                return true;

            ushort flags = alphaProp.Flags?.Value ?? 0;
            bool alphaBlend = (flags & 1) != 0;          // bit 0
            bool alphaTest = (flags & (1 << 9)) != 0;    // bit 9
            return !(alphaBlend || alphaTest);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Rewrites texture slot paths inside a NIF in place and saves it. Used by the Include-As-New
    /// asset isolation (<c>AssetHandler.PostProcessIsolatedCopy</c>): an isolated copy of a mesh
    /// must reference the isolated copies of the textures its mod ships, or the private mesh would
    /// keep pulling whatever wins the shared texture path. Matching is by the EXACT stored slot
    /// string (pass a case-insensitive map; the same texture can be spelled differently across
    /// shapes). Slots are read and written per shape (<see cref="NifEdit.GetTexturePathByIndex"/> /
    /// <see cref="NifEdit.SetTextureSlot"/>), so a texture set shared by several shapes is
    /// rewritten once per sharing shape and counted each time, as before. Returns the number of
    /// slots rewritten (0 = file untouched, no save).
    /// </summary>
    public static int RewriteTexturePaths(string nifPath, IReadOnlyDictionary<string, string> pathMap,
        Action<string>? log = null)
    {
        int rewritten = 0;
        var nif = TryLoad(nifPath);
        if (nif == null)
        {
            log?.Invoke($"RewriteTexturePaths: could not load '{nifPath}'.");
            return 0;
        }

        foreach (var shape in NifEdit.GetShapes(nif))
        {
            for (int slot = 0; slot < 9; slot++)
            {
                string tex = NifEdit.GetTexturePathByIndex(nif, shape, slot);
                if (string.IsNullOrWhiteSpace(tex)) continue;
                if (!pathMap.TryGetValue(tex, out var newPath) ||
                    string.Equals(tex, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                NifEdit.SetTextureSlot(nif, shape, newPath, slot);
                rewritten++;
            }
        }

        if (rewritten > 0)
        {
            int saveResult = Save(nif, nifPath);
            if (saveResult != 0)
            {
                log?.Invoke($"RewriteTexturePaths: save failed ({saveResult}) for '{nifPath}'.");
                return 0;
            }
        }

        return rewritten;
    }

    /// <summary>
    /// Deletes the named shapes from a NIF in place and saves it. Used by the
    /// wig-forwarding pipeline to strip the baked hair shape(s) from a copied
    /// FaceGen NIF after the hair head part is removed from the NPC record —
    /// otherwise the baked hair renders alongside the forwarded wig in game.
    /// Matching is by exact shape name (FaceGen shapes are named after their
    /// head part EditorIDs), case-insensitive. Modeled on SynthEBD's
    /// FaceGenPatcher.RemoveShapesByHeadPartType: collect first, then delete
    /// (DeleteShape re-indexes blocks). Returns the number of shapes removed
    /// (0 = file left untouched, no save).
    /// </summary>
    public static int RemoveShapesByName(string nifPath, IReadOnlyCollection<string> shapeNames,
        Action<string>? log = null)
    {
        var wanted = shapeNames as ISet<string> ??
                     new HashSet<string>(shapeNames, StringComparer.OrdinalIgnoreCase);
        var nif = TryLoad(nifPath, out int loadResult);
        if (nif == null)
        {
            log?.Invoke($"RemoveShapesByName: failed to load NIF (code {loadResult}): {nifPath}");
            return 0;
        }

        int removedCount = RemoveShapesByNameCore(nif, wanted, nifPath, log);
        if (removedCount == 0) return 0;

        int saveResult = Save(nif, nifPath);
        if (saveResult != 0)
        {
            log?.Invoke($"RemoveShapesByName: save FAILED (code {saveResult}): {nifPath}");
            return 0;
        }

        return removedCount;
    }

    /// <summary>
    /// Renames shapes in a FaceGen NIF in place and saves it. The engine reconciles an NPC's
    /// head parts against its baked FaceGen geometry BY NAME —
    /// <c>actor3D-&gt;GetObjectByName(headPart-&gt;formEditorID)</c> — so whenever this app mints a
    /// duplicate head part under a new EditorID (Include As New appends
    /// <c>_&lt;sourcePlugin&gt;</c>), the shape that head part refers to has to follow, or the
    /// pairing breaks and the NPC dark-faces.
    ///
    /// <para>Keys are the OLD shape names, values the new ones. Matching is by exact name,
    /// case-insensitive, like <see cref="RemoveShapesByName"/>. A rename is skipped when the
    /// target name is already taken in this file (renaming onto it would make two shapes
    /// indistinguishable to the by-name lookup) — the usual cause being a file that has already
    /// been renamed by an earlier pass. Returns the number of shapes renamed; 0 leaves the file
    /// untouched with no save.</para>
    /// </summary>
    public static int RenameShapesByName(string nifPath, IReadOnlyDictionary<string, string> renames,
        Action<string>? log = null)
    {
        if (renames.Count == 0) return 0;

        var nif = TryLoad(nifPath, out int loadResult);
        if (nif == null)
        {
            log?.Invoke($"RenameShapesByName: failed to load NIF (code {loadResult}): {nifPath}");
            return 0;
        }

        // Collected before any rename so the "already taken" test sees the file's original
        // names, and so renaming does not disturb the enumeration.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toRename = new List<(INiShape Shape, string OldName, string NewName)>();
        int renamed = 0;
        foreach (var shape in NifEdit.GetShapes(nif))
        {
            string? name = shape.Name?.String;
            if (string.IsNullOrEmpty(name)) continue;
            present.Add(name);
            if (renames.TryGetValue(name, out var newName) &&
                !string.IsNullOrEmpty(newName) &&
                !string.Equals(name, newName, StringComparison.OrdinalIgnoreCase))
            {
                toRename.Add((shape, name, newName));
            }
        }

        foreach (var (shape, oldName, newName) in toRename)
        {
            if (present.Contains(newName))
            {
                log?.Invoke($"RenameShapesByName: '{oldName}' -> '{newName}' skipped in " +
                            $"{Path.GetFileName(nifPath)} — a shape is already named '{newName}'.");
                continue;
            }

            NifEdit.RenameShape(shape, newName);
            present.Remove(oldName);
            present.Add(newName);
            renamed++;
            log?.Invoke($"RenameShapesByName: '{oldName}' -> '{newName}' in {Path.GetFileName(nifPath)}");
        }

        if (renamed == 0) return 0;

        int saveResult = Save(nif, nifPath);
        if (saveResult != 0)
        {
            log?.Invoke($"RenameShapesByName: save FAILED (code {saveResult}): {nifPath}");
            return 0;
        }

        return renamed;
    }

    /// <summary>In-memory core of <see cref="RemoveShapesByName"/>: deletes the named
    /// shapes from an already-loaded NIF without saving. Shared with the wig bake,
    /// which strips the donor hair shapes and merges the wig in one load/save.</summary>
    private static int RemoveShapesByNameCore(NifFile nif, ISet<string> wanted, string nifPathForLog,
        Action<string>? log)
    {
        var toRemove = new List<(INiShape Shape, string Name)>();
        foreach (var shape in NifEdit.GetShapes(nif))
        {
            string? name = shape.Name?.String;
            if (string.IsNullOrEmpty(name)) continue;
            if (wanted.Contains(name))
            {
                toRemove.Add((shape, name));
            }
        }

        foreach (var (shape, name) in toRemove)
        {
            NifEdit.DeleteShape(nif, shape);
            log?.Invoke($"RemoveShapesByName: removed shape '{name}' from {Path.GetFileName(nifPathForLog)}");
        }

        return toRemove.Count;
    }

    /// <summary>
    /// The NiStringExtraData block name that HDT-SMP / FSMP uses to point a NIF at its
    /// physics XML config file. hdtSMP64 scanBBP() reads the XML path from the
    /// <c>stringData</c> of an extra-data block with this name (see hdtDefaultBBP.cpp).
    /// </summary>
    public const string SmpPhysicsExtraDataName = "HDT Skinned Mesh Physics Object";

    /// <summary>
    /// Scans a NIF for SMP/HDT physics XML references. A physics-enabled NIF carries an
    /// NiStringExtraData block whose string value is the (Data-relative) path to its physics
    /// XML. We collect that value when the block is named "HDT Skinned Mesh Physics Object"
    /// (the modern SMP marker) OR when its value simply ends in ".xml" (a robust catch-all for
    /// legacy/variant marker names such as the old "HDT Havok Path"). The same mechanism is
    /// used for hair, wigs, armor and body, so this single pass covers every physics type.
    /// Returns the raw string values exactly as stored in the NIF (caller normalizes/resolves).
    /// </summary>
    public static HashSet<string> GetPhysicsXmlPathsFromNif(string nifPath)
        => new HashSet<string>(ReadSummary(nifPath).PhysicsXmlPaths, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> ReadPhysicsXmlPaths(NifFile? nif)
    {
        HashSet<string> xmlPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (nif == null) return xmlPaths;

        foreach (var sed in nif.Blocks.OfType<NiStringExtraData>())
        {
            // Name lives on the NiExtraData base; StringData holds the XML path.
            string? blockName = sed.Name?.String;
            string? value = sed.StringData?.String;
            if (string.IsNullOrWhiteSpace(value)) continue;

            bool isPhysicsMarker = !string.IsNullOrEmpty(blockName) &&
                blockName.Equals(SmpPhysicsExtraDataName, StringComparison.OrdinalIgnoreCase);
            bool looksLikeXml = value.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

            if (isPhysicsMarker || looksLikeXml)
            {
                xmlPaths.Add(value.Trim());
            }
        }
        return xmlPaths;
    }

    /// <summary>The FaceGen skin node every baked head shape must live under. The
    /// engine (and SynthEBD's FaceGenPatcher, which this mirrors) treats shapes
    /// under this node as the NPC's head geometry.</summary>
    public const string FaceGenSkinNodeName = "BSFaceGenNiNodeSkinned";

    /// <summary>
    /// One wig→FaceGen bake operation. <see cref="FaceGenNifPath"/> is edited in
    /// place (callers operate on a copy of the donor FaceGen). Wig shapes are
    /// renamed via <see cref="ShapeRenames"/> (destination names become the minted
    /// HeadPart EditorIDs — the engine reconciles records against baked shapes by
    /// name). <see cref="ShapeNamesToStrip"/> removes the donor's baked hair first.
    /// <see cref="PhysicsXmlNewDataRelPath"/> repoints the wig's SMP extra-data to
    /// the rewritten physics XML (whose per-shape entries match the renames).
    /// </summary>
    /// <param name="OnlyRenderShapes">When non-null, bake ONLY these source
    /// render shapes (others are dropped from the merge like collision meshes).
    /// Diagnostic/bisect hook — null bakes every render shape.</param>
    /// <param name="SynthesizeHairPartitionIfNoDonor">When the partition-template
    /// harvest finds no donor hair shape (the WNAM skin-carried wig pattern:
    /// bald FaceGen + hair ARMA in the skin), synthesize the SBP_131_HAIR
    /// template via <see cref="CreateSyntheticHairPartitionTemplate"/> instead
    /// of baking partition-less (dark-face risk, invariant 4). Default off —
    /// the proven outfit-wig path always harvests.</param>
    /// <param name="ApplyHairTint">Whether to overwrite the <c>hairTintColor</c> of
    /// every baked wig shape that uses the HairTint shader with this NPC's hair
    /// color. Already decided by the caller: the wig→HeadPart converter resolves
    /// <see cref="Models.WigHairTintMode"/> for the NPC, including what RaceMenu
    /// would have done to the worn wig (<see cref="WigHairTintDecision"/>). The
    /// bake itself applies no rule of its own; in particular it does not look at
    /// the tint the wig already carries. False leaves the authored tint alone.</param>
    /// <param name="HairTintRgb">The NPC record's resolved HCLR as sRGB 0..1 —
    /// the space <c>hairTintColor</c> is stored in. Only a fallback: the hair
    /// color harvested from the donor FaceGen's own HairTint shapes wins, and
    /// this is used when the FaceGen carries none.</param>
    public sealed record WigBakeInstruction(
        string FaceGenNifPath,
        string WigNifPath,
        IReadOnlyDictionary<string, string> ShapeRenames,
        IReadOnlyCollection<string> ShapeNamesToStrip,
        string? PhysicsXmlNewDataRelPath,
        IReadOnlyCollection<string>? OnlyRenderShapes = null,
        bool SynthesizeHairPartitionIfNoDonor = false,
        bool ApplyHairTint = false,
        (float R, float G, float B)? HairTintRgb = null);

    /// <summary>
    /// Names of the render shapes (shapes carrying a shader property) in a NIF, in
    /// enumeration order. Shader-less shapes (SMP collision/virtual meshes) are
    /// excluded — they get no HeadPart record and keep their original names during
    /// a bake. The first entry is the designated parent HeadPart shape.
    /// </summary>
    public static IReadOnlyList<string> GetRenderShapeNames(string nifPath)
    {
        var names = new List<string>();
        var nif = TryLoad(nifPath);
        if (nif == null) return names;
        foreach (var shape in NifEdit.GetShapes(nif))
        {
            string? name = shape.Name?.String;
            if (string.IsNullOrEmpty(name)) continue;
            if (shape.HasShaderProperty) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Whether any of the named shapes in a NIF carries skin partition data
    /// (<see cref="NifEdit.GetShapePartitions"/> succeeds). Mirrors the partition-template
    /// harvest at the top of <see cref="BakeWigIntoFaceGen"/>: the bake
    /// transplants the donor hair's BSDismemberSkinInstance partition entry onto
    /// the wig shapes, and without one the baked shapes keep their source
    /// skin-instance types (dark-face risk — see the bake's invariant 4). The
    /// wig→HeadPart converter probes this BEFORE minting records so a
    /// partition-less donor can fall back to ForwardToSkin instead of shipping a
    /// risky bake.
    /// </summary>
    public static bool HasShapeWithPartitions(string nifPath, IReadOnlyCollection<string> shapeNames)
    {
        if (shapeNames.Count == 0) return false;
        var wanted = shapeNames as ISet<string> ??
                     new HashSet<string>(shapeNames, StringComparer.OrdinalIgnoreCase);
        var nif = TryLoad(nifPath);
        if (nif == null) return false;
        foreach (var shape in NifEdit.GetShapes(nif))
        {
            string? name = shape.Name?.String;
            if (string.IsNullOrEmpty(name) || !wanted.Contains(name)) continue;
            if (NifEdit.GetShapePartitions(nif, shape, out _)) return true;
        }
        return false;
    }

    /// <summary>BSLightingShaderProperty shader type for hair (BSLSP_HAIRTINT).
    /// The engine multiplies the diffuse by <c>hairTintColor</c> on these
    /// shapes and on no others.</summary>
    private const uint HairTintShaderType = 6;

    /// <summary>A baked tint at or above this on every channel counts as
    /// neutral white — a no-op multiply that says nothing about the NPC's hair
    /// color. Used only when HARVESTING a color from a FaceGen
    /// (<see cref="HarvestFaceGenHairTint"/>); it plays no part in deciding
    /// whether a converted wig is tinted.</summary>
    private const float NeutralTintThreshold = 0.98f;

    private static bool IsNeutralTint((float R, float G, float B) tint) =>
        tint.R >= NeutralTintThreshold &&
        tint.G >= NeutralTintThreshold &&
        tint.B >= NeutralTintThreshold;

    /// <summary>The shape's baked <c>hairTintColor</c>, or null when it carries
    /// no BSLightingShaderProperty or is not a HairTint shape.</summary>
    private static (float R, float G, float B)? TryGetHairTint(NifFile nif, INiShape shape)
    {
        if (nif.GetBlock<BSLightingShaderProperty>(shape.ShaderPropertyRef) is not { } bslsp) return null;
        if ((uint)bslsp.ShaderType_SK_FO4 != HairTintShaderType) return null;
        var tint = bslsp.HairTintColor;
        return (tint.R, tint.G, tint.B);
    }

    /// <summary>Writes <paramref name="rgb"/> into the shape's HairTint shader.
    /// No-op (returns false) for shapes that aren't HairTint.</summary>
    private static bool TrySetHairTint(NifFile nif, INiShape shape, (float R, float G, float B) rgb)
    {
        if (nif.GetBlock<BSLightingShaderProperty>(shape.ShaderPropertyRef) is not { } bslsp) return false;
        if ((uint)bslsp.ShaderType_SK_FO4 != HairTintShaderType) return false;
        bslsp.HairTintColor = new Color3 { R = rgb.R, G = rgb.G, B = rgb.B };
        return true;
    }

    /// <summary>
    /// Whether any render shape in a NIF uses the HairTint shader (BSLSP_HAIRTINT) — the only
    /// shapes a converted wig's hair tint is ever written to, so a wig without one is unaffected
    /// by <see cref="Models.WigHairTintMode"/> in every mode. Loads the NIF (one file read and
    /// parse per call, not cached); false when it cannot be loaded.
    /// </summary>
    public static bool HasHairTintShape(string nifPath)
    {
        if (string.IsNullOrWhiteSpace(nifPath) || !File.Exists(nifPath)) return false;
        var nif = TryLoad(nifPath);
        if (nif == null) return false;
        foreach (var shape in NifEdit.GetShapes(nif))
        {
            if (TryGetHairTint(nif, shape) != null) return true;
        }
        return false;
    }

    /// <summary>
    /// The hair color the CK baked into this FaceGen — read off its own HairTint
    /// shapes. <paramref name="preferredShapeNames"/> (the donor hair about to be
    /// stripped) wins; otherwise any HairTint shape does, which on a bald FaceGen
    /// means the brows or beard. Those carry the same hair color the CK writes to
    /// hair, so a converted wig tinted from them matches the rest of the NPC's
    /// hair-colored geometry. Neutral-white tints are skipped as uninformative.
    /// Must run BEFORE the donor hair strip.
    /// </summary>
    private static (float R, float G, float B)? HarvestFaceGenHairTint(
        NifFile faceGen, IReadOnlyCollection<string> preferredShapeNames, Action<string>? log = null)
    {
        var preferred = preferredShapeNames as ISet<string> ??
                        new HashSet<string>(preferredShapeNames, StringComparer.OrdinalIgnoreCase);
        (float R, float G, float B)? fallback = null;
        string fallbackName = string.Empty;

        foreach (var shape in NifEdit.GetShapes(faceGen))
        {
            var tint = TryGetHairTint(faceGen, shape);
            if (tint == null || IsNeutralTint(tint.Value)) continue;
            string name = shape.Name?.String ?? string.Empty;
            if (preferred.Contains(name))
            {
                log?.Invoke($"BakeWigIntoFaceGen: harvested hair tint " +
                            $"({tint.Value.R:F4}, {tint.Value.G:F4}, {tint.Value.B:F4}) from donor hair '{name}'.");
                return tint;
            }
            if (fallback == null)
            {
                fallback = tint;
                fallbackName = name;
            }
        }

        if (fallback != null)
        {
            log?.Invoke($"BakeWigIntoFaceGen: harvested hair tint " +
                        $"({fallback.Value.R:F4}, {fallback.Value.G:F4}, {fallback.Value.B:F4}) " +
                        $"from FaceGen HairTint shape '{fallbackName}' (no donor hair shape).");
        }
        return fallback;
    }

    /// <summary>
    /// Converts an HCLR record color to the FaceGen <c>hairTintColor</c> space:
    /// the CK bakes <b>2 ×</b> the hair color. Measured over the 344 High Poly
    /// NPC Overhaul FaceGens whose NPC has a resolvable HCLR — 336 are exactly
    /// 2× on all three channels (the 8 outliers are NPCs whose FaceGen was
    /// exported against a different hair color than the record now carries).
    /// Alvor is the clean case: <c>HairColor05DarkBlond</c> = (56, 59, 44) and
    /// his baked brow/beard tint is (112, 118, 88).
    ///
    /// <para>Baking the raw record value instead leaves a converted wig at half
    /// the brightness of the same NPC's beard and brows.</para>
    /// </summary>
    public static (float R, float G, float B) HclrToFaceGenTint((float R, float G, float B) hclr) =>
        (Math.Min(hclr.R * 2f, 1f), Math.Min(hclr.G * 2f, 1f), Math.Min(hclr.B * 2f, 1f));

    /// <summary>
    /// The hair color the CK baked into a FaceGen NIF, read off its HairTint
    /// shapes (hair if present, else brows/beard — the CK writes the NPC's hair
    /// color to all of them). Null when the file can't be loaded or carries no
    /// informative HairTint shape. Used by the renderer to tint a worn hair-slot
    /// wig the way RaceMenu does, matching what
    /// <see cref="BakeWigIntoFaceGen"/> would bake for the same NPC.
    /// </summary>
    public static (float R, float G, float B)? GetFaceGenHairTint(string faceGenNifPath)
    {
        if (string.IsNullOrWhiteSpace(faceGenNifPath) || !File.Exists(faceGenNifPath)) return null;
        var nif = TryLoad(faceGenNifPath);
        if (nif == null) return null;
        return HarvestFaceGenHairTint(nif, Array.Empty<string>());
    }

    /// <summary>SSE facegen hair dismember partition (SBP_131_HAIR) — the ID
    /// helmets key on to hide hair.</summary>
    private const ushort HairDismemberPartitionId = 131;

    /// <summary>First-partition flags: PF_EDITOR_VISIBLE (0x0001) |
    /// PF_START_NET_BONESET (0x0100).</summary>
    private const ushort HairDismemberPartitionFlags = 0x0101;

    /// <summary>
    /// Builds a synthetic single-partition dismember template for facegen hair,
    /// for bakes with no donor hair shape to harvest from (the WNAM skin-carried
    /// wig pattern: bald FaceGen + hair ARMA in the skin). Values verified
    /// against real facegen NIFs (2026-07-23, NifDump partition dump over the
    /// FoxGlove Auri facegen and two CK-generated replacer facegens — BB's
    /// Companions 00013BB6/0001A68E): every hair shape carries exactly one
    /// partition, partID=131, flags=0x0101; multi-partition head shapes carry
    /// 0x0101 on the FIRST entry and 0x0001 on the rest, so a single-entry
    /// template takes the start-net-boneset bit.
    /// </summary>
    internal static List<BodyPartList> CreateSyntheticHairPartitionTemplate() =>
    [
        new BodyPartList
        {
            PartFlag = (BSPartFlag)HairDismemberPartitionFlags,
            BodyPart = (BSDismemberBodyPartType)HairDismemberPartitionId
        }
    ];

    /// <summary>
    /// Merges an entire wig NIF scene into a FaceGen NIF (the wig→HeadPart
    /// conversion bake). Strips the donor hair shapes, then clones the wig's full
    /// block tree (render shapes, SMP bone-chain NiNodes with hierarchy intact,
    /// physics extra-data) into the FaceGen NIF via nifly's CloneChildren. Render
    /// shapes are reparented under BSFaceGenNiNodeSkinned and renamed per the
    /// instruction. Returns the number of render shapes baked (0 = failure, file
    /// untouched on disk).
    ///
    /// <para><b>Engine-verified FaceGen invariants</b> — established 2026-07 by an
    /// in-game bisect on the FoxGlove Auri specimen (variants A–H; each violation
    /// below INDIVIDUALLY reproduces the dark-face bug, i.e. the face renders
    /// with no tint while geometry and even SMP physics keep working):</para>
    /// <list type="number">
    /// <item><b>Record↔NIF reconciliation is by name, both directions.</b> The
    /// engine matches baked shape names against the EditorIDs of the NPC's
    /// resolved head parts (own parts + ExtraParts recursively + race defaults
    /// for unfilled slots). A geometry-bearing part with no baked shape dark-faces;
    /// an ORPHAN baked shape (no matching part) also dark-faces — so a strip
    /// list must cover a removed part's ExtraParts (hairline AND scalp).
    /// MODELESS parts are excluded from the expected set (why the modeless
    /// NPC2_HairBald works with nothing baked) — every part whose shape IS baked
    /// must therefore carry a Model, extra parts included.</item>
    /// <item><b>Baked head-part geometry must be BSDynamicTriShape.</b> Plain
    /// BSTriShape renders but is not accepted as facegen geometry by the tint
    /// pass. nifly cannot promote already-SSE shapes (OptimizeFor early-outs
    /// unless converting LE→SSE; SetShapeDynamic only flags legacy
    /// NiGeometryData), hence the manual rebuild in
    /// ConvertRenderShapesToDynamic.</item>
    /// <item><b>No duplicate node names, no stray skeleton bones.</b> The engine
    /// merges facegen nodes onto the actor skeleton BY NAME; a second
    /// "NPC Head [Head]" (or an unreferenced orphan block with that name)
    /// poisons the merge. Cloned nodes whose names already exist are grafted
    /// onto the existing node and deleted; subtrees no kept shape skins to
    /// (SMP collision/constraint anchors: breasts, clavicles, NPC Root) are
    /// dropped. Working reference: Project AHO's shipped SMP facegen — chain
    /// bones once each under the EXISTING NPC Head, nothing else.</item>
    /// <item><b>Skin data must match the CK pattern.</b> BSDismemberSkinInstance
    /// with hair-classified partitions (transplanted from the donor hair; nifly
    /// partition splits inherit the partition ID), GPU skin weights present in
    /// vertData (a Create()-rebuilt shape has ZERO weights and renders nowhere),
    /// and the NiSkinPartition's embedded vertex data rebuilt to the shape's
    /// layout via UpdateSkinPartitions.</item>
    /// <item><b>The engine does NOT validate baked shapes against the HDPT
    /// Model NIF.</b> Shape names and vertex counts may differ freely from the
    /// Model's contents (the shipping FoxGlove facegen is hand-built:
    /// 17833-vert baked hair vs an 8674-vert model named 's4studio_mesh_3').
    /// The Model's role is record-side only (geometry-bearing marker, player/
    /// RaceMenu use).</item>
    /// <item><b>HDT-SMP works on facegen-baked shapes.</b> Requirements: the
    /// "HDT Skinned Mesh Physics Object" NiStringExtraData at root pointing at
    /// the physics XML, plus the chain bones in-file. Collision/virtual meshes
    /// need NOT be baked (dropped here — no working facegen carries them); the
    /// XML tolerates entries for absent shapes, and its body-bone references
    /// resolve against the actor's runtime skeleton, not the NIF.</item>
    /// </list>
    ///
    /// <para><b>Library mechanics relied on here:</b> CloneChildren deep-clones
    /// with hierarchy intact and rewrites the SOURCE root's child/extra refs in
    /// place to the new destination ids (that rewrite is the readback used to
    /// find the clones). A pointer inside a cloned subtree is rebound only when it
    /// points at that subtree's top block, so skin-instance bone pointers keep
    /// their source ids and need a name-based SetShapeBoneIDList remap plus a
    /// skeleton-root repoint. Blocks are held by object, not by id: RemoveBlock
    /// renumbers, and Save re-sorts into canonical order. Duplicates and unkept
    /// subtrees are deleted explicitly; blocks left unreferenced by that are
    /// dropped at save.</para>
    /// </summary>
    public static int BakeWigIntoFaceGen(WigBakeInstruction ins, Action<string>? log = null)
    {
        var faceGen = TryLoad(ins.FaceGenNifPath);
        if (faceGen == null)
        {
            log?.Invoke($"BakeWigIntoFaceGen: failed to load FaceGen NIF: {ins.FaceGenNifPath}");
            return 0;
        }

        var wig = TryLoad(ins.WigNifPath);
        if (wig == null)
        {
            log?.Invoke($"BakeWigIntoFaceGen: failed to load wig NIF: {ins.WigNifPath}");
            return 0;
        }

        // 0a. Harvest the donor hair's dismember partition info BEFORE stripping.
        //     Every CK-baked facegen shape uses BSDismemberSkinInstance with
        //     hair-classified partitions (the same partition IDs helmets use to
        //     hide hair); the wig's armor shapes use plain NiSkinInstance, which
        //     no working facegen precedent carries. Transplanting the donor
        //     hair's partition entry normalizes the wig shapes to CK shape.
        List<BodyPartList>? hairPartitionTemplate = null;
        foreach (var stripName in ins.ShapeNamesToStrip)
        {
            var donorShape = FindShapeByName(faceGen, stripName);
            if (donorShape == null) continue;
            if (NifEdit.GetShapePartitions(faceGen, donorShape, out var template))
            {
                hairPartitionTemplate = template;
                log?.Invoke($"BakeWigIntoFaceGen: harvested dismember partition template from '{stripName}'.");
                break;
            }
        }
        if (hairPartitionTemplate == null && ins.SynthesizeHairPartitionIfNoDonor)
        {
            hairPartitionTemplate = CreateSyntheticHairPartitionTemplate();
            log?.Invoke("BakeWigIntoFaceGen: no donor hair shape with partitions; " +
                        "synthesized SBP_131_HAIR partition template.");
        }
        else if (hairPartitionTemplate == null)
        {
            log?.Invoke("BakeWigIntoFaceGen: WARNING - no donor hair shape with partitions found; " +
                        "wig shapes keep their source skin-instance types.");
        }

        // 0a-2. Find the hair color to bake into the wig, BEFORE the donor hair
        //       strip (the harvest fallback reads shapes the strip removes).
        //       WHETHER to tint was decided by the caller (ins.ApplyHairTint,
        //       see WigHairTintMode); this step only finds the color. Writing
        //       the NPC's hair color in is what the CK does when it exports
        //       FaceGen for a real hair part — which is exactly what the wig is
        //       becoming.
        //
        //       The FaceGen's OWN HairTint shapes win over the HCLR record: the
        //       CK wrote the same hair color to the hair, brows and beard, so
        //       harvesting from them makes the converted wig match the rest of
        //       that NPC's hair-colored geometry BY CONSTRUCTION. The record is
        //       only a fallback, because the two genuinely disagree when a mod
        //       later in the load order overrides the color record after the
        //       appearance mod exported its FaceGen (Alvor: FaceGen baked from
        //       HairColor05DarkBlond (56,59,44), winning override (54,41,37) —
        //       using the record gave him red hair over a dark-blond beard).
        (float R, float G, float B)? bakedHairTint = null;
        if (ins.ApplyHairTint)
        {
            bakedHairTint = HarvestFaceGenHairTint(faceGen, ins.ShapeNamesToStrip, log);
            if (bakedHairTint == null && ins.HairTintRgb != null)
            {
                bakedHairTint = HclrToFaceGenTint(ins.HairTintRgb.Value);
                log?.Invoke($"BakeWigIntoFaceGen: no FaceGen HairTint shape; hair tint from the NPC " +
                            $"record HCLR ({ins.HairTintRgb.Value.R:F4}, {ins.HairTintRgb.Value.G:F4}, " +
                            $"{ins.HairTintRgb.Value.B:F4}) x2 = ({bakedHairTint.Value.R:F4}, " +
                            $"{bakedHairTint.Value.G:F4}, {bakedHairTint.Value.B:F4}).");
            }
            else if (bakedHairTint == null)
            {
                log?.Invoke("BakeWigIntoFaceGen: the FaceGen carries no HairTint shape and the NPC has " +
                            "no hair color record; wig keeps its baked tint.");
            }
        }

        // 0b. Normalize the wig scene in memory (never saved back to its path):
        //     drop collision meshes, then promote render shapes to
        //     BSDynamicTriShape so the clone carries CK-shaped facegen geometry.
        DropShaderlessShapes(wig, log);
        if (ins.OnlyRenderShapes != null)
        {
            var keep = ins.OnlyRenderShapes as ISet<string> ??
                       new HashSet<string>(ins.OnlyRenderShapes, StringComparer.OrdinalIgnoreCase);
            var excluded = new List<(INiShape Shape, string Name)>();
            foreach (var shape in NifEdit.GetShapes(wig))
            {
                string? shapeName = shape.Name?.String;
                if (!string.IsNullOrEmpty(shapeName) && !keep.Contains(shapeName))
                {
                    excluded.Add((shape, shapeName));
                }
            }
            foreach (var (shape, shapeName) in excluded)
            {
                NifEdit.DeleteShape(wig, shape);
                log?.Invoke($"BakeWigIntoFaceGen: excluded render shape '{shapeName}' (OnlyRenderShapes bisect).");
            }
        }
        if (!ConvertRenderShapesToDynamic(wig, hairPartitionTemplate, log))
        {
            return 0;
        }

        // 1. Strip the donor's baked hair shapes first (same pass as the
        //    ForwardToSkin strip, but in-memory so it's one load/save).
        if (ins.ShapeNamesToStrip.Count > 0)
        {
            var wanted = ins.ShapeNamesToStrip as ISet<string> ??
                         new HashSet<string>(ins.ShapeNamesToStrip, StringComparer.OrdinalIgnoreCase);
            RemoveShapesByNameCore(faceGen, wanted, ins.FaceGenNifPath, log);
        }

        var destRoot = faceGen.GetRootNode();
        if (destRoot == null)
        {
            log?.Invoke("BakeWigIntoFaceGen: FaceGen NIF has no root node.");
            return 0;
        }
        NiNode skinNode = FindNodeByName(faceGen, FaceGenSkinNodeName) ?? destRoot;

        var srcRoot = wig.GetRootNode();
        if (srcRoot == null)
        {
            log?.Invoke("BakeWigIntoFaceGen: wig NIF has no root node.");
            return 0;
        }

        // Union of bone names the kept render shapes actually skin to. Drives
        // which cloned node subtrees the FaceGen needs: working SMP facegens
        // (Project AHO; engine-verified via the G1/H bisect) carry ONLY the
        // physics chain bones — no duplicate skeleton nodes, no body anchors.
        // A duplicate "NPC Head [Head]"-style node alone dark-faces the NPC
        // (the engine merges facegen nodes onto the skeleton by name).
        var boneUnion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shape in NifEdit.GetShapes(wig))
        {
            if (!shape.IsSkinned) continue;
            foreach (string boneName in wig.GetShapeBoneNames(shape)) boneUnion.Add(boneName);
        }

        // The FaceGen's own blocks, captured before the clone appends the wig's.
        var preCloneBlocks = new HashSet<INiObject>(faceGen.Blocks, ReferenceEqualityComparer.Instance);
        var preCloneNodes = faceGen.Blocks.OfType<NiNode>().ToList();

        // 2. Snapshot the wig root's child/extra-data slots BEFORE the clone:
        //    CloneChildren rewrites these refs in place to the new destination
        //    block IDs, which is how the cloned top-level blocks are found again
        //    afterwards — but the SOURCE ids must be captured now (needed to pair
        //    source shapes with their clones for the bone remap).
        var childSlots = SnapshotRefArray(srcRoot.Children);
        var extraSlots = SnapshotRefArray(srcRoot.ExtraDataList);

        // 3. Whole-tree merge. Every block referenced (recursively) from the wig
        //    root is cloned into the FaceGen NIF with hierarchy and strings
        //    intact; nothing in the destination references the clones yet.
        faceGen.CloneChildren(srcRoot, wig);

        // 4. Attach the cloned top-level blocks. Render shapes go under the
        //    FaceGen skin node. Cloned NODES are normalized to the working
        //    SMP-facegen pattern (Project AHO; confirmed by the G1/H bisect):
        //    - a node whose name already exists in the FaceGen (NPC Head,
        //      NPC Spine2, ...) has its children GRAFTED onto the existing
        //      node and the duplicate deleted — duplicate skeleton nodes
        //      dark-face the NPC;
        //    - a subtree none of the kept shapes skin to (collision/constraint
        //      anchors like breasts/clavicles) is dropped entirely;
        //    - everything else (the physics chain roots) attaches to the root.
        //    Ids are read and attached only here, before anything is removed, so
        //    they are still valid; every later step holds the blocks themselves.
        int bakedRenderShapes = 0;
        var clonedShapePairs = new List<(INiShape Src, INiShape Dest, bool IsRender)>();
        var duplicateNodes = new List<INiObject>();      // deleted alone (children grafted/shared)
        var unkeptSubtreeRoots = new List<INiObject>();  // deleted recursively
        for (int slot = 0; slot < childSlots.Count; slot++)
        {
            int srcId = childSlots[slot];
            if (srcId == NiRef.NPOS) continue;
            int destId = srcRoot.Children.GetBlockRef(slot);
            var destBlock = faceGen.GetBlock(destId);
            if (destBlock == null || preCloneBlocks.Contains(destBlock))
            {
                log?.Invoke($"BakeWigIntoFaceGen: WARNING - child slot {slot} was not rewritten to a cloned block (got {destId}); skipping.");
                continue;
            }

            var srcBlock = wig.GetBlock(srcId);

            if (srcBlock is INiShape srcShape && destBlock is INiShape destShape)
            {
                bool isRender = srcShape.HasShaderProperty;
                clonedShapePairs.Add((srcShape, destShape, isRender));
                if (isRender)
                {
                    skinNode.Children.AddBlockRef(destId);
                    bakedRenderShapes++;
                }
                else
                {
                    unkeptSubtreeRoots.Add(destBlock); // stray shader-less shape
                }
            }
            else if (destBlock is NiNode clonedNode)
            {
                var subtreeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectSubtreeNodeNames(faceGen, clonedNode, subtreeNames);
                if (!subtreeNames.Overlaps(boneUnion))
                {
                    unkeptSubtreeRoots.Add(clonedNode);
                    log?.Invoke($"BakeWigIntoFaceGen: dropping unneeded node subtree '{clonedNode.Name?.String}' " +
                                "(no kept shape skins to it).");
                    continue;
                }

                string? clonedName = clonedNode.Name?.String;
                NiNode? existing = null;
                if (!string.IsNullOrEmpty(clonedName))
                {
                    existing = preCloneNodes.FirstOrDefault(preNode =>
                        clonedName.Equals(preNode.Name?.String, StringComparison.OrdinalIgnoreCase));
                }

                if (existing != null)
                {
                    int grafted = 0;
                    foreach (int childId in clonedNode.Children.Indices.ToList())
                    {
                        if (childId == NiRef.NPOS) continue;
                        existing.Children.AddBlockRef(childId);
                        grafted++;
                    }
                    duplicateNodes.Add(clonedNode);
                    log?.Invoke($"BakeWigIntoFaceGen: grafted {grafted} child(ren) of duplicate node " +
                                $"'{clonedName}' onto the existing FaceGen node.");
                }
                else
                {
                    destRoot.Children.AddBlockRef(destId);
                }
            }
            else
            {
                destRoot.Children.AddBlockRef(destId);
            }
        }

        // 5. Rename render shapes to their minted HeadPart EditorIDs.
        foreach (var (src, dest, isRender) in clonedShapePairs)
        {
            if (!isRender) continue;
            string? srcName = src.Name?.String;
            if (srcName != null && ins.ShapeRenames.TryGetValue(srcName, out var newName) &&
                !string.IsNullOrEmpty(newName))
            {
                NifEdit.RenameShape(dest, newName);
            }
        }

        // 5b. Re-tint the baked wig with this NPC's hair color (bakedHairTint is
        //     null unless the caller asked for it). Every HairTint shape of the
        //     wig is written, whatever tint it carried: RaceMenu, whose work on
        //     the worn wig this stands in for, does not look at the existing
        //     tint either. Only the CLONED shapes in this FaceGen copy are
        //     touched — the wig NIF itself is shared by every NPC wearing it
        //     and must keep its authored tint.
        if (bakedHairTint != null)
        {
            int tinted = 0;
            foreach (var (_, dest, isRender) in clonedShapePairs)
            {
                if (!isRender) continue;
                var current = TryGetHairTint(faceGen, dest);
                if (current == null) continue; // not a HairTint shape
                if (TrySetHairTint(faceGen, dest, bakedHairTint.Value))
                {
                    tinted++;
                    log?.Invoke($"BakeWigIntoFaceGen: hair tint '{dest.Name?.String}' " +
                                $"({current.Value.R:F4}, {current.Value.G:F4}, {current.Value.B:F4}) -> " +
                                $"({bakedHairTint.Value.R:F4}, {bakedHairTint.Value.G:F4}, {bakedHairTint.Value.B:F4}).");
                }
            }
            if (tinted == 0)
            {
                log?.Invoke("BakeWigIntoFaceGen: no HairTint shapes in the baked wig; nothing re-tinted.");
            }
        }

        // 6. Bone remap. The cloned skin instances still carry SOURCE block
        //    indices in their bone ptr lists (and skeleton root). Build a
        //    name→node map over the cloned NiNodes and rewrite each skinned
        //    shape's bone ID list in source order.
        // Pre-existing FaceGen nodes WIN name collisions: cloned duplicates of
        // skeleton nodes (NPC Head, NPC Spine2, ...) are deleted after the
        // graft, so skinning must resolve to the surviving originals. Chain
        // bones exist only as clones and resolve there.
        var nodesByName = new Dictionary<string, NiNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in preCloneNodes.Concat(faceGen.Blocks.OfType<NiNode>().Where(n => !preCloneBlocks.Contains(n))))
        {
            string? nodeName = node.Name?.String;
            if (!string.IsNullOrEmpty(nodeName)) nodesByName.TryAdd(nodeName, node);
        }

        faceGen.GetBlockIndex(destRoot, out int destRootId);
        foreach (var (src, dest, _) in clonedShapePairs)
        {
            if (!src.IsSkinned) continue;

            var destBoneIds = new List<int>();
            foreach (string boneName in wig.GetShapeBoneNames(src))
            {
                if (nodesByName.TryGetValue(boneName, out var boneNode) &&
                    faceGen.GetBlockIndex(boneNode, out int destBoneId))
                {
                    destBoneIds.Add(destBoneId);
                }
                else
                {
                    // No collapse fallback (SynthEBD's would silently kill SMP
                    // weights): a missing bone means the merge failed.
                    log?.Invoke($"BakeWigIntoFaceGen: ERROR - bone '{boneName}' of shape " +
                                $"'{src.Name?.String}' has no cloned node in the FaceGen NIF; aborting bake.");
                    return 0; // nothing saved; the on-disk FaceGen is untouched
                }
            }

            NifEdit.SetShapeBoneIDList(faceGen, dest, destBoneIds);

            // Skeleton root ptr is also a stale source index — repoint at the
            // FaceGen root (matches how the baked head shapes are rigged).
            if (faceGen.GetBlock<NiSkinInstance>(dest.SkinInstanceRef) is { } skinInst)
            {
                if (skinInst.SkeletonRoot is { } skeletonRoot) skeletonRoot.Index = destRootId;
                else skinInst.SkeletonRoot = new NiBlockPtr<NiNode>(destRootId);
            }
        }

        // 7. Physics extra-data: the clone carried the wig's NiStringExtraData
        //    across (it is a ref of the wig root). Attach it to the FaceGen root
        //    and repoint its value at the rewritten XML.
        for (int slot = 0; slot < extraSlots.Count; slot++)
        {
            if (extraSlots[slot] == NiRef.NPOS) continue;
            int destId = srcRoot.ExtraDataList.GetBlockRef(slot);
            var destBlock = faceGen.GetBlock(destId);
            if (destBlock == null || preCloneBlocks.Contains(destBlock)) continue;

            destRoot.ExtraDataList ??= new NiBlockRefArray<NiExtraData>();
            destRoot.ExtraDataList.AddBlockRef(destId);

            if (ins.PhysicsXmlNewDataRelPath != null && destBlock is NiStringExtraData sed)
            {
                string? sedName = sed.Name?.String;
                if (sedName != null &&
                    sedName.Equals(SmpPhysicsExtraDataName, StringComparison.OrdinalIgnoreCase))
                {
                    if (sed.StringData is { } stringData) stringData.String = ins.PhysicsXmlNewDataRelPath;
                    else sed.StringData = new NiStringRef(ins.PhysicsXmlNewDataRelPath);
                }
            }
        }

        // Delete the duplicate nodes (children survive via the graft) and the
        // unkept subtrees. Leaving them as orphan blocks is NOT safe: a stray
        // duplicate-named node poisons the engine's name-based facegen/skeleton
        // merge just like an attached one (G1 bisect).
        var deleteBlocks = new List<INiObject>(duplicateNodes);
        foreach (var subtreeRoot in unkeptSubtreeRoots)
        {
            CollectSubtreeBlocks(faceGen, subtreeRoot, deleteBlocks);
        }
        int deleted = NifEdit.DeleteBlocks(faceGen, deleteBlocks);
        if (deleted > 0)
        {
            log?.Invoke($"BakeWigIntoFaceGen: removed {deleted} duplicate/unneeded cloned block(s).");
        }

        // A provided-but-empty OnlyRenderShapes is the additions-only
        // diagnostic mode: clone just the bone tree + physics extra-data with
        // no shapes. Otherwise zero baked shapes means the bake failed.
        bool additionsOnly = ins.OnlyRenderShapes is { Count: 0 };
        if (bakedRenderShapes == 0 && !additionsOnly)
        {
            log?.Invoke($"BakeWigIntoFaceGen: no render shapes found in {ins.WigNifPath}; nothing baked.");
            return 0;
        }

        int saveResult = Save(faceGen, ins.FaceGenNifPath);
        if (saveResult != 0)
        {
            log?.Invoke($"BakeWigIntoFaceGen: save FAILED (code {saveResult}): {ins.FaceGenNifPath}");
            return 0;
        }

        log?.Invoke($"BakeWigIntoFaceGen: baked {bakedRenderShapes} render shape(s) from " +
                    $"{Path.GetFileName(ins.WigNifPath)} into {Path.GetFileName(ins.FaceGenNifPath)}.");
        return bakedRenderShapes;
    }

    /// <summary>Deletes every shape without a shader property from an in-memory
    /// NIF (SMP collision/virtual meshes). Collect-then-delete: DeleteShape
    /// re-indexes blocks.</summary>
    private static void DropShaderlessShapes(NifFile nif, Action<string>? log)
    {
        var doomed = new List<(INiShape Shape, string Name)>();
        foreach (var shape in NifEdit.GetShapes(nif))
        {
            if (!shape.HasShaderProperty)
            {
                doomed.Add((shape, shape.Name?.String ?? "(unnamed)"));
            }
        }
        foreach (var (shape, name) in doomed)
        {
            NifEdit.DeleteShape(nif, shape);
            log?.Invoke($"BakeWigIntoFaceGen: dropped shader-less shape '{name}' " +
                        "(SMP collision/virtual mesh — not facegen geometry).");
        }
    }

    /// <summary>
    /// Rebuilds every shader-bearing BSTriShape of an in-memory NIF as a
    /// BSDynamicTriShape (the block type the CK bakes into facegen NIFs and the
    /// engine's tint reconciliation expects). Neither nifly converts an
    /// already-SSE shape in place, so this rebuilds each one via Create
    /// (positions/triangles/UVs/normals), recalculates tangent space, and
    /// re-links the original shader/alpha/skin blocks by index before
    /// ReplaceBlock swaps it in under the same block id (keeping all parent
    /// childRefs valid). Create fills only the static vertex data; the dynamic
    /// position array is derived from it when the file is saved. The wig has no
    /// vertex colors / eye data / second UVs (verified on the specimen), so those
    /// attributes don't need carrying.
    /// </summary>
    private static bool ConvertRenderShapesToDynamic(NifFile nif,
        List<BodyPartList>? partitionTemplate, Action<string>? log)
    {
        var version = nif.Header.Version;

        var toConvert = NifEdit.GetShapes(nif)
            .Where(s => s.HasShaderProperty && s is not BSDynamicTriShape)
            .ToList();

        foreach (var shape in toConvert)
        {
            nif.GetBlockIndex(shape, out int id);
            if (shape is not BSTriShape src)
            {
                log?.Invoke($"BakeWigIntoFaceGen: ERROR - render shape block {id} is " +
                            $"{nif.Header.GetBlockTypeNameById(id)}, not BSTriShape; cannot convert. Aborting.");
                return false;
            }

            string name = src.Name?.String ?? string.Empty;
            var verts = src.VertexPositions;
            var uvs = src.UVs;
            var normals = src.HasNormals ? src.Normals : null;
            var tris = new List<Triangle>(src.Triangles ?? new List<Triangle>());

            // Capture the per-vertex GPU skin weights/bone indices BEFORE
            // ReplaceBlock drops the source block. Create() can't know them,
            // and an SSE skinned shape whose vertData weights are zero
            // collapses to nothing when GPU-skinned (all renderers draw
            // skinned shapes from this data via the partition).
            ushort numVerts = src.VertexCount;
            float[][]? vertWeights = null;
            byte[][]? vertWeightBones = null;
            if (src.IsSkinned)
            {
                vertWeights = new float[numVerts][];
                vertWeightBones = new byte[numVerts][];
                for (int v = 0; v < numVerts; v++)
                {
                    var w = new float[4];
                    var b = new byte[4];
                    if (src.VertexDataSSE is { } sse && v < sse.Count)
                    {
                        var item = sse[v];
                        for (int k = 0; k < 4; k++) { w[k] = (float)item.BoneWeights[k]; b[k] = item.BoneIndices[k]; }
                    }
                    else if (src.VertexData is { } data && v < data.Count)
                    {
                        var item = data[v];
                        for (int k = 0; k < 4; k++) { w[k] = (float)item.BoneWeights[k]; b[k] = item.BoneIndices[k]; }
                    }
                    vertWeights[v] = w;
                    vertWeightBones[v] = b;
                }
            }

            var dyn = new BSDynamicTriShape();
            dyn.Create(version, verts, tris, uvs, normals);
            // Not dyn.CalcTangentSpace(): Nifly's turns degenerate triangles into NaN tangents.
            // This rewrites every vertex's tangent data, including what Create's internal call set.
            NifEdit.CalcTangentSpace(dyn);
            dyn.Flags_ui = src.Flags_ui;
            dyn.Flags_us = src.Flags_us;
            dyn.Translation = src.Translation;
            dyn.Rotation = src.Rotation;
            dyn.Scale = src.Scale;
            dyn.Bounds = src.Bounds;

            if (src.ShaderPropertyRef is { } shaderRef && !shaderRef.IsEmpty())
                dyn.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(shaderRef.Index);
            if (src.AlphaPropertyRef is { } alphaRef && !alphaRef.IsEmpty())
                dyn.AlphaPropertyRef = new NiBlockRef<NiAlphaProperty>(alphaRef.Index);
            bool skinned = src.SkinInstanceRef is { } skinRef && !skinRef.IsEmpty();
            if (skinned)
            {
                dyn.IsSkinned = true;
                dyn.SkinInstanceRef = new NiBlockRef<NiObject>(src.SkinInstanceRef.Index);
            }

            nif.ReplaceBlock(id, dyn);
            NifEdit.RenameShape(dyn, name);

            if (skinned && vertWeights != null && vertWeightBones != null)
            {
                // Restore the captured GPU skin weights into the rebuilt
                // vertData (SetShapeVertWeights resolves the shape by name —
                // the conversion keeps the source name; EditorID renames
                // happen later in the facegen). Mirrors what nifly's own
                // LE→SSE headParts conversion does before repartitioning.
                // Resolved once: the per-vertex lookup is the same first-by-name shape.
                var weightTarget = NifEdit.FindShapeForVertWeights(nif, name);
                var boneIds = new List<byte>(4);
                var weightsVec = new List<float>(4);
                for (ushort v = 0; v < numVerts; v++)
                {
                    boneIds.Clear();
                    weightsVec.Clear();
                    for (int k = 0; k < 4; k++)
                    {
                        if (vertWeights[v][k] > 0f)
                        {
                            boneIds.Add(vertWeightBones[v][k]);
                            weightsVec.Add(vertWeights[v][k]);
                        }
                    }
                    if (weightsVec.Count == 0) continue;
                    NifEdit.SetShapeVertWeights(weightTarget, v, boneIds, weightsVec);
                }

                // Normalize the skin instance to the CK-baked shape:
                // BSDismemberSkinInstance carrying the donor hair's partition
                // classification, all triangles in that partition
                // (convertSkinInstance replaces a plain NiSkinInstance under the
                // same block id). UpdateSkinPartitions then rebuilds the
                // partition data — splitting by the 80-bone SSE limit with each
                // split inheriting the hair partition ID — and refreshes the
                // partition's embedded copy of the (dynamic-layout, weighted)
                // vertData, without which the shape renders nothing.
                if (partitionTemplate != null)
                {
                    var allFirstPartition = new int[tris.Count];
                    NifEdit.SetShapePartitions(nif, dyn, partitionTemplate, allFirstPartition, true);
                    NifEdit.RemoveEmptyPartitions(nif, dyn);
                }
                nif.UpdateSkinPartitions(dyn);
            }

            log?.Invoke($"BakeWigIntoFaceGen: converted '{name}' BSTriShape → BSDynamicTriShape.");
        }

        return true;
    }

    /// <summary>Collects all NiNode names in a cloned subtree (root inclusive).</summary>
    private static void CollectSubtreeNodeNames(NifFile nif, NiNode node, HashSet<string> names)
    {
        string? nodeName = node.Name?.String;
        if (!string.IsNullOrEmpty(nodeName)) names.Add(nodeName);
        if (node.Children == null) return;
        foreach (var childRef in node.Children.References)
        {
            if (nif.GetBlock(childRef) is NiNode child) CollectSubtreeNodeNames(nif, child, names);
        }
    }

    /// <summary>Collects the blocks of a subtree (root inclusive) for deletion.</summary>
    private static void CollectSubtreeBlocks(NifFile nif, INiObject block, List<INiObject> blocks)
    {
        blocks.Add(block);
        if (block is not NiNode node || node.Children == null) return;
        foreach (var childRef in node.Children.References)
        {
            if (nif.GetBlock(childRef) is { } child) CollectSubtreeBlocks(nif, child, blocks);
        }
    }

    /// <summary>Finds a shape by name (case-insensitive).</summary>
    private static INiShape? FindShapeByName(NifFile nif, string name) =>
        NifEdit.GetShapes(nif).FirstOrDefault(shape =>
            name.Equals(shape.Name?.String, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a NiNode by name (case-insensitive). Port of the lookup at
    /// the heart of SynthEBD FaceGenPatcher.FindFaceGenSkinNode.</summary>
    private static NiNode? FindNodeByName(NifFile nif, string nodeName) =>
        nif.Blocks.OfType<NiNode>().FirstOrDefault(node =>
            node.Name?.String is { } name && name.Equals(nodeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Captures the current ref indices of a block-ref array, slot by
    /// slot (empty refs come back as <see cref="NiRef.NPOS"/>). Used to pair
    /// pre-clone source ids with their post-clone destination rewrites.</summary>
    private static List<int> SnapshotRefArray(NiRefArray? refs) =>
        refs == null ? new List<int>() : refs.Indices.ToList();
}
