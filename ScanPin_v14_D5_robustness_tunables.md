# ScanPin v14 · D5 — Crash protection + debug tunables

Post-dry-run robustness and facilitator aids. Fed after D1–D4 (it references their tunables).

## Rules (binding)
- Implement **EVERY task to completion** — no stopping early, no skipping, no unrequested scope. Build green after each. Blocked → **STOP and report**.
- **AGGRESSIVE DELETION** of anything superseded. `adaptify.sh` as needed. End with the **checklist**.

## Task 1 — [P1] Crash protection / restore-last-checkpoint
- **Auto-write the current checkpoint** (the existing data-state checkpoint: dataset, registration graph, pins) to browser local storage **every ~1 minute**.
- Add a **"Restore last checkpoint"** button in the debug menu that recovers the last auto-saved state (via the existing checkpoint-apply path).
- Guard the auto-save with a **debug-menu checkbox, default ON**, so it can be disabled if it causes trouble.
- **Verify:** the state auto-saves about every minute; restore recovers the last auto-saved state; unchecking the box stops the auto-save. Build green.

## Task 2 — [P2] Outline-thickness slider
- Add a **mesh outline-thickness slider** to the debug menu (per-participant tuning), adjusting outline weight live.
- **Verify:** the slider adjusts outline thickness live. Build green.

## Task 3 — [P2] Expose D3/D4 strengths as debug tunables (if cheap)
- If cheap, expose the **tile-isolation darkening strength** (D3 T1) and the **overlap feather radius** (D4 T1) as debug-menu tunables. If not cheap, note which were deferred.
- **Verify:** both are adjustable in the debug menu, or the deferral is reported. Build green.

## Checklist
- [ ] T1 [P1] ~1-min auto-checkpoint to local storage + debug restore button + default-on disable checkbox — DONE file:line / BLOCKED
- [ ] T2 [P2] outline-thickness debug slider — DONE / BLOCKED
- [ ] T3 [P2] isolation-strength + feather-radius debug tunables (or deferral noted) — DONE / BLOCKED
