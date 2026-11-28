"""NPC2 in-game pre-release check: the single definition of the corpus, the subjects, the launches and the matrix.
What the bench is and how a run is reproduced: ../README.md. Everything else in this folder
(bench_profiles, bench_run, bench_driver, bench_score, bench_corpus) reads from here.

Launch kinds:
  ref   one or more appearance mods active by themselves (the answer key), or none ("V", vanilla)
  arm   NPC2 output only, built headless for that arm's settings (Tests/Integration/Lab/BenchOutputBuilder.cs)
"""
import copy, json, os, struct
import bench_paths as P

INSTANCE = P.INSTANCE
MODS_DIR = os.path.join(INSTANCE, "mods")
NPC2 = P.NPC2
LIVE_SETTINGS = os.path.join(NPC2, r"bin\Debug\net10.0-windows10.0.19041.0\Settings.json")
CORPUS = P.CORPUS
SNAPSHOT = os.path.join(CORPUS, "npc2-settings-snapshot.json")      # NPC2's own analysis of the corpus mods, frozen
# N2B_RUN_ROOT names another run folder (H5, 2026-11-27: the final full run starts in a fresh one); default unchanged.
RUN_ROOT = P.RUN_ROOT
HERE = os.path.dirname(os.path.abspath(__file__))
OUTPUT_PREFIX = "NPC2Bench Output"       # BenchOutputBuilder refuses any other folder name

TOOLS = ["SKSE - RootBuilder", "SKSE Scripts", "Address Library for SKSE Plugins", "Survival Mode Prompt Removed",
         "DevBench 1.39.0", "powerofthree's Tweaks", "SkyPatcher", "Recast 1.6.3"]
FIXTURE_BASE = "NPC2Bench - ArmaRace Base"
FIXTURE_MODA = "NPC2Bench - ArmaRace ModA"
# Active in every launch, reference and test alike: the mods that ADD a subject NPC, and two hard requirements of
# corpus mods found on the first build (2026-11-25): High Poly NPC Overhaul has the Unofficial Patch as a master
# (NPC2 rightly rejects it without), and UBE's races reference RaceCompatibility.esm. So "Vanilla" here means the
# base game plus those two, and "Patched" adds AI Overhaul.
USSEP = "Unofficial Skyrim Special Edition Patch - Latest AE Version"
BASE_MODS = [USSEP, "RaceCompatibility with fixes for SSE", "High Poly Head SE",      # High Poly Head.esm: High Poly's other master
             "Song of the Green (Auri Follower)", "Vilja in Skyrim", FIXTURE_BASE]
PATCHED = ["AI Overhaul SSE"]
# N14 (2026-11-27): "DX Druid Auri.esp" names the armour pieces by the ids of the COMPACTED "DX Druid Armor.esp"
# (0x800 and up). With the UNP mod's uncompacted plugin alone every item of the distributed outfit pointed at
# nothing: SPID logged "Outfit DXDruidAuriSummer is empty ... will appear naked", the game stripped Auri and the
# driver rightly took no picture (A9, A10 of the final run). The ESPFE mod holds only that compacted plugin; listed
# after the UNP mod it wins the plugin while the UNP mod supplies the meshes and textures (checked file by file).
CONTEST = ["Spell Perk Item Distributor (SPID)", "DX Druid Armor - UNP",
           "DX Druid Armor - SSE CBBE BodySlide (with Physics) - ESPFE", "DX Druid Armor for Auri (SPID)"]
SWAP_OUTFIT = ("02D75E", "Skyrim.esm")   # FarmClothesOutfit02: a full outfit with no headwear

# key -> NPC2 ModSetting DisplayName (folders and plugins are read from the settings snapshot)
MOD = {
    "OW": "The Ordinary Women SSE",
    "SoN": "Sons of Nirn - Whiterun",
    "CotG": "Children of the Green - A Bosmer NPC Overhaul",
    "HPNO": "High Poly NPC Overhaul - Skyrim Special Edition 2.0 (All Vanilla NPCs)",
    "Fox": "FoxGlove - Auri Visual Overhaul - The FoxGlove - Classic Red - No Warpaint (Default)",
    "SotG": "Song of the Green (Auri Follower)",
    "Sera": "Seranaholic by rxkx22 - SSE - 1.8 - Vampire-style",
    "NF": "Nordic Faces - FaceGen - With FaceTint",
    "BW": "Bijin Warmaidens",
    "SSSP": "Shields Sister Npc Replacer (Aela Njada Ria) SkyPatcher",
    "UBEL": "UBE Lydia - NPC Replacer",
    "ViljaRe": "Vilja Re-Imagined SSE",
    "ViljaSOGS": "SOGS - Vilja Visual Overhaul",
    "ViljaBase": "Vilja in Skyrim",
    "ModA": FIXTURE_MODA,
}
WIG_MODES = ["None", "ForwardToSkin", "ForwardToOutfit", "ConvertToHeadParts"]

