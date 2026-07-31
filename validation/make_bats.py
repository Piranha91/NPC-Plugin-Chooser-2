"""
Writes ONE console spawn script per validation run, into that run's output folder.

Usage: python make_bats.py

The console command is `bat a`, but the FILE is a.txt - the console's `bat` command names the
script, not the extension. (NPC2's own generator says the same: Auxilliary.BuildSpawnBatchContent
documents it as "the in-game console '.bat' / sel.txt".)

The scripts live in each output folder, which MO2 maps into Data - where `bat` finds them. So
whichever run's output mod is enabled, that run's script is the one in scope, and switching runs
switches the script with it.

FormIDs are computed from the profile's load order + activation, honouring the ESL flag (ESL
plugins live in FE space and consume no normal slot), so the two-digit prefix is the real in-game
one rather than an assumption.
"""
import glob, os, struct, sys

PROFILE = r"S:\Skyrim NPC Selection\profiles\NPC Test\plugins.txt"
LOADORDER = r"S:\Skyrim NPC Selection\profiles\NPC Test\loadorder.txt"
DATA = r"C:\Games\Steam\steamapps\common\Skyrim Special Edition\Data"
MODS = r"S:\Skyrim NPC Selection\mods"


# ---------------------------------------------------------------- load order indices
UNRESOLVED = []
_MOD_PLUGINS = None


def mod_plugins():
    """
    Every plugin file sitting in a mod folder, by lower-cased file name. Built once by walking the
    mods directory; os.scandir rather than glob because mod plugins have names like
    "[Caenarvon] Cosplay Pack Gala.esp" and glob would read those brackets as a character class and
    never find the file. First copy wins - only the ESL flag is read from it, which is a property of
    the file itself.
    """
    global _MOD_PLUGINS
    if _MOD_PLUGINS is None:
        _MOD_PLUGINS = {}
        for mod in os.scandir(MODS):
            if not mod.is_dir():
                continue
            try:
                for f in os.scandir(mod.path):
                    if f.is_file() and f.name.lower().endswith((".esp", ".esm", ".esl")):
                        _MOD_PLUGINS.setdefault(f.name.lower(), f.path)
            except OSError:
                pass
    return _MOD_PLUGINS


def plugin_path(name):
    """
    Where a plugin's file actually is. Only the base masters live in Data; MO2 keeps every managed
    plugin in its own mod folder and merges them into Data through the VFS at launch. Looking in
    Data alone would therefore read the header of the five masters and nothing else - and would then
    treat an ESL-FLAGGED .esp in a mod folder as a normal plugin, giving it a slot it does not take
    and shifting the computed prefix of every full plugin after it by one. (This profile has 27 such
    plugins; the base masters sort above all of them, which is the only reason the specimens here
    were unaffected.)
    """
    direct = os.path.join(DATA, name)
    if os.path.exists(direct):
        return direct
    found = mod_plugins().get(name.lower())
    if found:
        return found
    UNRESOLVED.append(name)
    return None


def is_esl(name):
    if name.lower().endswith(".esl"):
        return True
    path = plugin_path(name)
    if path is None:
        return False
    try:
        with open(path, "rb") as f:
            head = f.read(12)
        if head[:4] != b"TES4":
            return False
        return bool(struct.unpack("<I", head[8:12])[0] & 0x200)
    except OSError:
        return False


def build_index():
    """
    MO2 writes the ORDER to loadorder.txt (every managed plugin) and ACTIVATION to plugins.txt
    (starred). The five base masters are implicitly always active and are deliberately absent from
    plugins.txt, so activation has to be "starred OR implicit", not "starred".
    """
    implicit = {"skyrim.esm", "update.esm", "dawnguard.esm", "hearthfires.esm", "dragonborn.esm"}
    starred = {l.strip().lstrip("*").lower() for l in open(PROFILE, encoding="utf-8-sig")
               if l.strip().startswith("*")}
    order = [l.strip() for l in open(LOADORDER, encoding="utf-8-sig")
             if l.strip() and not l.startswith("#")]

    idx, slot = {}, 0
    for name in order:
        if name.lower() not in starred and name.lower() not in implicit:
            continue          # present but disabled
        if is_esl(name):
            continue          # FE space; consumes no normal slot
        idx[name.lower()] = slot
        slot += 1
    return idx


INDEX = build_index()


def formid(formkey):
    local, plugin = formkey.split(":", 1)
    i = INDEX.get(plugin.lower())
    if i is None:
        sys.exit(f"{plugin} is not active in this profile - cannot compute a FormID")
    return f"{i:02X}{int(local, 16):06X}"


