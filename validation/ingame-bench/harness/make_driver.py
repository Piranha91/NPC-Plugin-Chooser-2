"""Derive harness/npc2bench/armarace_probe.py from harness/npc2recast/driver.py by exact, counted replacements."""
import os

import bench_paths as P
H = P.HOUSECARL_HARNESS
src = open(os.path.join(H, "npc2recast", "driver.py"), encoding="utf-8").read().replace("\r\n", "\n")


def rep(a, b, n=1):
    global src
    assert src.count(a) == n, (src.count(a), a[:70])
    src = src.replace(a, b)


def cut(start, end, new):
    global src
    i, j = src.index(start), src.index(end)
    assert src.count(start) == 1 and src.count(end) == 1
    src = src[:i] + new + src[j:]


rep('"""NPC2 Recast output in game',
    '"""ArmaRace fixture probe (2026-11-24): COPIED from harness/npc2recast/driver.py (completed against the real game\n'
    '2026-10-29) with only these changes: subjects from armarace_cases.json (vanilla NPCs), no save/load passes, no body\n'
    'view (every portrait is framed from about the collarbone up: FACE_D/FACE_Z), worn head-slot telemetry, a\n'
    'required-plugin check, and a recording failure is logged instead of aborting (stills are the evidence here).\n'
    'Original docstring follows.\n\nNPC2 Recast output in game')
rep('ARM = arg("--arm")', 'ARM = arg("--arm", "ArmaRace")')
rep(r'RUN = r"S:\Dev\houseCARL\_claude\runs\2026-10-29-npc2-recast-ingame"',
    r'RUN = r"S:\Dev\houseCARL\_claude\runs\2026-11-24-npc2bench-armarace"')
rep('C = json.load(open(os.path.join(HERE, "cases.json"), encoding="utf-8"))',
    'C = json.load(open(os.path.join(HERE, "armarace_cases.json"), encoding="utf-8"))')
# Collarbone-up framing: vFOV 39.4 deg (fDefaultWorldFOV 65 at 16:9) -> half-height 0.358*D = 24.4 u at D=68;
# eye at feet+124 -> the frame spans feet+99.6 .. feet+148.4.
rep('FACE_D, FACE_Z, BODY_D, BODY_Z = 95.0, 128.0, 260.0, 75.0   # faceswap smoke 2026-10-28',
    'FACE_D, FACE_Z = 40.0, 126.0   # measured 2026-11-24: at D=68 the frame is ~75 u tall (mid-chest up), not the 49 u computed from FOV 65; D=40 gives ~44 u, head to collar')
cut('def subjects():', 'def log(**row):',
    'def subjects():\n    """The four vanilla subjects of the 2x2 (armarace_cases.json)."""\n'
    '    return {n: (int(s["form"], 16), s["plugin"]) for n, s in C["subjects"].items()}\n\n\n')
rep('            "wornBody": fid(db.ret("Actor", "GetWornForm", ref, [0x4])),',
    '            "wornBody": fid(db.ret("Actor", "GetWornForm", ref, [0x4])),\n'
    '            "wornHair": fid(db.ret("Actor", "GetWornForm", ref, [0x2])),\n'
    '            "wornCirclet": fid(db.ret("Actor", "GetWornForm", ref, [0x1000])),')
rep('                                  "face34": (sx + FACE_D / math.sqrt(2), sy + FACE_D / math.sqrt(2), z + FACE_Z, 225.0),\n'
    '                                  "body": (sx, sy + BODY_D, z + BODY_Z, 180.0)}.items():',
    '                                  "face34": (sx + FACE_D / math.sqrt(2), sy + FACE_D / math.sqrt(2), z + FACE_Z, 225.0)}.items():')
rep('        if m.startswith(("Recast", "SkyPatcher", "NPC2 ", "Lab Replacer", "houseCARL - FaceSwapLab", "DevBench")):',
    '        if m.startswith(("NPC2Bench", "DevBench")):')
cut('def sequence():', 'def run_sequence():',
    'def sequence():\n    return [("spawn", None), ("pass", "new")]\n\n\n')
