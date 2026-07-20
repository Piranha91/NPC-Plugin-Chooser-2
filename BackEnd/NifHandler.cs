using System.IO;
using nifly;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class NifHandler
{
    public static HashSet<string> GetExtraTexturesFromNif(string nifPath)
    {
        var debug = File.Exists(nifPath);
        HashSet<string> uniqueTextures = new HashSet<string>();
        using (NifFile nif = new NifFile())
        {
            nif.Load(nifPath);
            // Assume 'niFile' is your loaded NIF file object and niFile.Header is the NiHeader.
            NiHeader header = nif.GetHeader();

            var blockCount = header.GetNumBlocks();
            for (uint id = 0; id < blockCount; id++)
            {
                NiObject block = header.GetBlockById(id);
                if (block is BSShaderTextureSet textureSet)
                {
                    // Access the texture paths safely
                    using var texturesArray = textureSet.textures;           // textures is a container (e.g. NiTArray<NiString>)
                    using var textureItems = texturesArray.items();          // items() gives an enumerable collection of NiString
                    foreach (NiString tex in textureItems)
                    {
                        if (tex != null)
                        {
                            string path = tex.get();  // Get the actual string from NiString
                            if (!string.IsNullOrEmpty(path))
                            {
                                uniqueTextures.Add(path);
                            }
                        }
                        // (NiString will be disposed at end of using scope if required)
                    }
                }
            }
            return uniqueTextures;
        }
    }

    /// <summary>
    /// Deletes the named shapes from a NIF in place and saves it. Used by the
    /// wig-forwarding pipeline to strip the baked hair shape(s) from a copied
    /// FaceGen NIF after the hair head part is removed from the NPC record —
    /// otherwise the baked hair renders alongside the forwarded wig in game.
    /// Matching is by exact shape name (FaceGen shapes are named after their
    /// head part EditorIDs), case-insensitive. Modeled on SynthEBD's
    /// FaceGenPatcher.RemoveShapesByHeadPartType: collect first, then delete
    /// (DeleteShape re-indexes blocks), with the GetShapes scope held open
    /// across the deletes and the Save. Returns the number of shapes removed
    /// (0 = file left untouched, no save).
    /// </summary>
    public static int RemoveShapesByName(string nifPath, IReadOnlyCollection<string> shapeNames,
        Action<string>? log = null)
    {
        var wanted = shapeNames as ISet<string> ??
                     new HashSet<string>(shapeNames, StringComparer.OrdinalIgnoreCase);
        using NifFile nif = new NifFile();
        int loadResult = nif.Load(nifPath);
        if (loadResult != 0)
        {
            log?.Invoke($"RemoveShapesByName: failed to load NIF (code {loadResult}): {nifPath}");
            return 0;
        }

        var toRemove = new List<(NiShape Shape, string Name)>();
        using var shapes = nif.GetShapes();
        foreach (var shape in shapes)
        {
            string? name = shape.name?.get();
            if (string.IsNullOrEmpty(name)) continue;
            if (wanted.Contains(name))
            {
                toRemove.Add((shape, name));
            }
        }

        if (toRemove.Count == 0) return 0;

        foreach (var (shape, name) in toRemove)
        {
            nif.DeleteShape(shape);
            log?.Invoke($"RemoveShapesByName: removed shape '{name}' from {Path.GetFileName(nifPath)}");
        }

        int saveResult = nif.Save(nifPath);
        if (saveResult != 0)
        {
            log?.Invoke($"RemoveShapesByName: save FAILED (code {saveResult}): {nifPath}");
            return 0;
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
    {
        HashSet<string> xmlPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (NifFile nif = new NifFile())
        {
            nif.Load(nifPath);
            NiHeader header = nif.GetHeader();

            var blockCount = header.GetNumBlocks();
            for (uint id = 0; id < blockCount; id++)
            {
                NiObject block = header.GetBlockById(id);
                if (block is NiStringExtraData sed)
                {
                    // name lives on the NiExtraData base; stringData holds the XML path.
                    string? blockName = sed.name?.get();
                    string? value = sed.stringData?.get();
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    bool isPhysicsMarker = !string.IsNullOrEmpty(blockName) &&
                        blockName.Equals(SmpPhysicsExtraDataName, StringComparison.OrdinalIgnoreCase);
                    bool looksLikeXml = value.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

                    if (isPhysicsMarker || looksLikeXml)
                    {
                        xmlPaths.Add(value.Trim());
                    }
                }
            }
            return xmlPaths;
        }
    }
}