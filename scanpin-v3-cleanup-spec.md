# ScanPin V3 Cleanup — Interaction Refinement Specification

## Purpose

This document specifies the cleanup pass for ScanPin V3. The core stratigraphy diagram, ghost clipping, and extracted lines (up to implementation priority item 3.12) are complete. This cleanup addresses interaction conflicts, visual refinements, and a card-based GUI layout system (now complete).

The target reader is a Claude Code AI agent. Graphics stubs are marked `STUB(graphics)`.

---

## 1. Left-Click Interaction Model

### Problem

Left-click currently serves multiple conflicting roles: camera orbit, widget adjustment, pin placement, and camera center (double-click). With terrain-dominated views, there is rarely empty space to use as a disambiguation signal.

### Solution: Timing + Context + Mode

**Interaction decomposition:**

| Gesture | Behavior |
|---|---|
| **Short click** (< 200ms, < 5px movement) | Context-dependent single action (see below) |
| **Left drag** (> 5px movement) | Camera orbit OR widget drag OR flashlight reposition, depending on what the drag started on and the current mode (see below) |
| **Double-click** | Set camera orbit center to the clicked surface point. Always. No other behavior. |
| **Long-press** (> 400ms, no drag) | Reserved for V4 radial menu. No behavior in V3. |

### Short Click Behavior

Short click does exactly one thing based on priority-ordered hit testing:

1. **Hit a widget handle** (slider handle, snap zone, card button) → activate that widget.
2. **Hit the cylinder hull** of the currently edited ScanPin → set cut plane (see Section 2).
3. **During placement mode, hit a mesh surface** → set pin anchor position (existing behavior).
4. **Hit nothing relevant** → do nothing.

### Left Drag Behavior

What a drag does depends on what was under the cursor at drag start:

1. **Started on a selected widget handle** (card title bar, slider handle) → widget drag. The widget captures the interaction for the full drag duration.
2. **Started on the cylinder hull** of the currently edited ScanPin → continuous cut plane adjustment. The cut plane updates live as the user drags across the hull surface (see Section 2).
3. **Ctrl held AND started on a mesh surface during placement** → reposition the ScanPin (flashlight mode). The pin's anchor point follows the cursor across the terrain surface in real-time, like shining a flashlight. The stratigraphy diagram updates as the pin moves (or updates on release if real-time is too expensive).
4. **Otherwise** → camera orbit (existing behavior).

Priority: widget handles (1) > cylinder hull (2) > Ctrl+flashlight (3) > camera orbit (4).

### Flashlight Mode (Desktop Only)

Flashlight mode temporarily changes left-drag behavior from camera orbit to pin repositioning during placement.

**Activation:** Hold **Ctrl** while left-dragging on a mesh surface during pin `Placement` phase. Flashlight behavior is active only for the duration of that drag — release the mouse or release Ctrl and it ends. No persistent mode, no long-press. Visual feedback: the pin follows the cursor across the terrain surface in real-time.

**Behavior:** The pin's anchor point tracks the cursor's surface hit point as the user drags. The stratigraphy diagram updates on release (or in real-time if performance allows).

**Touch / long-press (deferred — V4):** A radial menu on long-press will offer flashlight and other mode switches for touch interfaces.

---

## 2. Unified Hull Picking for Cut Plane

### Concept

The user clicks or drags on the ScanPin cylinder's hull surface to set the cut plane position. The click/drag position provides both an angle (around the cylinder axis) and an axis-position (distance along the axis). The current cut plane mode determines which coordinate is used:

- **AcrossAxis mode:** The axis-position of the click sets the cut plane distance. The angular coordinate is ignored.
- **AlongAxis mode:** The angular coordinate of the click sets the cut plane rotation angle. The axis-position is ignored.

This replaces both the V3 rail-and-handle slider (for AcrossAxis) and the cap-disk picker (for AlongAxis). **Remove both of these.** The cylinder hull itself is the control surface.

### Interaction

**Short click on hull:** Set the cut plane to the clicked coordinate (axis-position or angle, per mode). Snap immediately.

**Drag starting on hull:** Continuous adjustment. The cut plane updates live as the cursor moves across the hull surface. This enables fine-tuning without repeated clicks.

**Visual feedback on the hull (cut plane indicator):**