rep('        v = db.video_start(OUT, label=f"n2r-{ARM}", hook_limit=6, allow_display=False)\n'
    '        video = True\n'
    '        log(step="video_start", mode=v.get("mode"), v=v)\n'
    '        assert v.get("mode") == "game", f"video mode {v.get(\'mode\')}"\n',
    '        try:\n'
    '            v = db.video_start(OUT, label=f"n2r-{ARM}", hook_limit=6, allow_display=False)\n'
    '            video = True\n'
    '            log(step="video_start", mode=v.get("mode"), v=v)\n'
    '        except Exception as e:      # diagnostic recording only: the native stills are this probe\'s evidence\n'
    '            log(step="video_start_failed", error=repr(e))\n'
    '            db.video_mark = lambda label: log(step="video_mark_skipped", label=label)   # marks need the recorder\n')
rep('        log(step="loaded_plugins", plugins=db.loaded_plugins())\n',
    '        lp = db.loaded_plugins()\n'
    '        log(step="loaded_plugins", plugins=lp)\n'
    '        missing = [p for p in C["required_plugins"] if p.lower() not in [x.lower() for x in lp]]\n'
    '        assert not missing, f"fixture plugins not loaded: {missing}"\n')

# 2026-11-24: run from the Skyrim NPC Selection instance (the Conflict Lab is being read by another session's renderer)
rep('LAB = r"X:\Skyrim Conflict Lab"', 'LAB = r"S:\Skyrim NPC Selection"')
rep('LNK = os.path.join(LAB, "SKSE.lnk")', 'LNK = os.path.join(RUN, "SKSE-NPCSelection.lnk")')

# 2026-11-24 launch 4: (a) projection probe by pinned-camera rotation (devbench-screenshots "the projection rule"),
# (b) the engine's own FOV setting, (c) subject heading read before every picture, with the aspect predicted from it.
rep('    STATE["empty"] = shot(f"{tag}-empty-face")\n'
    '    log(step="empty_studio", tag=tag, camera=cam, path=STATE["empty"])\n',
    '    STATE["empty"] = shot(f"{tag}-empty-face")\n'
    '    log(step="empty_studio", tag=tag, camera=cam, path=STATE["empty"])\n'
    '    for pname, dyaw, dpitch in (("yaw5", 5.0, 0.0), ("yaw10", 10.0, 0.0), ("pitch5", 0.0, 5.0)):\n'
    '        c = drive(sx, sy + FACE_D, sz + FACE_Z, 180.0 + dyaw, dpitch)\n'
    '        log(step="fov_probe", name=pname, yawDeg=180.0 + dyaw, pitchDeg=dpitch, camera=c, path=shot(f"{tag}-fov-{pname}"))\n'
    '    drive(sx, sy + FACE_D, sz + FACE_Z, 180.0)\n')
rep('        log(step="unpause", ok=ok, hours=hours)\n',
    '        log(step="unpause", ok=ok, hours=hours)\n'
    '        for key in ("fDefaultWorldFOV:Display", "fDefault1stPersonFOV:Display"):\n'
    '            try:\n'
    '                log(step="getini", key=key, res=db.post("console", {"action": "exec", "command": f\'getini "{key}"\', "capture": True}))\n'
    '            except Exception as e:\n'
    '                log(step="getini_error", key=key, error=repr(e))\n')
rep('            "wornBody": fid(db.ret("Actor", "GetWornForm", ref, [0x4])),\n',
    '            "angleZ": db.ret("ObjectReference", "GetAngleZ", ref),\n'
    '            "wornBody": fid(db.ret("Actor", "GetWornForm", ref, [0x4])),\n')
rep('def shotat(name, tag):\n',
    'FACING_TOL = 20.0     # degrees off the camera bearing before a re-aim is tried\n'
    'VIEW_BEARING = {"face": 0.0, "face34": 45.0}   # camera bearing from the subject, clockwise from +Y (heading 0)\n'
    '\n\n'
    'def heading(ref):\n'
    '    """The reference\'s heading in degrees (ObjectReference.GetAngleZ: 0 = facing +Y, clockwise), or None."""\n'
    '    v = db.ret("ObjectReference", "GetAngleZ", ref)\n'
    '    return float(v) if isinstance(v, (int, float)) and not isinstance(v, bool) else None\n'
    '\n\n'
    'def wrap180(a):\n'
    '    return (a + 180.0) % 360.0 - 180.0\n'
    '\n\n'
    'def aspect(view, hdg):\n'
    '    """Which side of the subject the camera sees: degrees clockwise from its front, and a word for it."""\n'
    '    if hdg is None:\n'
    '        return {"deg": None, "side": "unknown"}\n'
    '    a = wrap180(VIEW_BEARING[view] - hdg)\n'
    '    side = "front" if abs(a) <= 25 else "three-quarter" if abs(a) <= 65 else "profile" if abs(a) <= 115 else \\\n'
    '        "rear three-quarter" if abs(a) <= 155 else "back"\n'
    '    return {"deg": round(a, 1), "side": side}\n'
    '\n\n'
    'def shotat(name, tag):\n')
