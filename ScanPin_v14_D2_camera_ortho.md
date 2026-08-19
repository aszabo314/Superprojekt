# ScanPin v14 · D2 — Camera pan + ortho-tile orientation

Post-dry-run camera/view fixes.

## Rules (binding)
- Implement **EVERY task to completion** — no stopping early, no skipping, no unrequested scope. Build green after each. Blocked → **STOP and report**.
- **AGGRESSIVE DELETION** of superseded input handling. `adaptify.sh` as needed. End with the **checklist**.

## Task 1 — [P1] Middle-mouse = in-plane pan; helicopter behind a modifier
- Make the default **middle-mouse drag an in-view-plane pan** (screen-space pan). Move the current MMB "helicopter" motion **behind a modifier key** (follow existing modifier conventions; a held modifier + MMB gives the old motion).
- Does **not** change the right-mouse camera (orbit/pan/center stays as-is).
- **Verify:** MMB drag pans in the view plane; modifier+MMB reproduces the old helicopter motion; RMB unchanged. Build green.

## Task 2 — [P1] Align ortho tiles to the main view (+ reset)
- Add a control that **rotates the shared top-down tile orientation so the tiles' vertical aligns with the main 3D camera's heading** — its view direction projected to the ground plane, or its up direction, whichever is angularly closer.
- Add a nearby **"Reset orientation"** control that restores the default **north-up**.
- **Verify:** the align control rotates the tiles to match the camera heading; reset restores north-up; both apply to the shared tile camera. Build green.

## Checklist
- [ ] T1 [P1] MMB in-plane pan; helicopter moved behind a modifier; RMB unchanged — DONE file:line / BLOCKED
- [ ] T2 [P1] align-tiles-to-view control + reset-to-north-up control — DONE / BLOCKED