# name: (form, plugin, mod key, tags). Adults only (no child NPC is ever launched, photographed or rendered).
S = {
    "Irileth": ("013BB8", "Skyrim.esm", "OW", ""), "Faralda": ("01C197", "Skyrim.esm", "OW", ""),
    "Carlotta": ("013B99", "Skyrim.esm", "OW", ""), "Saadia": ("013BA2", "Skyrim.esm", "OW", ""),
    "Sybille": ("0132AA", "Skyrim.esm", "OW", ""), "Danica": ("013BA5", "Skyrim.esm", "OW", ""),
    "Anoriath": ("013B97", "Skyrim.esm", "SoN", "antler"), "Elrindir": ("013B9E", "Skyrim.esm", "SoN", "antler"),
    "Belethor": ("013BA1", "Skyrim.esm", "SoN", ""), "Nazeem": ("013BBF", "Skyrim.esm", "SoN", ""),
    "Farkas": ("01A692", "Skyrim.esm", "SoN", ""), "Eorlund": ("013B9D", "Skyrim.esm", "SoN", ""),
    "Dervenin": ("01327C", "Skyrim.esm", "CotG", "antler"), "Beleval": ("01541C", "Dawnguard.esm", "CotG", "antler"),
    "Nivenor": ("01336F", "Skyrim.esm", "CotG", ""), "Anuriel": ("013349", "Skyrim.esm", "CotG", ""),
    "Ulfric": ("01414D", "Skyrim.esm", "HPNO", "wig g0"), "PitFan": ("0558F3", "Skyrim.esm", "HPNO", "wig g0"),
    "Cultist": ("034FC5", "Dragonborn.esm", "HPNO", "wig g0"),
    "Briarheart": ("044310", "Skyrim.esm", "HPNO", "wig g1"), "Velehk": ("075C7F", "Skyrim.esm", "HPNO", "wig g1"),
    "ArnielGane": ("01C19D", "Skyrim.esm", "HPNO", "wig g2"), "ArnielShade": ("06A152", "Skyrim.esm", "HPNO", "wig g2"),
    "Urag": ("01C193", "Skyrim.esm", "HPNO", "wig g3"), "Rikke": ("0132A1", "Skyrim.esm", "HPNO", "wig g3"),
    "RikkeTwin": ("0D0573", "Skyrim.esm", "HPNO", "wig g3"),
    "Kharjo": ("01B1D2", "Skyrim.esm", "HPNO", "g0"), "Derkeethus": ("01403E", "Skyrim.esm", "HPNO", "g0"),
    "Shahvee": ("01411A", "Skyrim.esm", "HPNO", "g0"),
    "Auri": ("000D63", "018Auri.esp", "Fox", "wig antler"),
    "Serana": ("002B6C", "Dawnguard.esm", "Sera", ""),
    "Ysolda": ("013BAB", "Skyrim.esm", "NF", ""), "Hadvar": ("02BF9F", "Skyrim.esm", "NF", ""),
    "Olava": ("013BAE", "Skyrim.esm", "NF", ""),
    "Delphine": ("013478", "Skyrim.esm", "BW", ""), "Adelaisa": ("01411D", "Skyrim.esm", "BW", ""),
    "Camilla": ("01347B", "Skyrim.esm", "BW", "guest:Delphine"), "Sven": ("01347F", "Skyrim.esm", "BW", "guest:Adelaisa"),
    "Aela": ("01A696", "Skyrim.esm", "BW", "contest"),
    "Lydia": ("0A2C8E", "Skyrim.esm", "UBEL", ""),
    "Vilja": ("000D67", "EMCompViljaSkyrim.esp", "ViljaRe", ""),
    # The ArmaRace fixture (bench-fixtures\ArmaRace-v2, H5 2026-11-27): Christer is Mod A's NPC, Thorgar the observer.
    # Until 2026-11-27 these were Orgnar (013479) and Hod (01347D), whom AI Overhaul also overrides (H5).
    "Christer": ("090738", "Skyrim.esm", "ModA", ""),
    "Thorgar": ("0AE777", "Skyrim.esm", None, "observer"),      # never selected: tells Include from Include As New
}
ALL = list(S)
MAIN = [n for n in ALL if n != "Aela"]                     # arms A1-A8; Aela belongs to the contest arms


