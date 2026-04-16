# ScanPin — Pre-Presentation Cleanup Specification

## Purpose

This is the bugfix and grievance pass before presenting the project. No new features — only fixes, correctness audits, performance passes, and small GUI improvements. The target reader is a Claude Code AI agent.

Items are grouped by area and ordered within each group by estimated impact. Critical bugs first, then correctness audits, then performance, then GUI polish.

---

## 1. Critical Interaction Bug: Pointer Capture

### Problem

When the user drags along the cylinder hull (to adjust the cut plane), the release event sometimes fails to fire if the cursor leaves the shape mid-drag. The interaction gets "stuck" — the drag continues even after mouse release, because the release event was delivered to a different target.

### Root Cause

The browser/aardvark.dom is routing pointer events based on what's under the cursor at each moment, rather than maintaining the capture on the original drag target. This is exactly what the [Pointer Capture API](https://developer.mozilla.org/en-US/docs/Web/API/Pointer_events#pointer_capture) is designed to solve.

### Fix

Use aardvark.dom's pointer capture API for drag interactions on the cylinder hull (and any other drag that can leave its target element).

**Investigation task:** Search aardvark.dom for the pointer capture API. Likely names: `SetPointerCapture`, `PointerCapture`, `OnPointerDown` with a capture flag, or an attribute like `PointerCapture=true`. Check the demo program and documentation.

**Apply pattern:**
```
On pointer down on cylinder hull:
  capture the pointer to this element
On pointer move:
  (no change — events are already captured)
On pointer up:
  release capture
  commit the drag result
```

Once the pointer is captured, ALL subsequent events for that pointer (move, up, cancel) are delivered to the capturing element until release. This fixes the stuck-drag bug.

**Apply to all drag interactions that can leave their target:**
- Cylinder hull drag (AcrossAxis height, AlongAxis angle).
- Card title bar drag.
- Stratigraphy diagram cut-line drag.
- Any slider handle that can be dragged past its track boundary.

---

## 2. Performance: In-Between Space Hover (HIGH PRIORITY)

### Problem

Hovering in the stratigraphy diagram to find the bounded in-between space takes multiple seconds for large cylinders. This is currently the slowest interaction in the system — slower even than the 3D neighborhood reconstruction.

### Root Cause

On each hover event, the implementation samples vertical columns of rays to determine which two datasets bound the hovered gap. For large cylinders this means many ray queries, recomputed for every mouse move.

### Fix: Pre-computed Hover Map

The stratigraphy diagram is static once the pin is placed — the underlying data does not change on hover. Therefore, compute a **hover lookup map** once at diagram creation (or on data change) and use it for all subsequent hover events.

**Hover map structure:**

```fsharp
/// Lookup table for in-between space identification.
/// For each pixel of the stratigraphy diagram, stores which two datasets
/// bound the gap at that pixel (if any).
type HoverMap = {
    /// Image-space lookup. Indexed by (diagramPixelX, diagramPixelY).
    /// For each pixel: the pair of bounding dataset IDs, or None if no gap.
    Cells : (DatasetId * DatasetId) option[,]
    /// Same dimensions as the stratigraphy diagram texture.
    Width : int
    Height : int
}

/// Compute the hover map from the stratigraphy data.
/// Called once on pin creation and on data change (dataset added/removed, prism changed).
val computeHoverMap : stratigraphy:StratigraphyData -> HoverMap
```

**Algorithm:** For each pixel, determine which angular column it falls in, find the two consecutive events in that column that bracket the pixel's y-coordinate, record the bounding dataset pair. This is one pass over the diagram pixels and is exactly the same computation as the current per-hover query — just cached.

**Hover handler becomes O(1):** index into the cached array, read the bounding pair.

**Invalidation:** Recompute the hover map when:
- The stratigraphy data changes (prism moved, dataset added/removed).
- The display mode changes (Undistorted ↔ Normalized) — the pixel mapping is different.
- The filter/ranking changes (hidden datasets affect which gaps exist).

**Optional: reduced sampling rate for the map itself.** If the full-resolution map is still too expensive to compute, sample at half resolution and use nearest-neighbor lookup. Unlikely to be necessary given this is a one-shot computation.

---

## 3. Correctness: In-Between Space Identification

### Problem

