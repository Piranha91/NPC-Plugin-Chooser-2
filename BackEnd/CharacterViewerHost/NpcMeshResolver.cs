using System;
using System.Collections.Generic;
using System.IO;
using CharacterViewer.Rendering;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Given an NPC FormKey + LinkCache, resolves all mesh file paths needed to
/// render the NPC: body, hands, feet, head (FaceGen), skeleton, plus FaceTint
/// DDS, NPC weight/height, hair color, QNAM TextureLighting, and ARMA SkinTexture
/// TXST overrides. All paths are Data-relative (e.g. "meshes\actors\character\…").
/// Ported from SynthEBD's NpcMeshResolver.
/// </summary>
public class NpcMeshResolver
{
    private readonly ICharacterViewerLogger _logger;
    private readonly CharacterViewerLogGate _logGate;

    public NpcMeshResolver(ICharacterViewerLogger logger, CharacterViewerLogGate logGate)
    {
        _logger = logger;
        _logGate = logGate;
    }

    private void LogVerbose(string message)
    {
        if (_logGate != null && _logGate.Verbose) _logger?.LogMessage(message);
    }

    /// <summary>
    /// Resolves the full set of paths for the NPC. Returns null if the NPC
    /// itself can't be resolved from the link cache.
    /// </summary>
    public ResolvedNpcMeshPaths? Resolve(FormKey npcFormKey, ILinkCache linkCache)
    {
        if (!linkCache.TryResolve<INpcGetter>(npcFormKey, out var npcGetter))
        {
            _logger?.LogError("CharacterViewer: Could not resolve NPC " + npcFormKey);
            return null;
        }

        var sex = Auxilliary.IsFemale(npcGetter) ? Sex.Female : Sex.Male;
        string npcName = npcGetter.Name?.String ?? npcGetter.EditorID ?? npcFormKey.ToString();
        LogVerbose("CharacterViewer: Resolving NPC " + npcName + " (" + npcFormKey + ")");

        FormKey? npcRaceKey = (npcGetter.Race != null && !npcGetter.Race.IsNull) ? npcGetter.Race.FormKey : null;

        var (armorGetter, armorSource) = ResolveWornArmor(npcGetter, linkCache, npcName);

        string? bodyPath = null;
        string? handsPath = null;
        string? feetPath = null;
        var chains = new Dictionary<string, string>();
        var txstTextures = new Dictionary<string, Dictionary<int, string>>();
        string sexLabel = sex == Sex.Female ? "Female" : "Male";

        if (armorGetter?.Armature != null)
        {
            foreach (var armaLink in armorGetter.Armature)
            {
                if (!linkCache.TryResolve<IArmorAddonGetter>(armaLink.FormKey, out var armaGetter)) continue;
                if (armaGetter.BodyTemplate == null) continue;
                if (!IsArmatureForRace(armaGetter, npcRaceKey))
                {
                    LogVerbose("CharacterViewer: Skipping Armature " + armaLink.FormKey +
                        " — race " + (npcRaceKey?.ToString() ?? "(none)") + " not in ARMA.Race/AdditionalRaces");
                    continue;
                }

                var flags = armaGetter.BodyTemplate.FirstPersonFlags;
                string? meshPath = GetWorldModelPath(armaGetter, sex);
                if (meshPath == null) continue;

                string meshFileName = Path.GetFileName(meshPath);
                var txstPaths = ResolveTxstTextures(armaGetter, sex, linkCache, armaLink.FormKey);

                if (bodyPath == null && flags.HasFlag(BipedObjectFlag.Body))
                {
                    bodyPath = meshPath;
                    chains["Body"] = npcName + " → " + armorSource + " → Armature(Body):" + armaLink.FormKey +
                        " → " + sexLabel + " → " + meshFileName;
                    LogVerbose("CharacterViewer: Armature[Body]=" + armaLink.FormKey + ", WorldModel=" + meshPath);
                    if (txstPaths.Count > 0) txstTextures["Body"] = txstPaths;
                }
                if (handsPath == null && flags.HasFlag(BipedObjectFlag.Hands))
                {
                    handsPath = meshPath;
                    chains["Hands"] = npcName + " → " + armorSource + " → Armature(Hands):" + armaLink.FormKey +
                        " → " + sexLabel + " → " + meshFileName;
                    LogVerbose("CharacterViewer: Armature[Hands]=" + armaLink.FormKey + ", WorldModel=" + meshPath);
                    if (txstPaths.Count > 0) txstTextures["Hands"] = txstPaths;
                }
                if (feetPath == null && flags.HasFlag(BipedObjectFlag.Feet))
                {
                    feetPath = meshPath;
                    chains["Feet"] = npcName + " → " + armorSource + " → Armature(Feet):" + armaLink.FormKey +
                        " → " + sexLabel + " → " + meshFileName;
                    LogVerbose("CharacterViewer: Armature[Feet]=" + armaLink.FormKey + ", WorldModel=" + meshPath);
                    if (txstPaths.Count > 0) txstTextures["Feet"] = txstPaths;
                }
            }
        }

        string headPath = BuildFaceGenPath(npcFormKey);
        chains["Head"] = npcName + " → FaceGen → " + Path.GetFileName(headPath);
        LogVerbose("CharacterViewer: FaceGen head mesh=" + headPath);

        string faceTintPath = BuildFaceTintPath(npcFormKey);
        string? skeletonPath = ResolveSkeletonPath(npcGetter, sex, linkCache);

        (float R, float G, float B)? textureLightingColor = null;
        var qnam = npcGetter.TextureLighting;
        if (qnam != null)
        {
            textureLightingColor = (qnam.Value.R / 255f, qnam.Value.G / 255f, qnam.Value.B / 255f);
            LogVerbose("CharacterViewer: TextureLighting (QNAM)=RGB(" +
                qnam.Value.R + ", " + qnam.Value.G + ", " + qnam.Value.B + ")");
        }

        int weight = Math.Clamp((int)npcGetter.Weight, 0, 100);
        float recordHeight = npcGetter.Height;
        float baseHeight = (float.IsFinite(recordHeight) && recordHeight > 0f) ? recordHeight : 1f;

        (float R, float G, float B)? hairRgb = null;
        if (npcGetter.HairColor != null && !npcGetter.HairColor.IsNull &&
            linkCache.TryResolve<IColorRecordGetter>(npcGetter.HairColor.FormKey, out var hclr))
        {
            var c = hclr.Color;
            hairRgb = (c.R / 255f, c.G / 255f, c.B / 255f);
        }

        return new ResolvedNpcMeshPaths
        {
            BodyMeshPath = bodyPath,
            HandsMeshPath = handsPath,
            FeetMeshPath = feetPath,
            HeadMeshPath = headPath,
            Sex = sex,
            SkeletonPath = skeletonPath,
            ResolutionChains = chains,
            TxstTextures = txstTextures,
            FaceTintPath = faceTintPath,
            TextureLightingColor = textureLightingColor,
            NpcWeight = weight,
            NpcBaseHeight = baseHeight,
            HairColorRgb = hairRgb,
        };
    }