def fk(name):
    f, p, _, _ = S[name]
    return f"{f}:{p}"


def tags(name):
    return S[name][3].split()


def swaps(name):
    return "wig" in tags(name) or "antler" in tags(name)


# ------------------------------------------------------------------ reference launches
# Groups are checked by bench_run (no subject may be supplied by two active mods); see check_ref_groups().
REFS = {
    "V": {"mods": [], "subjects": ALL, "note": "vanilla: the base game, the follower base mods and the fixture base"},
    "R1": {"mods": ["OW", "SoN", "Sera", "Fox", "ViljaRe", "ModA"],
           "subjects": [n for n in ALL if S[n][2] in ("OW", "SoN", "Sera", "Fox", "ViljaRe", "ModA")] + ["Thorgar"]},
    "R2": {"mods": ["HPNO"], "subjects": [n for n in ALL if S[n][2] == "HPNO"]},
    "R3": {"mods": ["NF", "BW"], "subjects": [n for n in ALL if S[n][2] == "NF"] + ["Delphine", "Adelaisa", "Aela"]},
    "R4": {"mods": ["UBEL", "SSSP", "ViljaSOGS", "CotG"],
           "subjects": ["Lydia", "Aela", "Vilja"] + [n for n in ALL if S[n][2] == "CotG"]},
}

# ------------------------------------------------------------------ the matrix (proposal: Settings matrix)
def _arm(backend, patching, order, auri_wig, tint, antlers, templates, overrides, outfits, **kw):
    d = dict(backend=backend, recast=kw.pop("recast", "Hybrid"), patching=patching, order=order, auri_wig=auri_wig,
             tint=tint, antlers=dict(zip(("SoN", "CotG", "Fox"), antlers)), templates=templates, overrides=overrides,
             outfits=outfits, subjects=MAIN, publish=True, split=None, reload=[], vilja="ViljaRe")
    d.update(kw)
    return d


INHERIT, OWN = "InheritFromTemplate", "GiveEachNpcOwnCopy"
SMALL = ["Auri", "Ulfric", "Urag", "Anoriath", "Dervenin", "Christer", "Thorgar"]
RELOAD = ["Irileth", "Belethor", "Urag", "Auri"]
ARMS = {
    "A1": _arm("ESP", "Create", "Vanilla", "ForwardToSkin", "Auto", ("Remove", "ForwardToSkin", "None"), INHERIT, "Include", False),
    "A2": _arm("ESP", "CreateAndPatch", "Patched", "ConvertToHeadParts", "Auto", ("Remove", "ForwardToSkin", "Remove"), OWN, "IncludeAsNew", True, vilja="ViljaSOGS"),
    "A3": _arm("SkyPatcher", "Create", "Vanilla", "ForwardToOutfit", "Auto", ("None", "Remove", "ForwardToSkin"), OWN, "Ignore", True),
    "A4": _arm("SkyPatcher", "CreateAndPatch", "Patched", "ForwardToSkin", "Auto", ("Remove", "ForwardToOutfit", "Remove"), INHERIT, "Include", False, vilja="ViljaSOGS", reload=RELOAD),
    "A5": _arm("Recast", "Create", "Vanilla", "None", "Auto", ("ForwardToSkin", "None", "ForwardToOutfit"), INHERIT, "IncludeAsNew", True),
    "A6": _arm("Recast", "CreateAndPatch", "Patched", "ConvertToHeadParts", "Always", ("Remove", "ForwardToSkin", "Remove"), OWN, "Ignore", True, vilja="ViljaSOGS", reload=RELOAD),
    "A7": _arm("Recast", "Create", "Vanilla", "ForwardToOutfit", "Auto", ("None", "Remove", "Remove"), OWN, "Include", True, recast="RecastOnly"),
    "A8": _arm("Recast", "CreateAndPatch", "Patched", "ConvertToHeadParts", "Never", ("ForwardToSkin", "Remove", "ForwardToSkin"), INHERIT, "IncludeAsNew", False, recast="RecastOnly", vilja="ViljaSOGS"),
    "A9": _arm("ESP", "CreateAndPatch", "Contested", "ForwardToOutfit", "Auto", ("ForwardToOutfit",) * 3, INHERIT, "Include", True, subjects=SMALL + ["Aela"], all_wigs="ForwardToOutfit"),
    "A10": _arm("ESP", "CreateAndPatch", "Contested", "ForwardToOutfit", "Auto", ("ForwardToOutfit",) * 3, INHERIT, "Include", True, subjects=SMALL, all_wigs="ForwardToOutfit", publish=False),
    "A11": _arm("SkyPatcher", "CreateAndPatch", "Contested", "ForwardToOutfit", "Auto", ("ForwardToOutfit",) * 3, INHERIT, "Include", True, subjects=SMALL + ["Aela"], all_wigs="ForwardToOutfit", third_party=["SSSP"]),
    "A12": _arm("ESP", "CreateAndPatch", "Patched", "ForwardToSkin", "Auto", ("Remove", "ForwardToSkin", "Remove"), OWN, "IncludeAsNew", True, vilja="ViljaSOGS",
                subjects=["Irileth", "Faralda", "Carlotta", "Saadia", "Belethor", "Nazeem", "Farkas", "Eorlund", "Urag", "Ulfric", "Auri", "Serana"],
                split=dict(SplitOutput=True, SplitOutputByGender=True, SplitOutputMaxNpcs=8, AutoEslIfy=False), same_as="A2"),
}
ARM_NUM = {a: int(a[1:]) for a in ARMS}
LAUNCHES = list(REFS) + list(ARMS)


