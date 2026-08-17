# ScanPin v14 · F9 — Aiming & linking legibility

Final-release test-session fixes. Every item is the same problem: the "which thing, and where" signal is too weak at the moment the user is aiming. All are solved by **re-applying existing vocabulary** — the gold hover highlight, the armed-pick dim/disable scrim (A9), and the thick-white-rim locator (A10) — not new systems.

## Rules (binding)
- Do tasks in order; **build green after each**. Blocked → **STOP and report**.
- **AGGRESSIVE DELETION** of anything superseded. **No drift.** `adaptify.sh` after `[<ModelType>]` edits; never hand-edit `*.g.fs`. End with the **checklist**.

## Governing rules (apply throughout)
1. **Gold FILL = reference identity (F1); gold OUTLINE/GLOW = transient hover/focus.** Highlighting is always an outline/glow treatment, never a fill — so a highlighted pin can never be misread as "belongs to the reference."
2. **Armed picking dims and disables the invalid surfaces but EXEMPTS the reference marks the user must aim against** (a sibling correspondence point stays lit through the dim).
3. **When a highlighted target is off the ortho frame, point to it with an edge arrow** rather than letting the link silently fail.

## Task 1 — Pin hover → strong top-down link
- In Pin mode, hovering a pin gives the same pin in **both top-down tiles** a **gold hover outline with thickened lines**. If the pin lies **outside a tile's 2D view**, draw an **arrow at that tile's edge** aimed at the off-frame pin (composes with the existing tile auto-refocus: arrow when off-frame, highlight when on-frame).
- **Verify:** hovering a pin lights it gold + thick in both tiles; an off-frame pin shows an edge arrow pointing to it. Build green.

## Task 2 — Armed correspondence pick: gate the tiles
- While a correspondence pick is armed, the **valid pick tile is fully lit** and the **other tile is darkened AND inert** — a click there does nothing (picking is valid in exactly one tile). Reuse the A9 armed-scrim treatment, scoped to the tile pair.
- **Verify:** with a point pick armed, only the correct tile accepts a click; the other reads as dimmed/disabled. Build green.

## Task 3 — Show the sibling point through the dim
- While placing one correspondence point, the **other (already-placed) correspondence point stays at full strength (gold highlight)** on top of the dimming in its tile — the dim suppresses picking affordance, not the reference mark the user is matching against. Draw the **edge arrow** if that sibling point is off the tile's frame.
- **Verify:** while placing B, point A is unmistakable in its tile despite that tile being dimmed; off-frame A shows an arrow. Build green.

## Task 4 — Thicker 3D correspondence glyph
- Increase the **white rim weight** on the correspondence crosshair (the triplex rim/ink/core glyph) and **thicken the white ground intersection lines**, so the glyph reads over both noisy grey texture and the dark void. Expose a **thickness knob in the debug menu** (texture noise is dataset-dependent).
- **Verify:** the crosshair and its ground lines are clearly legible on noisy terrain; the debug thickness knob adjusts them. Build green.

## Task 5 — Matrix hover → connected scanpins
- In Matrix mode, hovering a **pair (cell)** gold-highlights **that edge's scanpins**; hovering a **mesh** gold-highlights **all pins on all edges touching that mesh** (the mesh's full correspondence footprint). Gold outline treatment (rule 1).
- **Verify:** hovering a cell lights its pins; hovering a mesh lights every pin on all its edges; leaving clears. Build green.

## Checklist
- [ ] T1 pin-hover gold+thick link in both tiles + off-frame edge arrow — DONE file:line / BLOCKED
- [ ] T2 armed pick: valid tile lit, other tile dimmed + inert — DONE / BLOCKED
- [ ] T3 sibling already-placed point stays gold through the dim + off-frame arrow — DONE / BLOCKED
- [ ] T4 thicker crosshair white rim + ground lines; debug thickness knob — DONE / BLOCKED
- [ ] T5 matrix hover highlights connected scanpins (cell = edge's pins; mesh = all its edges' pins) — DONE / BLOCKED
