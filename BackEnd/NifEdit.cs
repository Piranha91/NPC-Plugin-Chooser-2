using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// The C++ nifly <c>NifFile</c> helpers NifHandler relied on through the old SWIG binding
/// (niflysharp 1.3.0, built from SteveTownsend/nifly tag v1.3.0 = 04de830) that NiflySharp
/// does not provide. Each method names the C++ function it reproduces and follows that
/// source, quirks included, so a file edited here comes out like one the old library wrote.
///
/// <para>Two NiflySharp rules bind every method: blocks are tracked by object, never by id
/// (RemoveBlock renumbers, Save re-sorts), and a struct held in a list is changed in place
/// through <see cref="CollectionsMarshal.AsSpan{T}"/> or read, modified and re-assigned.</para>
/// </summary>
internal static class NifEdit
{
    /// <summary>
    /// The shapes C++ <c>NifFile::GetShapes</c> returns: every <c>NiShape</c>, in block order.
    /// NiflySharp's <see cref="NifFile.GetShapes"/> also yields NiParticleSystem and its
    /// subclasses (an NiAVObject in C++, reached through NiParticles : NiGeometry here), plus
    /// BSGeometry and NiClod, which C++ v1.3.0 does not model at all.
    /// </summary>
    internal static IEnumerable<INiShape> GetShapes(NifFile nif) =>
        nif.GetShapes().Where(s => s is not (NiParticleSystem or BSGeometry or NiClod));