def hpno_group_mode(arm, group):
    """High Poly is registered under four names, one per wig mode; the group-to-mode assignment rotates by arm."""
    if ARMS[arm].get("all_wigs"):
        return ARMS[arm]["all_wigs"]
    return WIG_MODES[(int(group[1]) + ARM_NUM[arm]) % 4]


def hpno_name(mode):
    return f"{MOD['HPNO']} [wig {mode}]"


def subject_mod(arm, name):
    """(ModSetting display name, donor form key) NPC2 is told to use for this subject in this arm, or None."""
    f, p, mk, tg = S[name]
    if mk is None:
        return None
    donor = fk(name)
    for t in tg.split():
        if t.startswith("guest:"):
            donor = fk(t[6:])
    if mk == "HPNO":
        g = next(t for t in tg.split() if t.startswith("g"))
        return hpno_name(hpno_group_mode(arm, g)), donor
    if name == "Vilja":
        return MOD[ARMS[arm]["vilja"]], donor
    return MOD[mk], donor


def wig_handling_active(arm):
    a = ARMS[arm]
    return a["backend"] != "ESP" or a["patching"] == "CreateAndPatch"   # Settings.WigHandlingActiveForOutputMode


def tint_mode(arm, name, overrides=None):
    """(mode, where it comes from): the wig hair tint mode NPC2 resolves for this subject's converted wig, "Auto",
    "Always" (On) or "Never" (Off). NPC2's precedence (Models/WigHairTintMode.cs, read 2026-11-26): the NPC's own
    entry in Settings.NpcWigHairTintModes, then the mod's ModWigHairTintMode, then DefaultWigHairTintMode.
    stage_settings() gives every arm DefaultWigHairTintMode = the arm's tint, FoxGlove the same value as its
    per-mod mode, every other mod none, and no per-NPC entry. `overrides` is the N2B_SETTINGS_OVERRIDES object of
    the build a launch was made from (bench_run.build keeps it in build.json as settingsOverrides): the keys
    "NpcWigHairTintModes" ({form key: mode}), "mod:<MOD key>:ModWigHairTintMode" and "DefaultWigHairTintMode"."""
    ov = overrides or {}
    a, mk = ARMS[arm], S[name][2]
    if name == "Vilja":
        mk = a["vilja"]
    per_npc = {k.lower(): v for k, v in (ov.get("NpcWigHairTintModes") or {}).items()}
    if per_npc.get(fk(name).lower()):
        return per_npc[fk(name).lower()], "the NPC's own entry (NpcWigHairTintModes)"
    mod_mode = a["tint"] if mk == "Fox" else None                 # as stage_settings stages it
    if f"mod:{mk}:ModWigHairTintMode" in ov:
        mod_mode = ov[f"mod:{mk}:ModWigHairTintMode"]
    if mod_mode:
        return mod_mode, "the mod's mode (ModWigHairTintMode)"
    return ov.get("DefaultWigHairTintMode") or a["tint"], "the default mode (DefaultWigHairTintMode)"


