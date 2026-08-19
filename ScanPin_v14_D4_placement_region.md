# ScanPin v14 · D4 — Placement region: overlap feather + per-dataset default radius

Post-dry-run fixes to where and how big a new pin is. One task needs analysis before coding — do the analysis, don't guess.

## Rules (binding)
- Implement **EVERY task to completion** — no stopping early, no skipping, no unrequested scope. Build green after each. Blocked → **STOP and report**.
- **AGGRESSIVE DELETION** of anything superseded. `adaptify.sh` after `[<ModelType>]` edits; never hand-edit `*.g.fs`. End with the **checklist**.

## Task 1 — [P1] Feather the valid placement overlap (~1 m)
- Pin placement is currently restricted to the strict mesh-mesh overlap, which is slightly too tight — valid features extend a bit outside it. Enlarge the valid region by a **~1 m feather**.
- **First analyze** how the placement-overlap gate is currently computed, then implement a **world-space** feather. **Recommended definition:** a point is valid if **both meshes have surface within the feather radius (~1 m)** of the point — i.e. relax the strict "both present here" test to "both present within the feather." (Do **not** feather a screen-space mask by a world distance — that is view-dependent and wrong.)
- Make the **feather radius a debug tunable** (see D5).
- If, after analysis, the recommended approach is genuinely infeasible in this codebase, **STOP and report** with 2–3 concrete alternative proposals rather than hacking.
- **Verify:** features up to ~1 m outside the strict overlap are now pickable; the strict-overlap-only behavior is gone; the feather radius is tunable. Build green.

## Task 2 — [P1] Per-dataset default pin radius
- Implement an **extensible per-dataset default-radius mechanism** — a config keyed by dataset id/name, defaulting to the current global value. **Not** a one-off inline hardcode.
- Set the warm-up dataset **"ScanPin - UserStory"** to **~2.5× the current default**; all other datasets keep the current default.
- **Verify:** new pins in "ScanPin - UserStory" start ~2.5× larger; other datasets are unchanged; adding a future per-dataset override is a one-line config edit. Build green.

## Checklist
- [ ] T1 [P1] analyzed the overlap gate; world-space ~1 m feather implemented (or STOP+proposals); feather radius tunable — DONE file:line / BLOCKED
- [ ] T2 [P1] extensible per-dataset default radius; "ScanPin - UserStory" ~2.5×; others unchanged — DONE / BLOCKED