    private (IArmorGetter? armor, string source) ResolveWornArmor(INpcGetter npcGetter, ILinkCache linkCache, string npcName)
    {
        if (npcGetter.WornArmor != null && !npcGetter.WornArmor.IsNull &&
            linkCache.TryResolve<IArmorGetter>(npcGetter.WornArmor.FormKey, out var armorGetter))
        {
            LogVerbose("CharacterViewer: WornArmor=" + npcGetter.WornArmor.FormKey);
            return (armorGetter, "WornArmor:" + npcGetter.WornArmor.FormKey);
        }

        if (npcGetter.Race != null && !npcGetter.Race.IsNull &&
            linkCache.TryResolve<IRaceGetter>(npcGetter.Race.FormKey, out var raceGetter) &&
            raceGetter.Skin != null && !raceGetter.Skin.IsNull &&
            linkCache.TryResolve<IArmorGetter>(raceGetter.Skin.FormKey, out var raceSkinArmor))
        {
            LogVerbose("CharacterViewer: No WornArmor for " + npcName + ", falling back to Race.Skin=" + raceGetter.Skin.FormKey);
            return (raceSkinArmor, "Race.Skin:" + raceGetter.Skin.FormKey);
        }

        LogVerbose("CharacterViewer: No WornArmor or Race.Skin found for " + npcName);
        return (null, "(none)");
    }