def effective_modes(arm, name):
    """What the arm asks NPC2 to do with this subject's wig and antlers ('None' where handling is inert).
    "tint" is the ARM's mode as defined; a build made with overrides is read through tint_mode()."""
    a, (f, p, mk, tg) = ARMS[arm], S[name]
    wig = antler = "None"
    if mk == "HPNO" and "wig" in tg.split():
        wig = hpno_group_mode(arm, next(t for t in tg.split() if t.startswith("g")))
    if mk == "Fox":
        wig, antler = a["auri_wig"], a["antlers"]["Fox"]
    if mk in ("SoN", "CotG") and "antler" in tg.split():
        antler = a["antlers"][mk]
    asked = {"wig": wig, "antler": antler, "tint": a["tint"]}
    if not wig_handling_active(arm):
        wig = antler = "None"
    return {"wig": wig, "antler": antler, "tint": a["tint"], "asked": asked}


# ------------------------------------------------------------------ antlers designated by hand (H2d, 2026-11-26)
# Children of the Green carries Dervenin's and Beleval's antlers as EXTRA PARTS of other head parts, not as head
# parts of the NPC (read from "Children of the Green.esp": 000CotG_DerveninHair and 000CotG_DerveninScars list
# 000CotG_DerveninHLExtra01, model DerveninAntlers.nif; 000CotG_BelevalHair lists 000CotG_BelevalHLExtra01,
# Antlers04.nif, and 000CotG_BelevalHLExtra02, AntlerRingL.nif). NPC2's analysis does name them
# (ModSetting.DetectedAntlerHeadParts holds 000801, 00003F and 000040), but antler mode Remove takes a detected
# part off an NPC only when it is one of the NPC's own head parts; an extra part is removed only when the user
# designates it in the 3D preview (WigForwarder.CollectAntlerHeadPartRemoval; Settings.ManualAntlerHeadParts, keyed
# by EditorID with the mod and NPC it was designated on). So in the first run Remove changed nothing for these two
# (region above the hairline 0.32 to 0.74 against the mod reference in A3, A7, A8; removed antlers read 2.2 to 5.2).
# Decision Q6: designate them by hand. A designation is eligibility only: nothing is removed unless the mod's antler
# mode is Remove (CotG: A3, A7, A8), so it is staged in every arm, as a user's own Settings.json would hold it.
MANUAL_ANTLERS = {
    "Dervenin": ["000CotG_DerveninHLExtra01"],
    "Beleval": ["000CotG_BelevalHLExtra01", "000CotG_BelevalHLExtra02"],
}


def manual_antler_designations(arm, exclude=()):
    """Settings.ManualAntlerHeadParts for one arm: [{"EditorId", "Sources": [{"ModName", "NpcFormKey"}]}], the shape
    NPC2 persists (Models/ManualAntlerHeadPart.cs; a real one is in the repository's validation/Settings.live.json)."""
    out = []
    for name, parts in MANUAL_ANTLERS.items():
        sm = subject_mod(arm, name)
        if name not in ARMS[arm]["subjects"] or name in exclude or sm is None:
            continue
        for editor_id in parts:
            out.append({"EditorId": editor_id, "Sources": [{"ModName": sm[0], "NpcFormKey": sm[1]}]})
    return out


# ------------------------------------------------------------------ where the screenshots go (H7, 2026-11-26)
# A full arm writes about 3.4 GB of 3840x2160 PNG frames and a full tier about 45 GB. They used to go into the
# launch folder under RUN_ROOT\live, on S:, which the first run filled to 2.7 GB free. They now go under a capture
# root on another drive: <capture root>\<run folder name>\<launch folder name>\shots. The launch folder keeps the
# small files (launch.json, log.jsonl, logs, the profile as it was, the build state). The driver logs each frame's
# full path, so the scorer needs no setting. N2B_CAPTURE_ROOT names another root; "." puts the frames back inside
# the launch folder (the layout of every launch up to 2026-11-26).
CAPTURE_ROOT_DEFAULT = P.CAPTURE_ROOT_DEFAULT


def capture_root():
    return os.environ.get("N2B_CAPTURE_ROOT") or CAPTURE_ROOT_DEFAULT