rep('    out = {"wornBody": worn}\n    cams = {}\n',
    '    hdg0 = heading(ref)\n'
    '    hdg1 = None\n'
    '    if hdg0 is None or abs(wrap180(hdg0)) > FACING_TOL:      # re-aim once, then read back\n'
    '        db.ret("ObjectReference", "SetAngle", ref, [0.0, 0.0, 0.0], timeout_ms=20000)\n'
    '        time.sleep(2.0)\n'
    '        hdg1 = heading(ref)\n'
    '    try:\n'
    '        insp = db.post("inspect", {"kind": "refs", "formId": ref})\n'
    '    except Exception as e:\n'
    '        insp = repr(e)\n'
    '    out = {"wornBody": worn, "heading": {"afterPlacement": hdg0, "afterReaim": hdg1}, "inspect": str(insp)[:600]}\n'
    '    cams = {}\n')
rep('        cams[view] = drive(x, y, zz, yaw)\n',
    '        cams[view] = drive(x, y, zz, yaw)\n'
    '        hs = heading(ref)\n'
    '        out[view + "-predicted"] = {"headingAtShot": hs, **aspect(view, hs)}     # written before the picture exists\n'
    '        log(step="aspect", name=name, view=view, **out[view + "-predicted"])\n')

# 2026-11-24 launch 5 (Piranha): per-actor AI freeze at spawn vs the global `tai` only, read from the head bones,
# with the player's position relative to each subject; then a tracking test with the player moved to either side.
rep('def shotat(name, tag):\n',
    'HEAD, EYE, HMAG = "NPC Head [Head]", "NPCEyeBone", "NPC Head MagicNode [Hmag]"   # skeleton.nif, read 2026-11-24\n'
    'FREEZE = C.get("freeze", {})     # subject -> "papyrus" (Actor.EnableAI(false)) | "console" ("<ref>".tai)\n'
    '\n\n'
    'def nodepos(ref, node):\n'
    '    p = [db.ret("NetImmerse", "GetNodeWorldPosition" + a, None, [{"form": ref}, node, False]) for a in "XYZ"]\n'
    '    return p if all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in p) else None\n'
    '\n\n'
    'def headread(name, ref, stage):\n'
    '    """One row: body heading, head direction (world yaw of head->eye and head->magic-node), AI flag, and where the\n'
    '    player stands relative to the subject\'s front (GetHeadingAngle: degrees, + = to its right)."""\n'
    '    row = {"name": name, "stage": stage, "freeze": FREEZE.get(name), "heading": heading(ref), "pos": db.pos(ref),\n'
    '           "aiEnabled": db.ret("Actor", "IsAIEnabled", ref), "playerPos": db.pos(db.PLAYER),\n'
    '           "playerFromFront": db.ret("ObjectReference", "GetHeadingAngle", ref, [{"form": db.PLAYER}])}\n'
    '    if row["pos"] and row["playerPos"]:\n'
    '        row["playerDist"] = round(math.dist(row["pos"][:2], row["playerPos"][:2]), 1)\n'
    '    h, e, m = nodepos(ref, HEAD), nodepos(ref, EYE), nodepos(ref, HMAG)\n'
    '    row["nodes"] = {"head": h, "eye": e, "hmag": m}\n'
    '    for key, n in (("eye", e), ("hmag", m)):\n'
    '        if h and n and abs(n[0] - h[0]) + abs(n[1] - h[1]) > 0.05:\n'
    '            yaw = math.degrees(math.atan2(n[0] - h[0], n[1] - h[1]))\n'
    '            row["headYaw_" + key] = round(yaw, 1)\n'
    '            if row["heading"] is not None:\n'
    '                row["headVsBody_" + key] = round(wrap180(yaw - row["heading"]), 1)\n'
    '    log(step="headread", **row)\n'
    '    return row\n'
    '\n\n'
    'def freeze(name, ref):\n'
    '    how = FREEZE.get(name)\n'
    '    if not how:\n'
    '        return\n'
    '    before = db.ret("Actor", "IsAIEnabled", ref)\n'
    '    if how == "papyrus":\n'
    '        res = db.ret("Actor", "EnableAI", ref, [False], timeout_ms=20000)\n'
    '    else:\n'
    '        res = db.post("console", {"action": "exec", "command": \'"%s".tai\' % ref[2:]})\n'
    '    time.sleep(1.0)\n'
    '    log(step="freeze", name=name, how=how, res=res, aiBefore=before, aiAfter=db.ret("Actor", "IsAIEnabled", ref))\n'
    '    headread(name, ref, "frozen")\n'
    '\n\n'
    'def tracktest():\n'
    '    """Does a subject\'s head follow the player? One frozen and one unfrozen subject side by side at heading 0, the\n'
    '    player moved to their right and then their left, under the global `tai` and then with it off."""\n'
    '    release_cam()\n'
    '    sx, sy, sz = CELL["studio"]\n'
    '    pair = {"Orgnar": (sx + 60.0, sy), "Hod": (sx - 60.0, sy)}\n'
    '    for n, (x, y) in pair.items():\n'
    '        ref = REF[n]\n'
    '        place(ref, x, y, sz); time.sleep(2.0)\n'
    '        reattach(ref)\n'
    '        db.ret("ObjectReference", "SetAngle", ref, [0.0, 0.0, 0.0], timeout_ms=20000)\n'
    '    time.sleep(2.0)\n'
    '    for n in pair:\n'
    '        headread(n, REF[n], "track-setup")\n'
    '    spots = {"right": [(sx + 220.0, sy + 150.0), (CELL["park_x"][1], CELL["park_y"])],\n'
    '             "left": [(sx - 220.0, sy + 150.0), (CELL["park_x"][0], CELL["park_y"])]}\n'
    '    for ai_label, off in (("globalTaiOn", True), ("globalTaiOff", False)):\n'
    '        ai_travel(off)\n'
    '        for side, cands in spots.items():\n'
    '            landed = None\n'
    '            for (x, y) in cands:\n'
    '                try:\n'
    '                    landed = db.move_within_cell(x, y, sz)\n'
    '                    break\n'
    '                except Exception as e:\n'
    '                    log(step="track_move_refused", side=side, x=x, y=y, error=repr(e)[:300])\n'
    '            log(step="track_player", ai=ai_label, side=side, landed=landed)\n'
    '            time.sleep(6.0)\n'
    '            for n in pair:\n'
    '                headread(n, REF[n], f"track-{ai_label}-{side}")\n'
    '\n\n'
    'def shotat(name, tag):\n')
