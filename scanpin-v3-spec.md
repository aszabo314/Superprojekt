# ScanPin V3 — Specification (Living)

## Purpose

V3 redesigns the ScanPin inspector. The V2 orthographic 3D sub-viewport was visually
confusing and redundant with the main scene. V3 removes it, transfers inspection
controls into the 3D scene, and replaces the core sample volume view with an
**unwrapped cylinder surface stratigraphy diagram**.

`STUB(server)` = server-side computation that needs an endpoint.

---

## Status

All core phases complete:

- **Phase 0–1** (data model, stratigraphy computation/renderer, ranking merge) — done
- **Phase 2** (3D slider → hull picking, ghost clipping, extracted lines) — done
- **Phase 3.12** (explosion view) — done
- **V3 Cleanup** (card GUI, interaction model, hull picking, cut plane polish, 3D text labels) — done
- **Phase 4.15** (remove V2 sub-viewport code + GUI) — done

Remaining items below.

---

## Phase 3 — Experimental

### 3.13 Between-space hover in the stratigraphy diagram

- Mouse position on the diagram → (angle, z).
- Find the two consecutive contact events that bracket the cursor's z in that
  angular column → identify the bounding pair of datasets.
- Highlight that between-space band in the diagram (warm translucent overlay).
- Emit `HoverBetweenSpace(pinId, angle, zLo, zHi, lower, upper)` →
  `pin.BetweenSpaceHover`.
- HTML tooltip showing "Between [A] and [B], gap: X.Xm".
- `ClearBetweenSpaceHover` on mouse leave.

### 3.14 Between-space 3D highlight

Driven by `pin.BetweenSpaceHover`:

- Define a neighborhood around (angle, z): e.g. ±15°, ±some z range.
- **`STUB(server)`**: query both bounding meshes for ray hits in the
  neighborhood. Return pairs of (lower surface point, upper surface point).
- Triangulate a quad patch between the two surfaces.
- Render as a translucent warm-colored volume, depth test enabled, alpha
  blending.
- Prototype quality; edge artifacts acceptable.

---

---

## Out of Scope (Future)

- **Embedded thumbnails** — billboard previews of each non-selected pin's
  stratigraphy in the 3D scene.
- **Average-line distortion** — third stratigraphy mode subtracting per-column
  average z.
- **Connected between-space region finding** — flood-fill the hover region
  instead of using a fixed neighborhood.
- **Radial menu** — long-press touch menu (V4).
- **Flashlight mode** — Ctrl+drag pin repositioning, requires client-side BVH (V4).

---

## Open STUBs (recap)

- `STUB(server)`: real ray-mesh stratigraphy query (replaces `Stratigraphy.compute`
  mock). Inputs: anchor, axis, radius, extents, angularRes. Output: existing
  `StratigraphyData`.
- `STUB(server)`: between-space neighborhood query (3.14).