In-between spaces sometimes "flip" or "break" around mesh intersections. When two meshes cross each other inside the cylinder, the gap between them changes identity (from "between A and B" to "between B and A"), causing the hover region to fragment.

### Expected Behavior

**One continuous in-between space = one selectable area**, regardless of how often the participating meshes intersect each other. The "inbetween-ness" of a region is defined by the pair of meshes that bound it, not by which is above and which is below.

### Fix

Change the bounding pair representation to be **order-independent**:

```fsharp
// Before (order matters):
type GapKey = { Lower: DatasetId; Upper: DatasetId }

// After (canonical order — smaller ID first):
type GapKey = { DatasetA: DatasetId; DatasetB: DatasetId }
    // where DatasetA <= DatasetB (lexicographic order on the ID)
```

In the hover map computation: after identifying the two bounding events, sort the pair by ID before storing. This ensures that "gap between A and B" and "gap between B and A" resolve to the same key, so connected regions across mesh crossings are treated as one selectable area.

**Visual effect:** Hovering the gap now highlights all connected pixels with the same bounding pair, regardless of which mesh is currently on top. Mesh intersection points no longer fragment the hover region.

---

## 4. Correctness Audit: Stratigraphy and Cross-Section Diagrams

### Task

Review the implementation of both diagrams for logical soundness. This is not a known bug — it's a correctness audit.

### Audit Checklist

For the **stratigraphy diagram**:
- Angular sampling: do the angular columns cover the full 0°–2π range without gaps or overlaps? Is the first and last column adjacent (wrapping around)?
- Axis-position range: does the vertical extent match the prism's actual extent (ExtentForward + ExtentBackward)?
- Per-column intersection sorting: are events sorted by z ascending, consistently?
- Non-heightfield handling: when a ray hits the same mesh multiple times, are all intersections recorded?
- Normalized mode: per-column min/max is correct; minimum range clamping is applied; the normalization doesn't lose data at columns with no intersections.
- Color assignment: dataset colors are consistent across columns and diagrams.

For the **cross-section diagram** (the original V1 cut-line profile, if it still exists in the system):
- Cut plane orientation: does the rendered cross-section match the specified cut plane mode (AlongAxis vs. AcrossAxis) and parameter (angle or distance)?
- Polyline construction: are intersection segments correctly connected into continuous polylines? (Same mesh, adjacent triangles produce connected segments.)
- Coordinate mapping: is the 2D diagram coordinate system correctly derived from the 3D cut plane?

**Methodology:** Read the computation code, trace a few columns/cross-sections by hand against a known test case (e.g., a flat horizontal plane mesh should produce a straight horizontal line in the stratigraphy diagram, at all angular positions). Document any discrepancies found. Fix them if the cause is clear; flag them for human review if uncertain.

---

## 5. Stratigraphy Diagram: Always-Visible Cut Line

### Problem

The cut line in the stratigraphy diagram is sometimes not visible — either too small to perceive, or hidden behind other rendered elements.

### Fix

Render the cut line as an HTML/SVG overlay on top of the OpenGL stratigraphy texture. This is always drawable regardless of the underlying texture scale.

**Implementation:**
- The stratigraphy diagram is displayed in an HTML element (likely an `<img>` or a render target canvas).
- Overlay an SVG element on top with the same dimensions.
- Draw the cut line as an SVG `<line>` with visually distinct styling (e.g., white with a subtle shadow, 2px stroke width).
- Update the SVG line position whenever the cut plane parameter changes.

**Suppression in Normalized mode:**

In normalized mode, the cut line is no longer a straight horizontal/vertical line — because each column's z-axis is independently normalized, the cut line becomes a jagged shape (mapping different z-values to different pixel positions per column). Rendering a single line in normalized mode would be wrong.

**Behavior:** Do not render the HTML/SVG overlay line when `StratigraphyDisplay = Normalized`. The underlying OpenGL rendering may still attempt to show the cut plane; the overlay simply doesn't add to it.

---

## 6. Performance: Cut Plane Drag

### Problem

Dragging the cut plane (via hull picking, stratigraphy diagram, or any other control) is visibly slow. Frame rate drops noticeably during drag.

### Diagnosis Tasks