    private static readonly Regex Slashes = new(@"/+|\\+", RegexOptions.CultureInvariant);
    private static readonly Regex UpToTextures = new(@"^(.*?)\\textures\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingBackslashes = new(@"^\\+", RegexOptions.CultureInvariant);
    private static readonly Regex NoTexturesPrefix = new(@"^(?!^textures\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Port of <c>NifFile::TrimTexturePaths</c>, which the old library ran on every load
    /// (<c>PrepareData</c>, unless <c>preserveTexturePaths</c>; NiflySharp has the call commented
    /// out). Every texture path NPC2 read, and every path it saved back, went through it: slashes
    /// collapsed to single backslashes, everything up to the first <c>\textures\</c> dropped,
    /// leading backslashes dropped, and a relative path given a <c>textures\</c> prefix.
    ///
    /// <para>Kept as in C++: the BSEffectShaderProperty paths are trimmed only inside the
    /// texture-set branch, which an effect shader never reaches, so they stay as stored. The
    /// terrain-only <c>Data\</c> prefix is left out: NPC2 never loads a terrain file.</para>
    /// </summary>
    internal static void TrimTexturePaths(NifFile nif)
    {
        var version = nif.Header.Version;
        bool isSpecial = version.FileVersion == NiFileVersion.V10_0_1_0 && version.UserVersion == 0;
        bool prefixTextures = !version.IsOB() && !isSpecial;

        string Trim(string tex)
        {
            if (string.IsNullOrEmpty(tex)) return tex;
            tex = Slashes.Replace(tex, "\\");
            tex = UpToTextures.Replace(tex, "", 1);
            tex = LeadingBackslashes.Replace(tex, "");
            if (prefixTextures && !Path.IsPathFullyQualified(tex))
                tex = NoTexturesPrefix.Replace(tex, "textures\\", 1);
            return tex;
        }

        foreach (var shape in GetShapes(nif))
        {
            var shader = nif.GetShader(shape);
            if (shader != null)
            {
                var setRef = TextureSetRefOf(shader);
                var textureSet = setRef is null ? null : nif.GetBlock(setRef);
                if (textureSet?.Textures != null)
                {
                    foreach (var tex in textureSet.Textures)
                        if (tex != null) tex.Content = Trim(tex.Content);
                }
            }

            var texturing = GetTexturingProperty(nif, shape);
            if (texturing == null) continue;
            for (int slot = 0; slot <= 9; slot++)
            {
                var (has, desc) = TexturingSlot(texturing, slot);
                if (!has || !desc.HasValue) continue;
                var source = nif.GetBlock(desc.Value.Source);
                if (source == null) continue;
                SetSourcePath(nif, desc.Value, Trim(FilePathString(source.FileName)));
            }
        }
    }

    /// <summary>The texture set a shader owns. Read off the concrete classes: the
    /// <see cref="INiShader.TextureSetRef"/> default interface member returns null. These are the
    /// two C++ shader classes that override <c>TextureSetRef()</c>.</summary>
    private static NiBlockRef<BSShaderTextureSet>? TextureSetRefOf(INiShader shader) => shader switch
    {
        BSLightingShaderProperty lighting => lighting.TextureSetRef,
        BSShaderPPLightingProperty ppLighting => ppLighting.TextureSetRef,
        _ => null
    };

    /// <summary>Port of <c>NifFile::GetTexturingProperty</c> (Oblivion-era shapes).</summary>
    private static NiTexturingProperty? GetTexturingProperty(NifFile nif, INiShape shape)
    {
        if (shape.Properties == null) return null;
        foreach (var prop in shape.Properties.References)
        {
            var texturing = nif.GetBlock<NiTexturingProperty>(prop);
            if (texturing != null) return texturing;
        }
        return null;
    }

    private static string FilePathString(FilePath path)
    {
        string? fromTable = path.Index?.String;
        return !string.IsNullOrEmpty(fromTable) ? fromTable : path.String?.Content ?? string.Empty;
    }

    /// <summary>Port of <c>NifFile::GetTexturePathByIndex</c> → <c>GetTextureSlot</c>: the
    /// texture set, then (only when there is no texture set) a BSEffectShaderProperty's own
    /// slots 0/1/3/4/5, then a legacy NiTexturingProperty. Empty string when nothing is
    /// there.</summary>
    internal static string GetTexturePathByIndex(NifFile nif, INiShape shape, int texIndex)
    {
        var shader = nif.GetShader(shape);
        if (shader != null)
        {
            var setRef = TextureSetRefOf(shader);
            var textureSet = setRef is null ? null : nif.GetBlock(setRef);
            if (textureSet?.Textures != null && texIndex + 1 <= textureSet.Textures.Count)
                return textureSet.Textures[texIndex]?.Content ?? string.Empty;

            if (textureSet == null && shader is BSEffectShaderProperty effect)
            {
                var slot = texIndex switch
                {
                    0 => effect.SourceTexture,
                    1 => effect.NormalTexture,
                    3 => effect.GreyscaleTexture,
                    4 => effect.EnvMapTexture,
                    5 => effect.EnvMaskTexture,
                    _ => null
                };
                return slot?.Content ?? string.Empty;
            }
        }

        var texturing = GetTexturingProperty(nif, shape);
        if (texturing != null && texturing.TextureCount > texIndex)
        {
            var (has, desc) = TexturingSlot(texturing, texIndex);
            if (has && desc.HasValue)
            {
                var source = nif.GetBlock(desc.Value.Source);
                if (source != null)
                {
                    string path = FilePathString(source.FileName);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
        }

        return string.Empty;
    }

    private static (bool Has, TexDesc? Desc) TexturingSlot(NiTexturingProperty t, int texIndex) => texIndex switch
    {
        0 => (t.HasBaseTexture == true, t.BaseTexture),
        1 => (t.HasDarkTexture == true, t.DarkTexture),
        2 => (t.HasDetailTexture == true, t.DetailTexture),
        3 => (t.HasGlossTexture == true, t.GlossTexture),
        4 => (t.HasGlowTexture == true, t.GlowTexture),
        5 => (t.HasBumpMapTexture == true, t.BumpMapTexture),
        6 => (t.HasDecal0Texture == true, t.Decal0Texture),
        7 => (t.HasDecal1Texture == true, t.Decal1Texture),
        8 => (t.HasDecal2Texture == true, t.Decal2Texture),
        9 => (t.HasDecal3Texture == true, t.Decal3Texture),
        _ => (false, null)
    };

    /// <summary>Port of <c>NifFile::SetTextureSlot</c>. Same three branches as the read. The
    /// legacy branch keeps the C++ behaviour of setting <c>textureCount = texIndex + 1</c>.</summary>
    internal static void SetTextureSlot(NifFile nif, INiShape shape, string path, int texIndex)
    {
        var shader = nif.GetShader(shape);
        if (shader != null)
        {
            var setRef = TextureSetRefOf(shader);
            var textureSet = setRef is null ? null : nif.GetBlock(setRef);
            if (textureSet?.Textures != null && texIndex + 1 <= textureSet.Textures.Count)
            {
                if (textureSet.Textures[texIndex] is { } existing) existing.Content = path;
                else textureSet.Textures[texIndex] = new NiString4(path);
                return;
            }

            if (textureSet == null && shader is BSEffectShaderProperty effect)
            {
                switch (texIndex)
                {
                    case 0: effect.SourceTexture = WithContent(effect.SourceTexture, path); break;
                    case 1: effect.NormalTexture = WithContent(effect.NormalTexture, path); break;
                    case 3: effect.GreyscaleTexture = WithContent(effect.GreyscaleTexture, path); break;
                    case 4: effect.EnvMapTexture = WithContent(effect.EnvMapTexture, path); break;
                    case 5: effect.EnvMaskTexture = WithContent(effect.EnvMaskTexture, path); break;
                }
                return;
            }
        }

        var texturing = GetTexturingProperty(nif, shape);
        if (texturing == null) return;
        texturing.TextureCount = (uint)texIndex + 1;
        switch (texIndex)
        {
            case 0: texturing.HasBaseTexture = true; SetSourcePath(nif, texturing.BaseTexture, path); break;
            case 1: texturing.HasDarkTexture = true; SetSourcePath(nif, texturing.DarkTexture, path); break;
            case 2: texturing.HasDetailTexture = true; SetSourcePath(nif, texturing.DetailTexture, path); break;
            case 3: texturing.HasGlossTexture = true; SetSourcePath(nif, texturing.GlossTexture, path); break;
            case 4: texturing.HasGlowTexture = true; SetSourcePath(nif, texturing.GlowTexture, path); break;
            case 5: texturing.HasBumpMapTexture = true; SetSourcePath(nif, texturing.BumpMapTexture, path); break;
            case 6: texturing.HasDecal0Texture = true; SetSourcePath(nif, texturing.Decal0Texture, path); break;
            case 7: texturing.HasDecal1Texture = true; SetSourcePath(nif, texturing.Decal1Texture, path); break;
            case 8: texturing.HasDecal2Texture = true; SetSourcePath(nif, texturing.Decal2Texture, path); break;
            case 9: texturing.HasDecal3Texture = true; SetSourcePath(nif, texturing.Decal3Texture, path); break;
        }
    }

    private static NiString4 WithContent(NiString4? existing, string path)
    {
        if (existing == null) return new NiString4(path);
        existing.Content = path;
        return existing;
    }

    private static void SetSourcePath(NifFile nif, TexDesc desc, string path)
    {
        var source = nif.GetBlock(desc.Source);
        if (source == null) return;
        var fileName = source.FileName;
        if (fileName.Index is { } index) index.String = path;
        else fileName.Index = new NiStringRef(path);
        if (fileName.String is { } str) str.Content = path;
        else fileName.String = new NiString4(path);
        source.FileName = fileName;
    }

    /// <summary>C++ <c>Vector3::Normalize</c>: a zero vector stays zero. .NET's
    /// <see cref="Vector3.Normalize"/> turns it into NaN.</summary>
    private static Vector3 NormalizeLikeNifly(Vector3 v)
    {
        float d = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (d == 0f) d = 1f;
        return new Vector3(v.X / d, v.Y / d, v.Z / d);
    }

    private static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Vector3 Cross(Vector3 a, Vector3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>C++ <c>static_cast&lt;uint8_t&gt;(std::round(((c + 1) / 2) * 255))</c>, with
    /// std::round's halves-away-from-zero.</summary>
    private static sbyte PackUnit(float c) =>
        unchecked((sbyte)(byte)MathF.Round((c + 1f) / 2f * 255f, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Port of <c>BSTriShape::CalcTangentSpace</c>. NiflySharp's own version normalizes with
    /// <see cref="Vector3.Normalize"/>, which returns NaN for the zero direction a degenerate
    /// triangle (no area in position or UV) produces; the NaN then spreads to every vertex of
    /// that triangle and past the zero-vector fallback, and is written as the vertex's tangent
    /// and bitangent. The C++ normalize leaves a zero vector alone, so those vertices fall back
    /// to the normal-derived basis.
    /// </summary>
    internal static void CalcTangentSpace(BSTriShape shape)
    {
        if (!shape.HasNormals || !shape.HasUVs) return;
        var sse = shape.VertexDataSSE;
        var data = shape.VertexData;
        if (sse == null && data == null) return;

        var normals = shape.Normals;
        shape.HasTangents = true;

        int n = shape.VertexCount;
        var tan1 = new Vector3[n];
        var tan2 = new Vector3[n];

        Vector3 Position(int i) => sse != null
            ? sse[i].Vertex
            : shape.IsFullPrecision
                ? data![i].Vertex
                : new Vector3((float)data![i].VertexHalf.X, (float)data[i].VertexHalf.Y, (float)data[i].VertexHalf.Z);

        Vector2 Uv(int i) => sse != null
            ? new Vector2((float)sse[i].UV.U, (float)sse[i].UV.V)
            : new Vector2((float)data![i].UV.U, (float)data[i].UV.V);

        foreach (var triangle in shape.Triangles ?? new List<Triangle>())
        {
            int i1 = triangle.V1, i2 = triangle.V2, i3 = triangle.V3;
            if (i1 >= n || i2 >= n || i3 >= n) continue;

            Vector3 v1 = Position(i1), v2 = Position(i2), v3 = Position(i3);
            Vector2 w1 = Uv(i1), w2 = Uv(i2), w3 = Uv(i3);

            float x1 = v2.X - v1.X, x2 = v3.X - v1.X;
            float y1 = v2.Y - v1.Y, y2 = v3.Y - v1.Y;
            float z1 = v2.Z - v1.Z, z2 = v3.Z - v1.Z;

            float s1 = w2.X - w1.X, s2 = w3.X - w1.X;
            float t1 = w2.Y - w1.Y, t2 = w3.Y - w1.Y;

            float r = s1 * t2 - s2 * t1;
            r = r >= 0f ? +1f : -1f;

            var sdir = NormalizeLikeNifly(new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r));
            var tdir = NormalizeLikeNifly(new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r));

            tan1[i1] += tdir; tan1[i2] += tdir; tan1[i3] += tdir;
            tan2[i1] += sdir; tan2[i2] += sdir; tan2[i3] += sdir;
        }

        var sseSpan = sse != null ? CollectionsMarshal.AsSpan(sse) : default;
        var dataSpan = data != null ? CollectionsMarshal.AsSpan(data) : default;
        for (int i = 0; i < n; i++)
        {
            Vector3 normal = normals[i];
            Vector3 tangent = tan1[i];
            Vector3 bitangent = tan2[i];

            if (tangent == Vector3.Zero || bitangent == Vector3.Zero)
            {
                tangent = new Vector3(normal.Y, normal.Z, normal.X);
                bitangent = Cross(normal, tangent);
            }
            else
            {
                tangent = NormalizeLikeNifly(tangent);
                tangent = tangent - normal * Dot(normal, tangent);
                tangent = NormalizeLikeNifly(tangent);

                bitangent = NormalizeLikeNifly(bitangent);
                bitangent = bitangent - normal * Dot(normal, bitangent);
                bitangent = bitangent - tangent * Dot(tangent, bitangent);
                bitangent = NormalizeLikeNifly(bitangent);
            }

            if (sse != null)
            {
                ref var v = ref sseSpan[i];
                v.Tangent.X = PackUnit(tangent.X);
                v.Tangent.Y = PackUnit(tangent.Y);
                v.Tangent.Z = PackUnit(tangent.Z);
                v.BitangentX = bitangent.X;
                v.BitangentY = PackUnit(bitangent.Y);
                v.BitangentZ = PackUnit(bitangent.Z);
            }
            else
            {
                ref var v = ref dataSpan[i];
                v.Tangent.X = PackUnit(tangent.X);
                v.Tangent.Y = PackUnit(tangent.Y);
                v.Tangent.Z = PackUnit(tangent.Z);
                if (shape.IsFullPrecision) v.BitangentX = bitangent.X;
                else v.BitangentXHalf = (Half)bitangent.X;
                v.BitangentY = PackUnit(bitangent.Y);
                v.BitangentZ = PackUnit(bitangent.Z);
            }
        }
    }

    /// <summary>
    /// The bounds half of C++ <c>NifFile::Optimize</c>, which the old library's default
    /// <c>Save</c> ran: every shape's bounding sphere recomputed from its vertices. Both libraries
    /// compute it with Miniball, and both sometimes return a sphere that leaves vertices outside
    /// (measured over a quarter of the SE meshes: 56 shapes with the old library's float Miniball,
    /// 210 with Nifly's). A sphere that misses vertices lets the engine cull geometry that is
    /// on screen, so the radius is grown to the farthest vertex whenever one falls outside.
    /// </summary>
    internal static void UpdateBounds(NifFile nif)
    {
        foreach (var shape in GetShapes(nif))
        {
            if (shape is BSTriShape bsTriShape)
            {
                var verts = bsTriShape.VertexPositions ?? new List<Vector3>();
                bsTriShape.Bounds = ContainAll(new BoundingSphere(verts), verts);
            }
            else if (shape.GeometryData is { } data)
            {
                var verts = data.Vertices ?? new List<Vector3>();
                data.Bounds = ContainAll(new BoundingSphere(verts), verts);
            }
        }
    }

    private static BoundingSphere ContainAll(BoundingSphere sphere, List<Vector3> verts)
    {
        float radius = sphere.Radius;
        foreach (var v in verts)
        {
            float d = Vector3.Distance(v, sphere.Center);
            if (d > radius) radius = d;
        }
        return radius == sphere.Radius ? sphere : new BoundingSphere(sphere.Center, radius);
    }

    /// <summary>
    /// The block half of C++ <c>NifFile::Optimize</c> (<c>DeleteUnreferencedBlocks</c>): every
    /// block, root excepted, that nothing references or points at is deleted, repeatedly, until
    /// none is left. C++ and NiflySharp both rescan the whole file after each deletion and ask, per
    /// block, whether any block references it; in NiflySharp each such question rebuilds every
    /// block's reference list, which made it the largest cost of a save. Here every ref and
    /// pointer is counted once and zero-count blocks are taken off a worklist, decrementing what
    /// they reference, which reaches the same set: counts only fall, so a block either reaches zero
    /// in both or in neither. Nothing is deleted when the file has unknown blocks, as in C++.
    /// </summary>
    internal static int RemoveUnreferencedBlocks(NifFile nif)
    {
        if (nif.HasUnknownBlocks) return 0;
        var root = nif.GetRootNode();
        if (root == null || !nif.GetBlockIndex(root, out int rootIndex)) return 0;

        int n = nif.Blocks.Count;
        var counts = new int[n];
        var targets = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            var block = nif.Blocks[i];
            var list = new List<int>();
            foreach (var r in block.References.Concat(block.Pointers))
            {
                if (r == null || r.IsEmpty() || r.Index < 0 || r.Index >= n) continue;
                counts[r.Index]++;
                list.Add(r.Index);
            }
            targets[i] = list;
        }

        var removed = new bool[n];
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
            if (i != rootIndex && counts[i] == 0) queue.Enqueue(i);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (removed[i]) continue;
            removed[i] = true;
            foreach (int t in targets[i])
            {
                if (--counts[t] == 0 && t != rootIndex && !removed[t]) queue.Enqueue(t);
            }
        }

        return DeleteBlocks(nif, Enumerable.Range(0, n).Where(i => removed[i]).Select(i => nif.Blocks[i]).ToList());
    }

    /// <summary>Port of <c>NifFile::RenameShape</c>.</summary>
    internal static bool RenameShape(INiShape? shape, string newName)
    {
        if (shape == null) return false;
        if (shape.Name is { } name) name.String = newName;
        else if (shape is NiObjectNET named) named.Name = new NiStringRef(newName);
        return true;
    }

    /// <summary>
    /// Removes blocks in one pass, as C++ <c>NiHeader::DeleteBlock</c> would one by one: every ref
    /// and pointer to a removed block is cleared (its slot kept, as C++ <c>BlockDeleted</c> does)
    /// and every later index shifts down past the removed ones.
    ///
    /// <para>NiflySharp's <see cref="NifFile.RemoveBlock(int)"/> is not used. It decrements later
    /// refs in place and then calls <c>List.Remove</c> on the ref to the deleted block, and
    /// <c>NiRef.Equals</c> compares indices, so that Remove can take out a SIBLING ref that was just
    /// decremented onto the same index (1stpersonhunterhammer.nif in the USSEP lost EdgeBlood01 when
    /// its hammer shape was removed). It also walks every block's refs once per removed block,
    /// rebuilding each block's reference list each time.</para>
    /// </summary>
    internal static int DeleteBlocks(NifFile nif, IEnumerable<INiObject?> blocks)
    {
        var doomed = new SortedSet<int>();
        foreach (var block in blocks)
        {
            if (block is NiObject niObject && nif.GetBlockIndex(niObject, out int index)) doomed.Add(index);
        }
        if (doomed.Count == 0) return 0;

        var doomedSorted = doomed.ToArray();
        int NewIndex(int i)
        {
            int at = Array.BinarySearch(doomedSorted, i);
            return at >= 0 ? NiRef.NPOS : i - ~at;
        }

        for (int i = 0; i < nif.Blocks.Count; i++)
        {
            if (doomed.Contains(i)) continue;
            var block = nif.Blocks[i];
            foreach (var r in block.References.Concat(block.Pointers))
            {
                if (r == null || r.IsEmpty()) continue;
                int moved = NewIndex(r.Index);
                if (moved == NiRef.NPOS) r.Clear();
                else r.Index = moved;
            }
        }

        foreach (int index in doomed.Reverse())
        {
            nif.Header.RemoveBlockInfo(index);
            nif.Blocks.RemoveAt(index);
        }
        return doomed.Count;
    }

    internal static bool DeleteBlock(NifFile nif, INiObject? block) => DeleteBlocks(nif, new[] { block }) > 0;

    /// <summary>
    /// A run of C++ <c>DeleteBlock</c> calls, deferred: a block marked here reads as gone (its refs
    /// as cleared) for every later lookup and ref count, exactly as after a real deletion, and
    /// <see cref="Commit"/> removes them all in one pass.
    /// </summary>
    private sealed class Deletion
    {
        private readonly NifFile _nif;
        private readonly HashSet<INiObject> _doomed = new(ReferenceEqualityComparer.Instance);
        private Dictionary<INiObject, List<INiObject>>? _referrers;

        public Deletion(NifFile nif) => _nif = nif;

        public INiObject? Resolve(INiRef? r)
        {
            var block = _nif.GetBlock(r);
            return block != null && _doomed.Contains(block) ? null : block;
        }

        public void Delete(INiObject? block)
        {
            if (block != null) _doomed.Add(block);
        }

        /// <summary>Port of <c>NiHeader::GetBlockRefCount(id, includePtrs: false)</c> over the
        /// blocks not yet deleted.</summary>
        public int RefCount(INiObject? target)
        {
            if (target == null || _doomed.Contains(target)) return 0;
            if (_referrers == null)
            {
                _referrers = new Dictionary<INiObject, List<INiObject>>(ReferenceEqualityComparer.Instance);
                foreach (var block in _nif.Blocks)
                {
                    foreach (var r in block.References)
                    {
                        if (r == null || r.IsEmpty() || _nif.GetBlock(r) is not { } t) continue;
                        if (!_referrers.TryGetValue(t, out var list)) _referrers[t] = list = new List<INiObject>();
                        list.Add(block);
                    }
                }
            }
            return _referrers.TryGetValue(target, out var refs) ? refs.Count(b => !_doomed.Contains(b)) : 0;
        }

        /// <summary>C++ <c>NifFile::GetShader</c> as seen after the deletions so far.</summary>
        public INiShader? GetShader(INiShape shape)
        {
            if (Resolve(shape.ShaderPropertyRef) is INiShader shader) return shader;
            INiShader? fallback = null;
            if (shape.Properties == null) return null;
            foreach (var propRef in shape.Properties.References)
            {
                if (Resolve(propRef) is not INiShader prop) continue;
                if (prop is not NiMaterialProperty) return prop;
                fallback = prop;
            }
            return fallback;
        }

        public int Commit() => DeleteBlocks(_nif, _doomed);
    }

    /// <summary>
    /// Port of <c>NifFile::DeleteShape</c> (with <c>DeleteShader</c>, <c>DeleteSkinning</c> and
    /// <c>RemoveAlphaProperty</c>). Kept as in C++: the shader and its texture set are deleted
    /// only when this shape is their sole referrer, but the alpha property is deleted
    /// unconditionally, so a shape sharing it loses its alpha ref too.
    /// </summary>
    internal static void DeleteShape(NifFile nif, INiShape? shape)
    {
        if (shape == null) return;
        var del = new Deletion(nif);

        if (shape.HasData) del.Delete(del.Resolve(shape.DataRef));

        if (shape.HasShaderProperty && del.RefCount(del.Resolve(shape.ShaderPropertyRef)) == 1)
        {
            DeleteShader(del, shape);
        }

        DeleteSkinning(del, shape);

        if (shape.Properties != null)
        {
            foreach (var prop in shape.Properties.References.Select(r => del.Resolve(r)).Reverse().ToList())
                del.Delete(prop);
        }

        if (shape.ExtraDataList != null)
        {
            foreach (var extra in shape.ExtraDataList.References.Select(r => del.Resolve(r)).Reverse().ToList())
                del.Delete(extra);
        }

        del.Delete(shape);
        del.Commit();
    }

    /// <summary>Port of <c>NifFile::DeleteShader</c>.</summary>
    private static void DeleteShader(Deletion del, INiShape shape)
    {
        if (del.Resolve(shape.ShaderPropertyRef) is INiShader shader)
        {
            DeleteShaderWithTextureSet(del, shader);
            shape.ShaderPropertyRef?.Clear();
        }

        RemoveAlphaProperty(del, shape);

        if (shape.Properties == null) return;
        // C++ treats NiMaterialProperty as an NiShader; NiflySharp does not, and it has no
        // texture set, so it takes the same path with an empty one.
        foreach (var prop in shape.Properties.References.Select(r => del.Resolve(r)).ToList())
        {
            if (prop is BSShaderPPLightingProperty or NiMaterialProperty)
                DeleteShaderWithTextureSet(del, prop);
        }
    }

    private static void DeleteShaderWithTextureSet(Deletion del, INiObject shader)
    {
        var setRef = shader is INiShader niShader ? TextureSetRefOf(niShader) : null;
        if (setRef is not null && !setRef.IsEmpty())
        {
            var textureSet = del.Resolve(setRef);
            if (del.RefCount(textureSet) == 1) del.Delete(textureSet);
        }

        if (shader is NiObjectNET net && net.Controller is { } controller)
            del.Delete(del.Resolve(controller));

        del.Delete(shader);
    }

    /// <summary>Port of <c>NifFile::RemoveAlphaProperty</c>: no ref-count check.</summary>
    private static void RemoveAlphaProperty(Deletion del, INiShape shape)
    {
        if (del.Resolve(shape.AlphaPropertyRef) is { } alpha)
        {
            del.Delete(alpha);
            shape.AlphaPropertyRef?.Clear();
        }

        if (shape.Properties == null) return;
        foreach (var prop in shape.Properties.References.Select(r => del.Resolve(r)).ToList())
        {
            if (prop is NiAlphaProperty) del.Delete(prop);
        }
    }

    /// <summary>Port of <c>NifFile::DeleteSkinning</c>. As in C++, a shader this shape still
    /// references (one shared with another shape, which DeleteShape keeps) loses its skinned
    /// flag.</summary>
    private static void DeleteSkinning(Deletion del, INiShape shape)
    {
        if (del.Resolve(shape.SkinInstanceRef) is NiSkinInstance skinInst)
        {
            del.Delete(del.Resolve(skinInst.Data));
            del.Delete(del.Resolve(skinInst.SkinPartition));
            if (shape.HasSkinInstance)
            {
                del.Delete(skinInst);
                shape.SkinInstanceRef?.Clear();
            }
        }

        if (del.Resolve(shape.SkinInstanceRef) is BSSkin_Instance bsSkinInst)
        {
            del.Delete(del.Resolve(bsSkinInst.Data));
            if (shape.HasSkinInstance)
            {
                del.Delete(bsSkinInst);
                shape.SkinInstanceRef?.Clear();
            }
        }

        shape.IsSkinned = false;
        if (del.GetShader(shape) is BSShaderProperty shader) shader.Skinned = false;
    }

    private static BodyPartList DefaultPartitionInfo(NifFile nif) => new()
    {
        PartFlag = BSPartFlag.PF_EDITOR_VISIBLE,
        BodyPart = (BSDismemberBodyPartType)(nif.Header.Version.UserVersion >= 12 ? 32 : 0)
    };

    /// <summary>
    /// Port of <c>NifFile::GetShapePartitions</c>. True when the shape has an NiSkinInstance
    /// (any kind, dismember or not) with an NiSkinPartition. <paramref name="partitionInfo"/> is a
    /// copy of the BSDismemberSkinInstance partition list, padded with
    /// {EDITOR_VISIBLE, 32 (SSE) / 0} entries up to the partition count.
    /// </summary>
    internal static bool GetShapePartitions(NifFile nif, INiShape? shape, out List<BodyPartList> partitionInfo)
    {
        partitionInfo = new List<BodyPartList>();
        if (shape == null) return false;

        var bsdSkinInst = nif.GetBlock<BSDismemberSkinInstance>(shape.SkinInstanceRef);
        if (bsdSkinInst?.Partitions != null) partitionInfo = new List<BodyPartList>(bsdSkinInst.Partitions);

        var skinInst = nif.GetBlock<NiSkinInstance>(shape.SkinInstanceRef);
        if (skinInst == null) return false;

        var skinPart = nif.GetBlock(skinInst.SkinPartition);
        if (skinPart == null) return false;

        skinPart.PrepareTriParts(shape.Triangles ?? new List<Triangle>());

        int partitionCount = skinPart.Partitions?.Count ?? 0;
        while (partitionInfo.Count < partitionCount) partitionInfo.Add(DefaultPartitionInfo(nif));

        return true;
    }

    /// <summary>
    /// Port of <c>NifFile::SetShapePartitions</c>. NiflySharp keeps the per-triangle partition
    /// cache (<c>triParts</c>) internal, so the triangles are distributed into each partition's
    /// <see cref="SkinPartition.TrianglesCopy"/> directly (C++
    /// <c>GenerateTrueTrianglesFromTriParts</c>) and the cache is then rebuilt from them with the
    /// public <see cref="NiSkinPartition.GenerateTriPartsFromTrueTriangles"/>. That rebuild
    /// matters: load fills the cache, and <c>UpdateSkinPartitions</c> trusts any cache whose size
    /// matches the shape's triangle count.
    /// </summary>
    internal static void SetShapePartitions(NifFile nif, INiShape? shape, List<BodyPartList> partitionInfo,
        IReadOnlyList<int> triParts, bool convertSkinInstance)
    {
        if (shape == null) return;

        var skinInst = nif.GetBlock<NiSkinInstance>(shape.SkinInstanceRef);
        if (skinInst == null) return;

        var skinPart = nif.GetBlock(skinInst.SkinPartition);
        if (skinPart == null) return;

        int numParts = partitionInfo.Count;
        bool hasUnassignedTris = false;
        foreach (int pi in triParts)
        {
            if (pi >= numParts) numParts = pi + 1;
            if (pi < 0) hasUnassignedTris = true;
        }
        if (hasUnassignedTris) ++numParts;

        var assigned = new List<int>(triParts.Count);
        foreach (int pi in triParts) assigned.Add(pi < 0 ? numParts - 1 : pi);

        var partitions = skinPart.Partitions ?? new List<SkinPartition>();
        if (partitions.Count > numParts) partitions.RemoveRange(numParts, partitions.Count - numParts);
        while (partitions.Count < numParts) partitions.Add(new SkinPartition());
        var span = CollectionsMarshal.AsSpan(partitions);
        for (int i = 0; i < span.Length; i++) span[i].HasVertexMap = true;
        skinPart.Partitions = partitions;
        skinPart.NumPartitions = (uint)numParts;

        var shapeTris = shape.Triangles ?? new List<Triangle>();
        if (shapeTris.Count == assigned.Count)
        {
            for (int i = 0; i < span.Length; i++)
            {
                ref var p = ref span[i];
                p.TrianglesCopy ??= new List<Triangle>();
                p.TrianglesCopy.Clear();
                p.Triangles?.Clear();
                p.NumStrips = 0;
                p.Strips?.Clear();
                p.StripLengths?.Clear();
                p.HasFaces = true;
                p.VertexMap?.Clear();
                p.VertexWeights?.Clear();
                p.BoneIndices?.Clear();
            }

            for (int t = 0; t < shapeTris.Count; t++)
            {
                int partInd = assigned[t];
                if (partInd >= 0 && partInd < span.Length) span[partInd].TrianglesCopy.Add(shapeTris[t]);
            }

            for (int i = 0; i < span.Length; i++) span[i].NumTriangles = (ushort)span[i].TrianglesCopy.Count;
        }
        skinPart.GenerateTriPartsFromTrueTriangles(shapeTris);

        var bsdSkinInst = nif.GetBlock<BSDismemberSkinInstance>(shape.SkinInstanceRef);
        if (bsdSkinInst == null && convertSkinInstance && nif.GetBlockIndex(skinInst, out int skinIndex))
        {
            // C++ copies the NiSkinInstance base (*static_cast<NiSkinInstance*>(bsd) = *skinInst);
            // those are all of NiSkinInstance's members.
            bsdSkinInst = new BSDismemberSkinInstance
            {
                Data = skinInst.Data?.Clone(),
                SkinPartition = skinInst.SkinPartition?.Clone(),
                SkeletonRoot = skinInst.SkeletonRoot?.Clone(),
                Bones = skinInst.Bones?.Clone(),
                NumBones = skinInst.NumBones
            };
            nif.ReplaceBlock(skinIndex, bsdSkinInst);
        }

        if (bsdSkinInst != null)
        {
            var infos = new List<BodyPartList>(partitionInfo);
            while (infos.Count < numParts) infos.Add(DefaultPartitionInfo(nif));
            bsdSkinInst.Partitions = infos;
        }
    }

    /// <summary>Port of <c>NifFile::RemoveEmptyPartitions</c>: drops partitions with no
    /// triangles from the NiSkinPartition and the matching BSDismemberSkinInstance entries,
    /// then refreshes the partition flags.</summary>
    internal static void RemoveEmptyPartitions(NifFile nif, INiShape? shape)
    {
        if (shape == null) return;

        var skinInst = nif.GetBlock<NiSkinInstance>(shape.SkinInstanceRef);
        if (skinInst == null) return;

        var skinPart = nif.GetBlock(skinInst.SkinPartition);
        if (skinPart?.Partitions == null) return;

        var empty = new List<int>();
        for (int i = 0; i < skinPart.Partitions.Count; i++)
            if (skinPart.Partitions[i].NumTriangles == 0) empty.Add(i);
        if (empty.Count == 0) return;

        for (int k = empty.Count - 1; k >= 0; k--) skinPart.Partitions.RemoveAt(empty[k]);
        skinPart.NumPartitions = (uint)skinPart.Partitions.Count;

        // C++ collapses its triParts in place; the equivalent here is to rebuild the internal
        // cache from the surviving partitions' triangles.
        skinPart.PrepareTrueTriangles();
        skinPart.GenerateTriPartsFromTrueTriangles(shape.Triangles ?? new List<Triangle>());

        if (skinInst is BSDismemberSkinInstance bsdSkinInst && bsdSkinInst.Partitions != null)
        {
            for (int k = empty.Count - 1; k >= 0; k--)
                if (empty[k] < bsdSkinInst.Partitions.Count) bsdSkinInst.Partitions.RemoveAt(empty[k]);
            nif.UpdatePartitionFlags(shape);
        }
    }

    /// <summary>Port of <c>NifFile::SetShapeVertWeights</c>: resolves the FIRST shape with that
    /// exact name, and writes at most four bone/weight pairs, normalised by their sum.</summary>
    internal static void SetShapeVertWeights(NifFile nif, string shapeName, ushort vertIndex,
        IReadOnlyList<byte> boneIds, IReadOnlyList<float> weights) =>
        SetShapeVertWeights(FindShapeForVertWeights(nif, shapeName), vertIndex, boneIds, weights);

    /// <summary>The shape <see cref="SetShapeVertWeights(NifFile, string, ushort, IReadOnlyList{byte}, IReadOnlyList{float})"/>
    /// writes to, for a caller that sets many vertices of one shape and looks it up once.</summary>
    internal static INiShape? FindShapeForVertWeights(NifFile nif, string shapeName) =>
        GetShapes(nif).FirstOrDefault(s => s.Name?.String == shapeName);

    internal static void SetShapeVertWeights(INiShape? shape, ushort vertIndex,
        IReadOnlyList<byte> boneIds, IReadOnlyList<float> weights)
    {
        if (shape is not BSTriShape bsTriShape) return;

        float sum = 0f;
        foreach (float w in weights) sum += w;
        int num = Math.Min(weights.Count, 4);

        if (bsTriShape.VertexDataSSE is { } sse)
        {
            if (vertIndex >= sse.Count) return;
            ref var v = ref CollectionsMarshal.AsSpan(sse)[vertIndex];
            for (int i = 0; i < 4; i++) { v.BoneWeights[i] = Half.Zero; v.BoneIndices[i] = 0; }
            for (int i = 0; i < num; i++) { v.BoneIndices[i] = boneIds[i]; v.BoneWeights[i] = (Half)(weights[i] / sum); }
        }
        else if (bsTriShape.VertexData is { } data)
        {
            if (vertIndex >= data.Count) return;
            ref var v = ref CollectionsMarshal.AsSpan(data)[vertIndex];
            for (int i = 0; i < 4; i++) { v.BoneWeights[i] = Half.Zero; v.BoneIndices[i] = 0; }
            for (int i = 0; i < num; i++) { v.BoneIndices[i] = boneIds[i]; v.BoneWeights[i] = (Half)(weights[i] / sum); }
        }
    }

    /// <summary>Port of <c>NifFile::SetShapeBoneIDList</c>: replaces the skin instance's bone
    /// list, and resizes the BSSkin::BoneData / NiSkinData bone arrays when their count no
    /// longer matches (both cleared first, as in C++).</summary>
    internal static void SetShapeBoneIDList(NifFile nif, INiShape? shape, IReadOnlyList<int> boneIds)
    {
        if (shape == null) return;

        BSSkin_BoneData? boneData = null;
        if (shape is BSTriShape)
        {
            var bsSkin = nif.GetBlock<BSSkin_Instance>(shape.SkinInstanceRef);
            if (bsSkin != null) boneData = nif.GetBlock(bsSkin.Data);
        }

        var boneCont = nif.GetBlock<INiSkin>(shape.SkinInstanceRef);
        if (boneCont == null) return;

        boneCont.Bones ??= new NiBlockPtrArray<NiNode>();
        boneCont.Bones.Clear();
        foreach (int id in boneIds) boneCont.Bones.AddBlockRef(id);
        boneCont.NumBones = (uint)boneCont.Bones.Count;

        if (boneData != null && (boneData.BoneList?.Count ?? 0) != boneIds.Count)
        {
            boneData.BoneList = Enumerable.Range(0, boneIds.Count).Select(_ => new BSSkinBoneTrans()).ToList();
            boneData.NumBones = (uint)boneIds.Count;
        }

        if (boneCont is NiSkinInstance skinInst)
        {
            var skinData = nif.GetBlock(skinInst.Data);
            if (skinData != null && (skinData.BoneList?.Count ?? 0) != boneIds.Count)
            {
                skinData.BoneList = Enumerable.Range(0, boneIds.Count).Select(_ => new BoneData()).ToList();
                skinData.NumBones = (uint)boneIds.Count;
            }
        }
    }
}
