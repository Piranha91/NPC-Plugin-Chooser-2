"""Builds the disposable fixture subset for the benchmark's 'refresh-mod' scenario.

  python Tests/Benchmark/make_refresh_fixture.py [--bench-root DIR] [--instance "S:\\Skyrim NPC Selection"]

Copies a few real mods from the MO2 instance (read-only source) into <bench-root>/fixture-template, and writes
<bench-root>/fixture-mutation.tsv: the controlled change applied, inside the disposable copy only, between the
initial population and the measured single-mod refresh of the target mod:
  * the target's own archive (which ships no FaceGen) is replaced by another mod's FaceGen archive, renamed to
    the target plugin's archive name, so archive ownership is unchanged but its FaceGen content is not;
  * one loose FaceGen mesh + tint pair from a third mod is added to the target.
A correct refresh must therefore pick up new archive FaceGen (cached archive listings must be invalidated) and
new loose FaceGen. The main instance is never written: the driver restores the template before every run.
"""
import argparse
import os
import shutil
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

TARGET = "Men of Glenmoril Refined - NPC Overhaul"         # loose FaceGen + plugin + FaceGen-free BSA
TARGET_BSA = "Glenmoril NPCs Refined.bsa"
DONOR_ARCHIVE_MOD = "Immersive College NPCs"                # small archive that does ship FaceGen
DONOR_ARCHIVE = "ICNs_ImmersiveCollegeNPCs.bsa"
DONOR_LOOSE_MOD = "Vilja Re-Imagined SSE"                   # source of one loose FaceGen pair
DONOR_LOOSE = [
    "Meshes/actors/character/facegendata/facegeom/emcompviljaskyrim.esp/00000D67.NIF",
    "Textures/actors/character/facegendata/facetint/emcompviljaskyrim.esp/00000D67.dds",
]
# Unchanged neighbours: the donor itself and a mod whose plugin and BSA ship no FaceGen (it is rejected
# as non-appearance by both variants).
SUBSET = [TARGET, DONOR_ARCHIVE_MOD, "Geralt Prologue Gear TW"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bench-root", default=str(REPO / "Tests" / "bin" / "DiscoveryBench"))
    ap.add_argument("--instance", default=r"S:\Skyrim NPC Selection")
    args = ap.parse_args()
    mods = Path(args.instance) / "mods"
    root = Path(args.bench_root)
    template = root / "fixture-template"
    if template.exists():
        shutil.rmtree(template)
    template.mkdir(parents=True)
    for mod in SUBSET:
        shutil.copytree(mods / mod, template / mod)

    fixture = root / "fixture"
    lines = [
        "# Applied by FixtureMutations inside the disposable fixture only.",
        "\t".join(["copy", str(mods / DONOR_ARCHIVE_MOD / DONOR_ARCHIVE), str(fixture / TARGET / TARGET_BSA)]),
    ]
    for rel in DONOR_LOOSE:
        lines.append("\t".join(["copy", str(mods / DONOR_LOOSE_MOD / rel), str(fixture / TARGET / rel)]))
    (root / "fixture-mutation.tsv").write_text("\n".join(lines) + "\n", encoding="utf-8")
    size = sum(f.stat().st_size for f in template.rglob("*") if f.is_file())
    print(f"template: {template} ({size / 1e6:.0f} MB, {len(SUBSET)} mods)")
    print(f"mutation: {root / 'fixture-mutation.tsv'}")
    print(f"run with: --scenarios refresh-mod --fixture-template \"{template}\" --fixture-root \"{fixture}\" "
          f"--refresh-mod \"{TARGET}\" --mutation-file \"{root / 'fixture-mutation.tsv'}\"")


if __name__ == "__main__":
    main()
