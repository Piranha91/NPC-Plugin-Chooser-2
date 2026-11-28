"""Install (or remove) the ArmaRace fixture + probe in the houseCARL Conflict Lab for the in-game 2x2 check.

    python install_lab.py install     copy the three mods in and create profile NPC2Bench-ArmaRace
    python install_lab.py remove      delete exactly what install created

MO2 must be closed (it rewrites profile files from memory). Do not install while another session holds the
machine's game lock: MO2 would add the new mod folders to that session's profile list on its next launch.
The profile is cloned from DevBench-Minimal-139 (vanilla + DevBench 1.39.0), as harness/npc2recast/make_profiles.py does.
"""
import os, shutil, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
FIX = os.path.dirname(HERE)
LAB = r"X:\Skyrim Conflict Lab"
BASE, NAME = "DevBench-Minimal-139", "NPC2Bench-ArmaRace"
MODS = {"NPC2Bench - ArmaRace Base": os.path.join(FIX, "mods"),
        "NPC2Bench - ArmaRace ModA": os.path.join(FIX, "mods"),
        "NPC2Bench - ArmaRace Probe": HERE}
PLUGINS = ["NPC2Bench_ArmaRace_Base.esp", "NPC2Bench_ArmaRace_ModA.esp", "NPC2Bench_ArmaRace_Probe.esp"]


def mo2_running():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq ModOrganizer.exe"], capture_output=True, text=True).stdout
    return "ModOrganizer.exe" in out


def install():
    dst = os.path.join(LAB, "profiles", NAME)
    assert not os.path.exists(dst), f"profile {NAME} already exists"
    for m, src in MODS.items():
        d = os.path.join(LAB, "mods", m)
        assert not os.path.exists(d), f"mod folder already exists: {m}"
        shutil.copytree(os.path.join(src, m), d)
    src = os.path.join(LAB, "profiles", BASE)
    os.makedirs(dst)
    for f in os.listdir(src):
        p = os.path.join(src, f)
        if os.path.isfile(p) and ".bak" not in f and "-bak" not in f:
            shutil.copy2(p, os.path.join(dst, f))
    os.makedirs(os.path.join(dst, "saves"), exist_ok=True)
    mods = list(MODS)
    ml = open(os.path.join(src, "modlist.txt"), encoding="utf-8").read().splitlines()
    head, rest = [l for l in ml if l.startswith("#")], [l for l in ml if not l.startswith("#")]
    rest = [l for l in rest if l[1:] not in mods]
    open(os.path.join(dst, "modlist.txt"), "w", encoding="utf-8", newline="\r\n").write(
        "\n".join(head + [f"+{m}" for m in reversed(mods)] + rest) + "\n")
    for f, star in (("plugins.txt", "*"), ("loadorder.txt", "")):
        lines = [l for l in open(os.path.join(src, f), encoding="utf-8").read().splitlines() if l.lstrip("*") not in PLUGINS]
        open(os.path.join(dst, f), "w", encoding="utf-8", newline="\r\n").write("\n".join(lines + [star + p for p in PLUGINS]) + "\n")
    print("installed", NAME, mods)


def remove():
    for p in [os.path.join(LAB, "profiles", NAME)] + [os.path.join(LAB, "mods", m) for m in MODS]:
        if os.path.isdir(p):
            shutil.rmtree(p)
            print("removed", p)


if __name__ == "__main__":
    assert not mo2_running(), "close MO2 first"
    {"install": install, "remove": remove}[sys.argv[1]]()
