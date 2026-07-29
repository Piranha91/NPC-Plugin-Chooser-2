"""
Stages one validation run's Settings.json.

    python run.py A skypatcher      # test A, SkyPatcher output mode
    python run.py A record          # test A, record (Create-and-Patch) mode
    python run.py B skypatcher
    python run.py B record
    python run.py C record          # the real C test
    python run.py C skypatcher      # C's negative control (must NOT report)
    python run.py restore           # put your live Settings.json back

On first use it snapshots your live Settings.json to validation/Settings.live.json and derives
every variant from that snapshot, so repeated runs never compound. Each run writes to its own
output folder ("NPC Output - A-skypatcher", ...) so nothing is overwritten.
"""
import json, os, shutil, sys

BIN = r"S:\Dev\NPC Plugin Chooser 2\NPC Plugin Chooser 2\bin\Debug\net10.0-windows10.0.19041.0"
HERE = os.path.dirname(os.path.abspath(__file__))
LIVE = os.path.join(BIN, "Settings.json")
SNAP = os.path.join(HERE, "Settings.live.json")
HPNO = "High Poly NPC Overhaul - Skyrim Special Edition 2.0 (All Vanilla NPCs)"

# Test A selects each templated DONOR *and its chain terminus*. The terminus is not needed for the
# fix to work — the flatten resolves it from HPNO's plugins whether or not it is selected — but it
# is needed for the side-by-side screenshot to mean anything. HPNO's ESP is not active in this
# profile (NPC2 bakes its appearance into the output instead), so an UNSELECTED terminus renders
# with whatever appearance ESP wins the load order: vanilla, or Bijin, or anything else. Comparing
# a flattened twin against that is comparing two different mods, not before-and-after.
SPECIMENS = {
    "A": ["0D0573:Skyrim.esm", "0132A1:Skyrim.esm",      # Legate Rikke  twin + terminus
          "06A152:Skyrim.esm", "01C19D:Skyrim.esm",      # Arniel's Shade + Arniel Gane
          "017938:Dragonborn.esm", "017F7D:Dragonborn.esm",  # Miraak MQ02 + Miraak
          "10C471:Skyrim.esm", "10C454:Skyrim.esm"],     # Vigilant 02NordM01 + 01NordM01
    "B": ["044310:Skyrim.esm", "0558F3:Skyrim.esm", "075C7F:Skyrim.esm", "016F69:Skyrim.esm"],
    "C": ["034FC5:Dragonborn.esm", "034FC3:Dragonborn.esm", "006E5C:Dawnguard.esm"],
}

# 006E5C (DLC1VQ03VampireDriverDead) is templated on BOTH flags — Traits AND Inventory. It is a
# perfectly good §1/§2 specimen in record mode, but in SkyPatcher mode the validator rejects it for
# the unrelated, pre-existing reason that SkyPatcher cannot redirect an inherited FACE. That popup
# is correct behaviour and has nothing to do with the outfit question, so it is dropped from the
# SkyPatcher variant rather than left to generate a distracting "Invalid Selections Found" dialog.
# The two cultists carry Inventory WITHOUT Traits and pass screening in both modes.
SKYPATCHER_EXCLUDE = {"C": {"006E5C:Dawnguard.esm"}}


def snapshot():
    if not os.path.exists(SNAP):
        if not os.path.exists(LIVE):
            sys.exit(f"no live Settings.json at {LIVE}")
        shutil.copy2(LIVE, SNAP)
        print(f"snapshotted your live settings -> {SNAP}")


def load_snapshot():
    with open(SNAP, encoding="utf-8-sig") as f:
        return json.load(f)


def hpno(d):
    for m in d["ModSettings"]:
        if m["DisplayName"] == HPNO:
            return m
    sys.exit("HPNO ModSetting not found in settings")


def stage(test, mode):
    snapshot()
    d = load_snapshot()
    sky = mode == "skypatcher"

    picked = [k for k in SPECIMENS[test]
              if not (sky and k in SKYPATCHER_EXCLUDE.get(test, set()))]
    d["SelectedAppearanceMods"] = {k: {"Item1": HPNO, "Item2": k} for k in picked}
    d["UseSkyPatcherMode"] = sky
    d["PatchingMode"] = "CreateAndPatch"
    d["OutputDirectory"] = f"NPC Output - {test}-{mode}"

    h = hpno(d)
    h["ModWigHandlingMode"] = None        # follow the global below
    h["ModTemplateHandlingMode"] = None
    h["IncludeOutfits"] = False

    if test == "A":
        # The flatten is what makes §3 reachable; ForwardToOutfit relocates the skin wig, which is
        # what exposes WHICH record's WornArmor the wig pass read.
        d["TemplateHandlingMode"] = "GiveEachNpcOwnCopy"
        d["DefaultWigHandlingMode"] = "ForwardToOutfit"
        d["DefaultAntlerHandlingMode"] = "Remove"
    elif test == "B":
        # §4 lives in ForwardToSkin. None of B's specimens are Traits-templated, so the template
        # mode is irrelevant here — pinned to Inherit so it cannot interact.
        d["TemplateHandlingMode"] = "InheritFromTemplate"
        d["DefaultWigHandlingMode"] = "ForwardToSkin"
        d["DefaultAntlerHandlingMode"] = "None"
    else:  # C
        # Wig/antler handling fully off so nothing else touches these records.
        d["TemplateHandlingMode"] = "InheritFromTemplate"
        d["DefaultWigHandlingMode"] = "None"
        d["DefaultAntlerHandlingMode"] = "None"
        h["IncludeOutfits"] = True                 # the feature under test
        d["NpcOutfitOverrides"] = {}               # no per-NPC override may mask the mod flag

    with open(LIVE, "w", encoding="utf-8") as f:
        json.dump(d, f, indent=1)

    print(f"staged test {test} / {mode}")
    print(f"  UseSkyPatcherMode   = {sky}")
    print(f"  TemplateHandlingMode= {d['TemplateHandlingMode']}")
    print(f"  WigHandlingMode     = {d['DefaultWigHandlingMode']}")
    print(f"  IncludeOutfits(HPNO)= {h['IncludeOutfits']}")
    print(f"  OutputDirectory     = {d['OutputDirectory']}")
    print(f"  NPCs selected       = {len(picked)}")
    dropped = [k for k in SPECIMENS[test] if k not in picked]
    if dropped:
        print(f"  dropped for this mode: {', '.join(dropped)}")
        print("    (Traits-templated as well as Inventory-templated - SkyPatcher rejects it for the")
        print("     unrelated inherited-FACE reason, which would just add a distracting popup.)")
    if test == "C" and sky:
        print("  NOTE: this is C's NEGATIVE CONTROL - it must NOT report anything inert.")
    print("\nNow launch NPC2 through MO2 on the 'NPC Test' profile and hit Run.")


def restore():
    if not os.path.exists(SNAP):
        sys.exit("no snapshot to restore from")
    shutil.copy2(SNAP, LIVE)
    print(f"restored your live settings from {SNAP}")


if __name__ == "__main__":
    a = sys.argv[1:]
    if len(a) == 1 and a[0] == "restore":
        restore()
    elif len(a) == 2 and a[0].upper() in SPECIMENS and a[1] in ("skypatcher", "record"):
        stage(a[0].upper(), a[1])
    else:
        sys.exit(__doc__)