- **AcrossAxis mode:** Render a **horizontal ring** on the cylinder hull at the current cut plane height. The ring is a thin line (1–2px visual width), rendered without depth test (always visible). Bright color (e.g., white or the pin's accent color).
- **AlongAxis mode:** Render a **vertical line** on the cylinder hull at the current cut plane angle (from bottom cap to top cap along the hull surface). Same rendering: thin, no depth test, bright color.
- Both indicators serve as "you are here" markers so the user can see the current cut plane position on the hull even before clicking.

### Linked Stratigraphy Diagram Interaction

The stratigraphy diagram already functions as a height slider (AcrossAxis mode). **Additionally implement angle picking in the stratigraphy diagram for AlongAxis mode:**

- In AlongAxis mode, clicking or dragging horizontally on the stratigraphy diagram sets the cut plane angle. The horizontal axis of the diagram corresponds to the angular coordinate (0°–360°).
- Visual indicator: a **vertical line** in the stratigraphy diagram at the current cut plane angle, styled as a thin bright line.

In both modes, the stratigraphy diagram and the 3D hull are fully linked — adjusting one updates the other.

**Summary of stratigraphy diagram interaction by mode:**

| Mode | Click/drag direction | Effect |
|---|---|---|
| AcrossAxis | Vertical (drag up/down) | Set cut plane axis-position (height) |
| AlongAxis | Horizontal (drag left/right) | Set cut plane angle |

---

## 3. Cut Plane Rendering Refinement

### Problem

The current solid white quad is too visually heavy and obscures the scene.

### Solution

Replace the solid quad with a subtle composite rendering:

**Outline rectangle:**
- Render only the four edges of the cut plane quad as thin lines (1–2px visual width).
- Color: white or pin accent color, ~60% opacity.
- No depth test (always visible through geometry, matching the cut lines).

**Gradient fill:**
- The interior of the quad has a very faint fill (~5–8% opacity white) that fades to transparent toward the center.
- Implementation: render a quad with vertex colors — edges at ~8% opacity, center at 0%. `STUB(graphics)`: This is a simple vertex-color gradient on a fullscreen-facing quad.

**Measurement ticks:**
- Along two opposite edges of the cut plane rectangle, render small perpendicular tick marks at regular intervals.
- Tick spacing: auto-scaled based on the cut plane's world-space extent (e.g., every 1m, 0.5m, 0.1m — pick the scale that gives roughly 5–15 ticks across the visible extent).
- Each tick is a short line segment (e.g., 5% of the plane width) perpendicular to the edge.
- At selected tick positions (e.g., every 5th tick), render a small dot or slightly longer tick instead of text — text rendering is deferred.

**Text rendering stub:**
- `STUB(graphics)`: The developer has a text rendering library in aardvark.dom that needs integration effort. For V3 cleanup, create the stub interface but use placeholder visuals (dots, longer ticks). The text rendering integration is a separate task.

```fsharp
/// Render a text label as a billboard in the 3D scene.
/// STUB(graphics): integrate with aardvark text rendering library.
/// For now, render a small colored dot at the label position as a placeholder.
val renderTextLabel3D : position:V3d -> text:string -> size:float -> color:C4b -> unit
```

---

## 4. Removed / Replaced Elements

| V3 Element | V3 Cleanup Action |
|---|---|
| Rail-and-handle 3D slider along cylinder axis | **Done.** Removed. Replaced by hull picking. |
| Cap-disk angle picker at cylinder top | **Done.** Removed. Replaced by hull picking. |
| Solid white cut plane quad | **Done.** Replaced with outline + gradient fill + measurement ticks. |
| Fixed-position HTML detail panel | **Done.** Replaced with card system. |
| Cut plane slider in HTML panel | **Done.** No HTML slider existed; hull picking + stratigraphy diagram interaction covers this. |

---

## 5. Implementation Priority

### Phase A: Card System — DONE
Card data model, rendering, drag, attach/detach, leader lines, viewport clamping, z-ordering, sub-card docking, migration of ScanPin GUI to cards.

### Phase B: Interaction Model — DONE (partial)
1. **Left-click decomposition** — DONE. Aardvark.Dom's built-in `Sg.OnTap`/`Sg.OnDoubleTap`/`Sg.OnLongPress` handle gesture recognition. Hull picking has priority over orbit via `Sg.OnPointerDown` returning `false`.
2. **Drag capture logic** — DONE. Hull `Sg.OnPointerDown` captures drag when clicking the cylinder hull; otherwise falls through to orbit controller.
3. **Flashlight mode** — DEFERRED. Requires real-time surface ray queries during drag, which are async server calls. Deferred to V4 when client-side BVH may be available.

### Phase C: Hull Picking — DONE
4. **Unified hull picking** — DONE. Transparent pickable cylinder hull; click/drag sets cut plane coordinate per mode. Analytical ray-cylinder intersection for continuous drag.
5. **Hull cut plane indicators** — DONE. Horizontal ring (AcrossAxis) and vertical line (AlongAxis) on hull, rendered without depth test.
6. **Stratigraphy diagram angle picking** — DONE. Horizontal click/drag in AlongAxis mode sets angle. Vertical blue indicator line in diagram.
7. **Remove old controls** — DONE. Rail+handle slider and cap-disk picker deleted from Revolver.fs.

### Phase D: Visual Polish — DONE
8. **Cut plane rendering** — DONE. Outline edges (white, 60% opacity), gradient fill (8% at edges, 0% at center), measurement ticks with auto-scaled spacing and dot placeholders at major ticks.
9. **Text rendering stub** — DONE. Major tick marks have dot placeholders at label positions. Full text integration deferred.

### Phase E: Deferred Stubs
10. **3D text rendering integration** — left as stub with dot placeholders at major tick positions.
11. **Radial menu component** — deferred to V4. Card system accommodates popup overlays.
12. **Flashlight mode** — deferred to V4. Requires client-side BVH or throttled server ray queries.