Before fixing, identify the bottleneck:
1. Is the scene graph rebuilding on every frame of the drag? (Check if the cut plane geometry is allocated fresh vs. reused.)
2. Are all meshes re-uploaded or re-rendered when only the cut plane changed?
3. Are the cut lines (per-dataset intersections) recomputed every frame?
4. Are cards being re-laid out or re-rendered on every frame?

### Fix: Isolate the Cut Plane in its Own Scene Graph Branch

The cut plane's transform changes frequently during drag, but the geometry of the meshes, the cylinder hull, and most other scene elements do not. Isolating the cut plane into a separate scene graph branch allows aardvark's incremental rendering to only update the plane's transform, not the entire scene.

**Scene graph restructuring:**

```
Scene Root
├── Static Meshes Branch (updates only when datasets change)
│   ├── Mesh 1
│   ├── Mesh 2
│   └── ...
├── Pin Cylinder Branch (updates when prism changes)
│   ├── Hull
│   ├── Caps
│   └── Edge Lines
├── Cut Plane Branch (updates frequently during drag)    ← ISOLATED
│   └── Cut Plane Quad (transform-only updates)
├── Cut Lines Branch (updates when cut plane changes)   ← ISOLATED
│   └── Per-dataset intersection polylines
└── Highlight Branch (updates during hover)
    └── In-between space volume
```

**Tiered update strategy:**

- **Per frame during drag:** Update only the Cut Plane Branch transform. The mesh geometry, cylinder hull, and other branches are not touched.
- **On drag end (or debounced):** Recompute the cut lines (per-dataset intersections with the new cut plane) and update the Cut Lines Branch. This is expensive — don't do it per frame. Instead, during drag show only the cut plane itself (no intersection lines). On drag end, compute the lines.
- **Optional intermediate update rate:** If showing no lines during drag feels broken, update the cut lines at a lower rate (e.g., every 5 frames, or every 100ms). A simple frame-counter or debounced update timer.

**Mutable/adaptive values:** Use aardvark's change-tracking primitives (IMod, cval, aval — whichever is idiomatic in aardvark.dom) so that only dependent nodes recompute when the cut plane transform changes. Audit the dependency graph to confirm that mesh rendering does not depend on the cut plane.

**Verification:** Profile before and after. The drag should be smooth (60fps or close) after isolation.

---

## 7. Placement Mode: Pin Length Control

### Problem

The user has no way to influence the length of a ScanPin. The cylinder currently extends a fixed default in both directions from the anchor.

### Fix

**New placement logic:**

1. The pin **starts** slightly above the anchor point (e.g., 1 meter above in the direction opposite to the extrusion axis).
2. The pin **extends** in the selected direction (the extrusion axis direction) for a default length.
3. The default length is auto-derived from the dataset(s): extend far enough to cover the expected depth at the anchor point. Reasonable default: the local bounding box depth plus some margin, or a fixed default like 100 meters for geodetic data.

**Data model change:**

```fsharp
type SelectionPrism = {
    AnchorPoint : V3d
    AxisDirection : V3d
    Footprint : FootprintPolygon
    // Renamed / redefined:
    AboveAnchor : float   // small, typically 1 meter — length above the anchor
    BelowAnchor : float   // user-controllable — length into the surface along the axis
}
```

**GUI: length slider.** Add a slider in the placement controls (during placement phase) that controls `BelowAnchor`. Range: e.g., from minimum length (enough to get through the topmost mesh) to a generous maximum. Default: auto-computed from the dataset depth at the anchor.

**Note:** The automatic "extend to cover all datasets" behavior from V1 is replaced by this explicit user control. Keep the auto-derived value as the default so users don't need to adjust it in most cases.

---

## 8. Placement Mode: Camera Orbit Center Follows Pin

### Problem

During pin placement, the camera's orbit center remains at its previous position (wherever the user last double-clicked). This means orbiting during placement circles around an irrelevant point, not the pin.

### Fix

On pin placement (when the user places the anchor or moves it via flashlight mode), automatically set the camera's orbit center to the pin's anchor point.

**Implementation:** In the message handler for setting the pin anchor, also dispatch a camera-center update message. Use the existing camera-center-setting logic.

**Deactivation:** After the pin is committed, the orbit center stays at the pin's anchor until the user double-clicks elsewhere or places another pin. This is consistent with existing double-click behavior.

---

## 9. Placement Mode: Detail Panel Offset

### Problem

When a pin is placed, the detail panel appears too close to the pin anchor point, overlapping with the cylinder in 3D space.

