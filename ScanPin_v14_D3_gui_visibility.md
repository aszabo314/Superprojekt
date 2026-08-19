# ScanPin v14 · D3 — GUI & visibility polish

Post-dry-run readability fixes.

## Rules (binding)
- Implement **EVERY task to completion** — no stopping early, no skipping, no unrequested scope. Build green after each. Blocked → **STOP and report**.
- **AGGRESSIVE DELETION** of superseded styling/handling. `adaptify.sh` as needed. End with the **checklist**.

## Task 1 — [P1] Stronger tile-click isolation signal
- When a mesh is isolated by **clicking its top-down tile**, make the isolated mesh stand out much more strongly: **strongly darken the non-isolated meshes** in the 3D (reuse the armed-pick darkening/scrim vocabulary). Keep the darkening strength a debug tunable if cheap (see D5).
- **Verify:** tile-click isolation clearly dims the other meshes and the isolated one pops. Build green.

## Task 2 — [P1] Visible resize handles
- Add a **visible handle affordance** (a grip) to the resize bars (the left column / inspect dock and the tile strip), so they read as draggable.
- **Verify:** resize bars show a visible grip and still resize. Build green.

## Task 3 — [P1] Full mesh names in tile titles
- Drop **all name-shortening**; the top-down tile titles show the **full mesh name (server folder name)**, wrapping/fitting as needed (no ellipsis/truncation).
- **Verify:** tile titles show complete server folder names. Build green.

## Task 4 — [P1] Tree/matrix visual parity
- Give the **registration tree** and the **adjacency matrix** the **same border/frame treatment and equal visual weight**, so they read as two distinct-but-equivalent peer controls (the tree was missed because the matrix dominated).
- **Verify:** tree and matrix have equal-sized borders and neither visually dominates. Build green.

## Checklist
- [ ] T1 [P1] tile-click isolation strongly darkens others — DONE file:line / BLOCKED
- [ ] T2 [P1] visible grips on the resize bars — DONE / BLOCKED
- [ ] T3 [P1] full mesh names in tile titles (no shortening) — DONE / BLOCKED
- [ ] T4 [P1] tree/matrix equal borders + parity — DONE / BLOCKED