    private static bool IsArmatureForRace(IArmorAddonGetter armaGetter, FormKey? npcRaceKey)
    {
        if (npcRaceKey == null) return true;
        if (armaGetter.Race != null && !armaGetter.Race.IsNull &&
            armaGetter.Race.FormKey.Equals(npcRaceKey.Value)) return true;
        if (armaGetter.AdditionalRaces != null)
        {
            foreach (var addRace in armaGetter.AdditionalRaces)
            {
                if (!addRace.IsNull && addRace.FormKey.Equals(npcRaceKey.Value)) return true;
            }
        }
        return false;
    }

    private static string? GetWorldModelPath(IArmorAddonGetter armaGetter, Sex sex)
    {
        if (armaGetter.WorldModel == null) return null;
        var model = sex == Sex.Female ? armaGetter.WorldModel.Female : armaGetter.WorldModel.Male;
        if (model?.File == null) return null;
        string path = model.File.GivenPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase))
        {
            path = "meshes\\" + path;
        }
        return path;
    }

    private Dictionary<int, string> ResolveTxstTextures(
        IArmorAddonGetter armaGetter, Sex sex, ILinkCache linkCache, FormKey armaFormKey)
    {
        var result = new Dictionary<int, string>();
        try
        {
            if (armaGetter.SkinTexture == null) return result;
            var skinTextureLink = sex == Sex.Female ? armaGetter.SkinTexture.Female : armaGetter.SkinTexture.Male;
            if (skinTextureLink == null || skinTextureLink.IsNull) return result;
            if (!linkCache.TryResolve<ITextureSetGetter>(skinTextureLink.FormKey, out var txst))
            {
                LogVerbose("CharacterViewer: Could not resolve TXST " + skinTextureLink.FormKey + " from ARMA " + armaFormKey);
                return result;
            }

            void TryAdd(int slot, string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string fullPath = path;
                if (!fullPath.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) &&
                    !fullPath.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
                {
                    fullPath = "textures\\" + fullPath;
                }
                result[slot] = fullPath;
            }

            TryAdd(0, txst.Diffuse?.DataRelativePath.Path);
            TryAdd(1, txst.NormalOrGloss?.DataRelativePath.Path);
            TryAdd(2, txst.GlowOrDetailMap?.DataRelativePath.Path);
            TryAdd(3, txst.Height?.DataRelativePath.Path);
            TryAdd(4, txst.Environment?.DataRelativePath.Path);
            TryAdd(5, txst.Multilayer?.DataRelativePath.Path);
            TryAdd(7, txst.BacklightMaskOrSpecular?.DataRelativePath.Path);
        }
        catch (Exception ex)
        {
            LogVerbose("CharacterViewer: TXST resolution failed for ARMA " + armaFormKey + ": " + ex.Message);
        }
        return result;
    }

    private static string BuildFaceGenPath(FormKey formKey)
    {
        string plugin = formKey.ModKey.FileName;
        string formId = formKey.ID.ToString("X8");
        return "meshes\\actors\\character\\FaceGenData\\FaceGeom\\" + plugin + "\\" + formId + ".nif";
    }

    private static string BuildFaceTintPath(FormKey formKey)
    {
        string plugin = formKey.ModKey.FileName;
        string formId = formKey.ID.ToString("X8");
        return "textures\\actors\\character\\FaceGenData\\FaceTint\\" + plugin + "\\" + formId + ".dds";
    }

    private string? ResolveSkeletonPath(INpcGetter npcGetter, Sex sex, ILinkCache linkCache)
    {
        if (npcGetter.Race == null || npcGetter.Race.IsNull) return null;
        if (!linkCache.TryResolve<IRaceGetter>(npcGetter.Race.FormKey, out var raceGetter)) return null;
        if (raceGetter.SkeletalModel == null) return null;
        var skelModel = sex == Sex.Female ? raceGetter.SkeletalModel.Female : raceGetter.SkeletalModel.Male;
        if (skelModel?.File == null) return null;
        string path = skelModel.File.GivenPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase))
        {
            path = "meshes\\" + path;
        }
        LogVerbose("CharacterViewer: Skeleton=" + path + " (Race=" + npcGetter.Race.FormKey + ", " + sex + ")");
        return path;
    }
}