# ---------------------------------------------------------------- specimens
# Ordered so each flatten PAIR spawns back to back, keeping the two halves near each other in the
# pile at the player's feet.
A = [
    ("0D0573:Skyrim.esm",     "Legate Rikke TWIN   (flattened)  - twin wears an officer's helmet"),
    ("0132A1:Skyrim.esm",     "Legate Rikke        (terminus)"),
    ("06A152:Skyrim.esm",     "Arniel's Shade TWIN (flattened)  <-- BEST PAIR: no headgear"),
    ("01C19D:Skyrim.esm",     "Arniel Gane         (terminus)"),
    ("017938:Dragonborn.esm", "Miraak MQ02 TWIN    (flattened)  - both masked, judge body/skin"),
    ("017F7D:Dragonborn.esm", "Miraak              (terminus)"),
    ("10C471:Skyrim.esm",     "Vigilant 02NordM01  (flattened)  - both hooded, judge face"),
    ("10C454:Skyrim.esm",     "Vigilant 01NordM01  (terminus)"),
]

B = [
    ("044310:Skyrim.esm", "Forsworn Briarheart - HairFemaleNord19 under a male skin wig"),
    ("0558F3:Skyrim.esm", "Pit Fan (female)    - HairMaleNord10 under a female skin wig"),
    ("075C7F:Skyrim.esm", "Velehk Sain         - MaleDremoraHair01 under a Dremora skin wig"),
    ("016F69:Skyrim.esm", "Dremora Kynval      - MaleDremoraHair01 under a Dremora skin wig"),
]

C_RECORD = [
    ("034FC5:Dragonborn.esm", "DLC2EncCultist06NordM     - expect CULTIST robes (record write is dead)"),
    ("034FC3:Dragonborn.esm", "DLC2EncCultist06DarkElfF  - expect CULTIST robes"),
    ("006E5C:Dawnguard.esm",  "DLC1VQ03VampireDriverDead - expect DLC1VampireClotheOnly"),
]
C_SKY = [
    ("034FC5:Dragonborn.esm", "DLC2EncCultist06NordM     - expect WARLOCK robes (outfitDefault= reaches the actor)"),
    ("034FC3:Dragonborn.esm", "DLC2EncCultist06DarkElfF  - expect WARLOCK robes"),
]

RUNS = {
    "NPC Output - A-skypatcher": ("a", "TEST A - flatten seam. Each TWIN should match the TERMINUS beside it.", A),
    "NPC Output - A-record":     ("a", "TEST A - flatten seam. Each TWIN should match the TERMINUS beside it.", A),
    "NPC Output - B-skypatcher": ("b", "TEST B - skin-carried wig. Each NPC should show ONE hairstyle (the wig), not two, and must not be bald.", B),
    "NPC Output - B-record":     ("b", "TEST B - skin-carried wig. Each NPC should show ONE hairstyle (the wig), not two, and must not be bald.", B),
    "NPC Output - C-skypatcher": ("c", "TEST C - inert outfit, SKYPATCHER. Compare against the record-mode run: the clothes should DIFFER.", C_SKY),
    "NPC Output - C-record":     ("c", "TEST C - inert outfit, RECORD. Compare against the skypatcher run: the clothes should DIFFER.", C_RECORD),
}


def content(title, entries):
    lines = [f"; {title}", ";"]
    for fk, label in entries:
        lines.append(f";   {formid(fk)}  {label}")
    lines += [";", "; spawns at your feet - stand somewhere open, they land in a pile.",
              "; tai has NO target, so it toggles AI for ALL actors: it freezes them, but running",
              "; another bat this session toggles it back on. Type tai again to re-freeze.", ";"]
    for fk, _ in entries:
        lines.append(f"player.placeatme {formid(fk)}")
    lines.append("tai")
    lines.append("")
    return "\r\n".join(lines)


written = 0
for folder, (name, title, entries) in RUNS.items():
    out = os.path.join(MODS, folder)
    if not os.path.isdir(out):
        print(f"skip (no run yet): {folder}")
        continue
    # Clear anything an earlier generation left: the per-pair set, and the wrong .bat extension.
    for stale in glob.glob(os.path.join(out, "*.bat")):
        os.remove(stale)
    with open(os.path.join(out, name + ".txt"), "w", encoding="ascii") as f:
        f.write(content(title, entries))
    written += 1
    print(f"{folder}\\{name}.txt   ({len(entries)} NPCs)")

print(f"\n{written} spawn script(s) written. In console: bat a   /   bat b   /   bat c")
print("load-order indices: " + ", ".join(
    f"{p}={INDEX.get(p.lower()):02X}" for p in ("Skyrim.esm", "Dawnguard.esm", "Dragonborn.esm")))
if UNRESOLVED:
    # Each of these was assumed non-ESL. If any of them IS ESL-flagged, every full plugin after it
    # got a prefix one too high - sanity-check the indices above against the in-game load order.
    print("\nWARNING: could not find these active plugins in Data or in a mod folder, so their ESL")
    print("flag could not be read: " + ", ".join(sorted(set(UNRESOLVED))))