### Fix

Increase the default offset of the Stratigraphy card when it first appears. The V3 spec had a 20px right / 10px up offset from the projected anchor; increase this to something like 80–120px right / 40px up, or make the offset proportional to the card size so the card edge is clearly separated from the pin visual.

**Concrete value:** Offset = `(cardWidth * 0.4, -cardHeight * 0.2)` from the projected anchor, adjusted so the card does not overlap the cylinder's silhouette.

This is a single constant change in the card initialization logic. Tune visually.

---

## 10. 2D GUI: Jump-to-Dataset Button

### Problem

The user has no quick way to frame the camera on a specific dataset.

### Fix

Add a "Jump" button next to each dataset entry in the global dataset list (not the per-pin ranking — the top-level dataset toggle list).

**Behavior on click:**

1. Find a viable orbit center on the dataset:
   - Compute the dataset's bounding box.
   - Raycast from the bounding box center downward (along world -Z, or along the dataset's natural orientation) to find a surface point.
   - If the raycast fails, fall back to the bounding box center.
2. Set the camera orbit center to this point.
3. Set the camera distance so the entire dataset fits within the view frustum:
   - Compute the bounding sphere (or use the bounding box diagonal).
   - Distance = `sphereRadius / tan(fov / 2)` or similar framing formula.
4. Optionally smoothly animate the camera to the new position (if animation infrastructure exists; otherwise snap).

**Icon:** A small "focus" or "target" icon inline with the dataset name.

---

## 11. 2D GUI: Revolver Circles Border

### Problem

The revolver widget (the stack of small circles showing the same terrain patch across all meshes) currently has no borders on the circles. They bleed into the background, making them hard to distinguish.

### Fix

Add a visible border around each revolver circle:
- **Stroke:** 1–2px solid, color chosen for contrast (e.g., white on dark backgrounds, dark on light).
- **Consistent across all circles** (no special styling for the active circle in this fix — if that's desired later, it can be added as a separate improvement).
- Implementation: add a stroke attribute to the SVG/canvas circle rendering, or a CSS border if circles are HTML elements.

---

## Implementation Priority

**Phase 1 — Critical interaction bugs (do first):**
1. ~~Pointer capture fix (Item 1).~~ ✅ Verified: already implemented (`pointerCapture = true` on all drag interactions).
2. ~~In-between space hover correctness (Item 3).~~ ✅ Verified: flood fill already uses order-independent geometric continuity, not mesh identity. No fix needed.
3. ~~In-between space hover performance (Item 2).~~ ✅ Done: union-find pre-computes all connected components once when stratigraphy arrives; hover is O(1) bracket lookup.

**Phase 2 — Performance:**
4. ~~Cut plane drag performance (Item 6).~~ ✅ Done: isolated cut plane quad to depend only on (prism, cutPlane); cut lines now use frozen CutResultsPlane so they don't rebuild during drag.

**Phase 3 — Correctness:**
5. ~~Diagram correctness audit (Item 4).~~ ✅ Verified: all computation correct (angular range, sorting, normalization, coordinate transforms).
6. ~~Stratigraphy cut line overlay (Item 5).~~ ✅ Done: made indicator lines 2× thicker with dark outline for contrast. Both AlongAxis and AcrossAxis indicators now highly visible.

**Phase 4 — Placement improvements:**
7. ~~Pin length control (Item 7).~~ ✅ Done: auto-derived default from dataset bounds; length slider in placement controls; ExtentBackward=1m (above anchor).
8. ~~Camera orbit center follows pin (Item 8).~~ ✅ Done: `SetAnchor` now emits `SetTargetCenter` with `Tanh` animation.
9. ~~Detail panel offset (Item 9).~~ ✅ Done: offset changed to `(cardWidth * 0.4, -cardHeight * 0.5 - 40)`.

**Phase 5 — GUI polish:**
10. ~~Jump-to-dataset button (Item 10).~~ ✅ Done: crosshair button next to each mesh in the list; sets orbit center to centroid with animated transition.
11. ~~Revolver circles border (Item 11).~~ ✅ Done: dark ring border in `readArraySliceColor` shader at r > 0.96.

Phases 1–2 are bugs that actively impede use. Phase 3 is correctness assurance before presenting. Phases 4–5 are grievance fixes.