def shots_dir(out):
    """The folder the driver hands DevBench for one launch's frames (`out`: the launch folder)."""
    root = capture_root()
    if root == ".":
        return os.path.join(out, "shots")
    return os.path.join(root, os.path.basename(RUN_ROOT), os.path.basename(out), "shots")


# ------------------------------------------------------------------ RaceMenu (wig tint mode Auto, 2026-11-26)
# NPC2's tint mode Auto gives a converted wig the NPC's hair colour only when RaceMenu would have tinted the worn
# wig: its SKSE plugin is installed and its hair-slot tint is on (BackEnd/RaceMenuDetector.cs, written from
# skee64's main.cpp and TintMaskInterface.cpp). The bench reads the same three facts from mod folders so the
# scorer's prediction does not rest on NPC2's own log.
RACEMENU_FILES = ("skee64.dll", "skee64.ini", "skee64_custom.ini")
HAIR_SLOT_MASK = 2050            # biped slots 31 (Hair) and 41 (LongHair): bits 1 and 11


def _ini_value(path, section, key):
    """The leading unsigned number of `key` in `[section]` (the shipped line reads "bEnableTintHairSlot=1 ; Default[1]"),
    or None when the file, the section or the key is absent or the value does not start with a digit."""
    sec = None
    try:
        lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
    except OSError:
        return None
    for line in lines:
        s = line.strip()
        if s.startswith("[") and "]" in s:
            sec = s[1:s.index("]")].strip().lower()
        elif sec == section.lower() and "=" in s and s.split("=", 1)[0].strip().lower() == key.lower():
            digits = ""
            for ch in s.split("=", 1)[1].strip():
                if not ch.isdigit():
                    break
                digits += ch
            return int(digits) if digits else None
    return None


def racemenu_state(mods, mods_dir=None):
    """What RaceMenu does to a worn hair-slot wig when these mod folders are enabled (priority order, lowest first;
    a later mod's file wins, as in MO2). {"installed", "mod", "tintEnabled", "slotMask", "tintsHairSlots", "files"}.
    Installed: SKSE\\Plugins\\skee64.dll exists. bEnableTintHairSlot ([Features]) and uTintHairSlot ([General])
    come from skee64.ini, then skee64_custom.ini key by key; without either the code defaults hold (on, mask 1,
    which is slot 30 and tints no hair-slot wig). "tintsHairSlots": installed, on, and the mask holds slot 31 or 41.
    Not read: the wig's own slots (RaceMenu tints an item whose armor AND addon both claim a slot of the mask)."""
    found = {}
    for m in mods:
        for fn in RACEMENU_FILES:
            p = os.path.join(mods_dir or MODS_DIR, m, "SKSE", "Plugins", fn)
            if os.path.isfile(p):
                found[fn] = (m, p)
    st = {"installed": "skee64.dll" in found, "mod": (found.get("skee64.dll") or (None,))[0], "tintEnabled": True, "slotMask": 1,
          "files": {fn: p for fn, (_, p) in found.items()}}
    for fn in ("skee64.ini", "skee64_custom.ini"):
        if fn in found:
            on, mask = _ini_value(found[fn][1], "Features", "bEnableTintHairSlot"), _ini_value(found[fn][1], "General", "uTintHairSlot")
            if on is not None:
                st["tintEnabled"] = bool(on)
            if mask is not None:
                st["slotMask"] = mask
    st["tintsHairSlots"] = bool(st["installed"] and st["tintEnabled"] and st["slotMask"] & HAIR_SLOT_MASK)
    return st


# ------------------------------------------------------------------ settings staging
def snapshot():
    path = SNAPSHOT if os.path.exists(SNAPSHOT) else LIVE_SETTINGS
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f), path


def mod_entry(d, display):
    for m in d["ModSettings"]:
        if m["DisplayName"] == display:
            return m
    if display == FIXTURE_MODA:
        return {"DisplayName": FIXTURE_MODA, "CorrespondingModKeys": ["NPC2Bench_ArmaRace_ModA.esp"],
                "CorrespondingFolderPaths": [os.path.join(MODS_DIR, FIXTURE_MODA)],
                "NpcFormKeys": [fk("Christer")], "NpcFormKeysToDisplayName": {fk("Christer"): "Christer"},
                "MergeInDependencyRecords": True, "CopyAssets": True}
    raise KeyError(f"no ModSetting named {display!r} in the settings snapshot")


