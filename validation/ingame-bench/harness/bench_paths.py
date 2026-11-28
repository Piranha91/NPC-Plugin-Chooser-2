r"""NPC2 in-game bench: every machine path the harness uses, in one place.

On another machine, edit the defaults below or set the environment variable named beside each one. The defaults
are the paths of the machine the 2.2.7 run was made on; with no variable set, the harness behaves exactly as it
did before this file existed. No other file of the harness holds a machine path that the code uses.

  N2B_INSTANCE           the MO2 instance the bench launches (profiles N2B-*, mods\, ModOrganizer.ini)
  N2B_NPC2_REPO          the NPC2 repository: headless builds run its test project, LIVE_SETTINGS is read from it
  N2B_CORPUS             the frozen corpus folder: mods\, MANIFEST.json, corpus.json, CORPUS.md, settings snapshot
  N2B_RUN_ROOT           the run folder: build\, live\, scores\, traces\, lock and status files
  N2B_CAPTURE_ROOT       where frames go; read when a launch starts (bench_def.capture_root), "." = launch folder
  N2B_FIXTURE_SRC        the ArmaRace fixture mods that `bench_run.py install` copies into the instance
  N2B_IDLE_LOCK          the machine queue (idle_lock.py) every game launch holds
  N2B_DEVBENCH_SCRIPTS   the folder holding dbharness.py (the DevBench bridge library)
  N2B_LAUNCH_DRIVER_DIR  the folder holding wmi_launch.py (starts MO2 outside the shell's job object)
  N2B_SKSE_LNK           the Windows shortcut the driver starts the game with (MO2, argument moshortcut://:SKSE)
  N2B_ARMARACE_RUN       armarace_probe.py only: the run folder of the fixture probe of 2026-11-24
  N2B_HOUSECARL_HARNESS  make_driver.py only: the harness folder that holds npc2recast\driver.py
  N2B_PYTHON             not used by the code (it runs under sys.executable): the interpreter the run used"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))


def _p(env, default):
    return os.environ.get(env) or default


INSTANCE = _p("N2B_INSTANCE", r"S:\Skyrim NPC Selection")
NPC2 = _p("N2B_NPC2_REPO", r"S:\Dev\NPC Plugin Chooser 2\NPC Plugin Chooser 2")
CORPUS = _p("N2B_CORPUS", r"S:\NPC2 Bench Corpus")
RUN_ROOT = _p("N2B_RUN_ROOT", r"S:\Dev\houseCARL\_claude\runs\2026-11-25-npc2bench")
CAPTURE_ROOT_DEFAULT = r"C:\NPC2 Bench Captures"      # N2B_CAPTURE_ROOT overrides it per launch (bench_def.capture_root)
FIXTURE_SRC = _p("N2B_FIXTURE_SRC", r"S:\Dev\NPC Plugin Chooser 2\bench-fixtures\ArmaRace-v2\mods")
IDLE_LOCK = _p("N2B_IDLE_LOCK", r"S:\Dev\houseCARL\_claude\tools\idle_lock.py")
DEVBENCH_SCRIPTS = _p("N2B_DEVBENCH_SCRIPTS", r"S:\Dev\houseCARL\.claude\skills\devbench-in-game-testing\scripts")
LAUNCH_DRIVER_DIR = _p("N2B_LAUNCH_DRIVER_DIR", r"S:\Dev\houseCARL\_claude\runs\2026-09-20-occlusion-transfer-test\driver")
SKSE_LNK = _p("N2B_SKSE_LNK", os.path.join(HERE, "SKSE-NPCSelection.lnk"))
ARMARACE_RUN = _p("N2B_ARMARACE_RUN", r"S:\Dev\houseCARL\_claude\runs\2026-11-24-npc2bench-armarace")
HOUSECARL_HARNESS = _p("N2B_HOUSECARL_HARNESS", r"S:\Dev\houseCARL\_claude\harness")
PYTHON = _p("N2B_PYTHON", os.path.expandvars(r"%LOCALAPPDATA%\Programs\Python\Python312\python.exe"))
