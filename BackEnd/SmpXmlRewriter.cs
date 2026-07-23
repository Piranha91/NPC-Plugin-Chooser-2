using System.IO;
using System.Xml.Linq;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Rewrites an HDT-SMP physics XML for the wig→HeadPart bake. SMP addresses
/// mesh shapes BY NAME (per-triangle-shape / per-vertex-shape "name"
/// attributes must match the NIF shape names, see hdtSMP64's config parser),
/// so when the bake renames the wig's render shapes to their minted HeadPart
/// EditorIDs, the physics config's shape entries must be renamed in lockstep.
/// Collision/virtual shape entries (never renamed — they get no HeadPart) pass
/// through untouched, as does everything else in the config.
/// </summary>
public static class SmpXmlRewriter
{
    private static readonly string[] ShapeElementNames = { "per-triangle-shape", "per-vertex-shape" };

    /// <summary>
    /// Copies <paramref name="sourceXmlPath"/> to <paramref name="destXmlPath"/>,
    /// rewriting shape-element name attributes through <paramref name="shapeRenames"/>
    /// (source shape name → new baked shape name). Returns the number of shape
    /// entries renamed. Creates the destination directory as needed; the source
    /// file is never modified.
    /// </summary>
    public static int RewriteShapeNames(string sourceXmlPath, string destXmlPath,
        IReadOnlyDictionary<string, string> shapeRenames, Action<string>? log = null)
    {
        XDocument doc = XDocument.Load(sourceXmlPath, LoadOptions.PreserveWhitespace);

        int renamed = 0;
        foreach (var element in doc.Descendants())
        {
            bool isShapeElement = false;
            foreach (var shapeElementName in ShapeElementNames)
            {
                if (element.Name.LocalName.Equals(shapeElementName, StringComparison.OrdinalIgnoreCase))
                {
                    isShapeElement = true;
                    break;
                }
            }
            if (!isShapeElement) continue;

            var nameAttr = element.Attribute("name");
            if (nameAttr == null) continue;

            if (shapeRenames.TryGetValue(nameAttr.Value, out var newName) && !string.IsNullOrEmpty(newName))
            {
                log?.Invoke($"SmpXmlRewriter: {element.Name.LocalName} '{nameAttr.Value}' -> '{newName}'");
                nameAttr.Value = newName;
                renamed++;
            }
        }

        string? destDir = Path.GetDirectoryName(destXmlPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        doc.Save(destXmlPath);
        return renamed;
    }
}