rep('            REF[n] = db.spawn(BASE[n], x, y, z)\n            dress(n, REF[n])\n',
    '            REF[n] = db.spawn(BASE[n], x, y, z)\n'
    '            headread(n, REF[n], "spawned")\n'
    '            freeze(n, REF[n])\n'
    '            dress(n, REF[n])\n')
rep('    log(step="spawn", refs=REF)\n',
    '    for n, r in REF.items():\n'
    '        headread(n, r, "parked")\n'
    '    log(step="spawn", refs=REF)\n')
rep('    out = {"wornBody": worn, "heading": {"afterPlacement": hdg0, "afterReaim": hdg1}, "inspect": str(insp)[:600]}\n',
    '    out = {"wornBody": worn, "heading": {"afterPlacement": hdg0, "afterReaim": hdg1}, "inspect": str(insp)[:600]}\n'
    '    headread(name, ref, "studio")\n')
rep('        log(step="aspect", name=name, view=view, **out[view + "-predicted"])\n',
    '        log(step="aspect", name=name, view=view, **out[view + "-predicted"])\n'
    '        headread(name, ref, "atShot-" + view)\n')
rep('def sequence():\n    return [("spawn", None), ("pass", "new")]\n',
    'def sequence():\n    return [("spawn", None), ("pass", "new"), ("tracktest", None)]\n')
rep('        elif op == "load": load(a)\n',
    '        elif op == "load": load(a)\n'
    '        elif op == "tracktest": tracktest()\n')

out = os.path.join(H, "npc2bench", "armarace_probe.py")
open(out, "w", encoding="utf-8", newline="\n").write(src)
print("wrote", out, len(src))