def mod_folders(d, key):
    return [os.path.basename(p) for p in mod_entry(d, MOD[key])["CorrespondingFolderPaths"]]


def stage_settings(arm, exclude=()):
    """A complete NPC2 Settings.json for one arm, derived from NPC2's own persisted analysis of the corpus mods.
    `exclude` drops subjects whose selection made NPC2 fail to write its plugin (bench_run.build records why)."""
    a = ARMS[arm]
    d, src = snapshot()
    out = {k: v for k, v in d.items() if k != "ModSettings"}
    antler = a["antlers"]
    mods = {}

    def add(display, source_display=None, **over):
        if display in mods:
            return
        m = copy.deepcopy(mod_entry(d, source_display or display))
        m["DisplayName"] = display
        m["ModTemplateHandlingMode"] = a["templates"]
        m["ModRecordOverrideHandlingMode"] = a["overrides"]
        m["IncludeOutfits"] = a["outfits"]
        m["ModWigHandlingMode"] = m["ModAntlerHandlingMode"] = m["ModWigHairTintMode"] = None
        m["LastKnownState"] = None
        m.update(over)
        mods[display] = m

    selections = {}
    for name in a["subjects"]:
        sm = subject_mod(arm, name)
        if sm is None or name in exclude:
            continue
        display, donor = sm
        mk = S[name][2]
        if mk == "HPNO":
            mode = display[display.rindex("[wig ") + 5:-1]
            add(display, MOD["HPNO"], ModWigHandlingMode=mode, ModAntlerHandlingMode="None")
        elif mk == "Fox":
            add(display, ModWigHandlingMode=a["auri_wig"], ModWigHairTintMode=a["tint"], ModAntlerHandlingMode=antler["Fox"])
        elif mk in ("SoN", "CotG"):
            add(display, ModAntlerHandlingMode=antler[mk], ModWigHandlingMode="None")
        elif mk == "ModA":
            # Decision Q4 (battle plan line 83) and N7: the fixture mod uses "include all overrides"
            # (ModSetting.IncludeAllOverrides, read by Patcher.cs 1036-1038 when the mod has its own override mode,
            # which stage_settings always gives it). Mod A's armature edit sits three links from the NPC (outfit,
            # armor, armature), beyond the walking search's default depth of 2; the all-overrides scan
            # (RecordHandler.GetAllOverriddenDependencyRecords) takes every non-NPC override in the mod's plugin.
            add(display, IncludeAllOverrides=True)
        else:
            add(display)
        selections[fk(name)] = {"Item1": display, "Item2": donor}
    for m in d["ModSettings"]:
        if m.get("IsAutoGenerated"):
            mods.setdefault(m["DisplayName"], copy.deepcopy(m))

    out.update({
        "ModSettings": list(mods.values()),
        "SelectedAppearanceMods": selections,
        "OutputDirectory": os.path.join(MODS_DIR, f"{OUTPUT_PREFIX} - {arm}"),
        "AppendTimestampToOutputDirectory": False,
        "OutputPluginName": f"N2B_{arm}",
        "PatchingMode": a["patching"], "OutputBackend": a["backend"], "RecastMode": a["recast"],
        "TemplateHandlingMode": a["templates"], "DefaultRecordOverrideHandlingMode": a["overrides"],
        "DefaultWigHandlingMode": "None", "DefaultWigHairTintMode": a["tint"], "DefaultAntlerHandlingMode": "None",
        "PublishForwardedOutfitsToDistributors": a["publish"],
        "AutoEslIfy": True, "AutoSplitOutput": False, "SplitOutput": False, "SplitOutputByGender": False,
        "SplitOutputByRace": False, "SplitOutputMaxNpcs": None,
        "NpcOutfitOverrides": {}, "ManualAntlerHeadParts": manual_antler_designations(arm, exclude),      # H2d
        "ManualWigArmatures": [], "ManualNonWigArmatures": [],
        "BatFilePreCommands": "", "BatFilePostCommands": "",
    })
    out.update(a["split"] or {})
    # N2B_SETTINGS_OVERRIDES (JSON object), for ONE diagnostic build: "Key": value sets a top-level setting,
    # "mod:<MOD key>:<Field>": value sets a field on every staged ModSetting of that mod (for HPNO: all four wig-mode
    # registrations). Added 2026-11-26 to ask "what if this arm converted this NPC's wig" without a new arm. A build
    # made with it is NOT the arm: launch it tagged, never untagged, and bench_run records the overrides in build.json.
    for key, value in json.loads(os.environ.get("N2B_SETTINGS_OVERRIDES") or "{}").items():
        if key.startswith("mod:"):
            _, mk, field = key.split(":", 2)
            hit = [m for m in out["ModSettings"] if m["DisplayName"].startswith(MOD[mk])]
            assert hit, f"N2B_SETTINGS_OVERRIDES: no staged ModSetting for {mk}"
            for m in hit:
                m[field] = value
        else:
            out[key] = value
    return out, src


