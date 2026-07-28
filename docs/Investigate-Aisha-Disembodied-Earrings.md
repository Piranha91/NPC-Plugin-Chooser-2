# Investigate: disembodied earrings in Aisha's mugshot

**Status:** not investigated. Parked deliberately during the FaceGen-ladder session
(2026-07-27) — noted so it is not lost, not because it blocks anything.

## Symptom

`Aisha (2D49CE:Glenmoril.esm)` under **GLENMORIL - NPC Overhaul** renders a mugshot where the
earrings float free of the head. The rest of the render looks correct — face, hair, skin and
tint all read as expected, so this is not a FaceGen resolution problem.

Observed in the mugshot only. No in-game check was made, so it is **unknown whether the game
renders it correctly** — establishing that is the first step, because it decides whether this is
a renderer bug or bad source data.

## Context at time of observation

- Ladder row 2: the mod supplies the FaceGen mesh, the face tint comes from the mod of origin.
  That concerns the head texture only and is very unlikely to be related.
- Rendered headlessly by `PatchVerifyRunner` immediately after a patch run, via
  `InternalMugshotGenerator.GenerateAsync`.
- Reference image:
  `bin/Debug/.../AutoGen Mugshots/GLENMORIL - NPC Overhaul/Glenmoril.esm/002D49CE.png`

## Where to start

1. Re-render in the in-app 3D preview and compare against the game. If the preview is fine and
   only the mugshot is wrong, suspect the mugshot path specifically rather than CV.R generally.
2. Turn on `Settings.InternalMugshot.LogRenderLogic` and read
   `RenderLogs/GLENMORIL - NPC Overhaul_Aisha_Mugshot.txt` — the attire/armature section will
   name the shape and the node it was attached to.
3. Earrings are almost certainly a worn-armor (ARMO/ARMA) piece rather than a head part, so the
   likely culprits are skinning/attachment: the shape resolving to an armature whose skeleton
   node is missing in the mugshot's scene, or being drawn unskinned at origin-relative
   coordinates. Compare against another NPC wearing the same piece to separate
   "this mod's mesh" from "this class of attachment".

## Related

Nothing known. Not believed to be connected to the FaceGen ladder work — no ladder branch
touches worn armor, and the head itself renders correctly.