# ------------------------------------------------------------------ plugins
def masters(path):
    """Master file names from a plugin's TES4 header."""
    with open(path, "rb") as f:
        head = f.read(24)
        if head[:4] != b"TES4":
            return []
        data = f.read(struct.unpack("<I", head[4:8])[0])
    out, i = [], 0
    while i + 6 <= len(data):
        t, n = data[i:i + 4], struct.unpack("<H", data[i + 4:i + 6])[0]
        if t == b"MAST":
            out.append(data[i + 6:i + 6 + n].rstrip(b"\0").decode("cp1252"))
        i += 6 + n
    return out


# Faster HDT-SMP is loaded in EVERY launch that loads an asset depending on it (Piranha, 2026-11-25: "Dependency on
# SMP while SMP is unloaded is a known crashing avenue"; "add SMP physics to the profiles that need them").
# What happened without it: arm A2 (Auri's wig converted to head parts) killed the game at Auri's spawn in two
# launches, and the same arm had her alive with SMP loaded. Before that the bench kept SMP out of the scored launches
# to keep moving hair out of the noise floor, which left Auri unassessed where her wig mattered most.
# NEEDS_SMP is the appearance mods whose meshes carry SMP physics. It comes from a scan of every .nif in the corpus
# for "HDT Skinned Mesh Physics Object" (scores\smp\nif-scan.json, 7116 meshes): only FoxGlove Auri's wig
# (22a_0.nif, 22a_1.nif). RE-RUN THAT SCAN when the corpus gains a mod, and add its key here.
# The scorer refuses a reading of such a subject from a launch without SMP (bench_score.rows_of).
SMP = "Faster HDT-SMP"
NEEDS_SMP = ("Fox",)
SMP_SETTLE = 6.0                # seconds a physics subject stands before its frames: hair falls into place after a move


def needs_smp(name):
    return S[name][2] in NEEDS_SMP


def launch_has_smp(name):
    """Does this launch load an asset that depends on SMP? A reference: its mods. An arm: NPC2's output carries the
    assets of every subject's mod. V does not (Auri there wears her follower mod's own hair)."""
    if name in REFS:
        return any(k in NEEDS_SMP for k in REFS[name]["mods"])
    return name in ARMS and any(needs_smp(n) for n in ARMS[name]["subjects"])



def launch_mods(name):
    """(enabled mod folders in priority order, lowest first; the NPC2 output folder or None)."""
    d, _ = snapshot()
    mods = list(TOOLS) + list(BASE_MODS)
    out = None
    if name in REFS:
        for k in REFS[name]["mods"]:
            mods += [m for m in mod_folders(d, k) if m not in mods]
    else:
        a = ARMS[name]
        if a["order"] in ("Patched", "Contested"):
            mods += PATCHED
        if a["order"] == "Contested":
            mods += CONTEST
            for k in a.get("third_party", []):
                mods += mod_folders(d, k)
        out = f"{OUTPUT_PREFIX} - {name}"
    if launch_has_smp(name) and SMP not in mods:
        mods.append(SMP)
    return mods, out


def launch_subjects(name):
    names = REFS[name]["subjects"] if name in REFS else ARMS[name]["subjects"]
    smp = launch_has_smp(name)
    return [{"name": n, "form": S[n][0], "plugin": S[n][1], "swap": swaps(n),
             "settle": SMP_SETTLE if smp and needs_smp(n) else 0} for n in names]


if __name__ == "__main__":
    import sys
    if sys.argv[1:2] == ["matrix"]:
        for arm in ARMS:
            print(arm, {k: ARMS[arm][k] for k in ("backend", "recast", "patching", "order", "templates", "overrides", "outfits")})
            for n in ARMS[arm]["subjects"]:
                print("   ", n, subject_mod(arm, n), effective_modes(arm, n) if swaps(n) else "")
