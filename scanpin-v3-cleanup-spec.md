# ScanPin V3 Cleanup — Interaction Refinement Specification

## Purpose

This document specifies the cleanup pass for ScanPin V3. The core stratigraphy diagram, ghost clipping, and extracted lines (up to implementation priority item 3.12) are complete. This cleanup addresses interaction conflicts, visual refinements, and a new card-based GUI layout system.

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

**Touch / long-press (deferred — V4):** A radial menu on long-press will offer flashlight and other mode switches for touch interfaces. Note: when implementing the card system's HTML/CSS library, structure it to accommodate popup overlays for this future radial menu.

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
- At selected tick positions (e.g., every 5th tick), render a small label with the distance value. **For now, render a small dot or slightly longer tick instead of text** — text rendering is deferred (see below).

**Text rendering stub:**
- `STUB(graphics)`: The developer has a text rendering library in aardvark.dom that needs integration effort. For V3 cleanup, create the stub interface but use placeholder visuals (dots, longer ticks). The text rendering integration is a separate task.
- Planned text label locations: measurement ticks on cut plane edges, dataset labels near cut lines, pin ID near anchor point.

```fsharp
/// Render a text label as a billboard in the 3D scene.
/// STUB(graphics): integrate with aardvark text rendering library.
/// For now, render a small colored dot at the label position as a placeholder.
val renderTextLabel3D : position:V3d -> text:string -> size:float -> color:C4b -> unit
```

---

## 4. Card-Based GUI Layout System

### Overview

A lightweight 2D layout system for draggable, dockable panels ("cards") rendered as HTML/CSS elements over the 3D viewport. Written in aardvark.dom's Elm-style architecture, translated to HTML+CSS.

This system replaces the current fixed-position detail panel. All ScanPin GUI elements (stratigraphy diagram, controls, dataset ranking) become cards within this system.

### Card Data Model

```fsharp
type CardId = CardId of System.Guid

/// What a card is anchored to.
type CardAnchor =
    /// Anchored to a 3D point in the scene (projected to screen each frame).
    | AnchorToWorldPoint of V3d
    /// Anchored to an edge of a parent card.
    | AnchorToCard of parentId:CardId * edge:CardEdge

type CardEdge = Top | Bottom | Left | Right

/// The attachment state of a card.
type CardAttachment =
    /// Card follows its anchor (moves when camera orbits or parent card moves).
    | Attached
    /// Card is at a fixed screen position. A leader line connects it to its anchor.
    | Detached of screenPos:V2d
    /// Card is being dragged by the user.
    | Dragging of currentPos:V2d

type Card = {
    Id : CardId
    /// What this card is anchored to.
    Anchor : CardAnchor
    /// Current attachment state.
    Attachment : CardAttachment
    /// Size of the card in pixels (fixed per card type, not resizable).
    Size : V2d
    /// Content identifier — determines what is rendered inside the card.
    Content : CardContent
    /// Whether the card is currently visible.
    Visible : bool
    /// Z-order for overlapping cards (higher = on top).
    ZOrder : int
}

type CardContent =
    | StratigraphyDiagram of ScanPinId
    | PinControls of ScanPinId
    | DatasetRanking of ScanPinId
    // Future: other card types

type CardSystemModel = {
    Cards : Map<CardId, Card>
    /// Which card is currently being dragged (at most one).
    DraggedCard : CardId option
    /// Viewport size (for clamping).
    ViewportSize : V2d
}
```

### Card Behavior

**Initialization:**
- When a ScanPin is selected, its cards are created (or made visible if they already exist).
- The main stratigraphy card starts `Attached` to the pin's 3D anchor point (`AnchorToWorldPoint`).
- Sub-cards (controls, ranking) start `Attached` to the stratigraphy card's edges (`AnchorToCard`).
- Default docking: controls card attached to bottom edge of stratigraphy card, ranking card attached to right edge.

**Attached positioning:**

For `AnchorToWorldPoint`:
- Project the 3D anchor point to screen coordinates each frame.
- Position the card with a fixed offset from the projected point (e.g., 20px to the right and 10px up, so the card doesn't overlap the pin itself).
- The offset direction can be chosen to keep the card on-screen (prefer the side of the screen with more space).

For `AnchorToCard`:
- Position the sub-card flush against the specified edge of the parent card.
- If multiple sub-cards are attached to the same edge, stack them along that edge (e.g., two cards on the bottom edge stack vertically downward).

**Dragging:**

1. User mouse-downs on the card's title bar area (a designated drag handle region at the top of the card, e.g., 24px tall).
2. Card enters `Dragging` state. It follows the cursor with the initial click offset preserved.
3. While dragging, the **snap zone** is visualized:
   - For a card anchored to a world point: a highlighted circle (e.g., 40px diameter, pulsing glow) at the projected anchor position.
   - For a sub-card anchored to a parent card edge: highlighted strips along the parent card's edges (e.g., 20px wide glowing regions on each edge).
4. User releases the card:
   - If the card center (or a designated snap point) is within the snap zone → `Attached`. Card snaps back to its anchor.
   - Otherwise → `Detached` at the release position.

**Leader line (when Detached):**

- A thin line (1px, dashed or dotted, semi-transparent) drawn from the card's nearest edge to the anchor point.
- For world-anchored cards: line goes from card edge to the projected 3D point.
- For card-anchored sub-cards: line goes from sub-card edge to the parent card's edge.
- Render as an SVG overlay or CSS-positioned div. Keep it visually subtle — it's a spatial reference, not a primary visual element.
- The line uses a gentle bezier curve (one control point offset perpendicular to the straight line by ~30px) for a softer appearance.

**Viewport clamping:**

All cards are clamped to stay fully within the viewport boundaries at all times:

```
// Pseudocode: clamp card position so the entire card is visible
clampedX = clamp(cardX, 0, viewportWidth - cardWidth)
clampedY = clamp(cardY, 0, viewportHeight - cardHeight)
```

This applies in all states:
- `Attached`: after computing the position from the anchor, clamp to viewport. If the anchor projects off-screen, the card sticks to the nearest viewport edge.
- `Detached`: the stored screen position is clamped whenever the viewport resizes.
- `Dragging`: the card position during drag is clamped (the user cannot drag a card off-screen).

When an `Attached` card is clamped (its anchor is near or off the edge of the viewport), the leader line appears automatically (same as `Detached`) to show the user where the anchor is. The card is still considered `Attached` — it will follow the anchor back on-screen when the camera moves.

**Z-ordering:**

- Clicking (or starting to drag) a card brings it to the top of the z-order.
- Implementation: increment a global z-counter and assign it to the interacted card's `ZOrder`. Use CSS `z-index`.

### Card Messages

```fsharp
type CardMessage =
    | StartDragCard of CardId * mouseOffset:V2d
    | DragCard of V2d                          // current mouse position
    | EndDragCard                               // release
    | ToggleCardVisibility of CardId * bool
    | BringToFront of CardId
    | ViewportResized of V2d                    // update clamping
```

### Card Visual Design

Each card is an HTML div with:
- A **title bar** (drag handle): 24px tall, subtle background color (e.g., semi-transparent dark), card title text on the left, a close/minimize button on the right.
- A **content area** below the title bar: renders the card's content (stratigraphy diagram render target, toggle buttons, scrollable list, etc.).
- **Border:** 1px solid, semi-transparent, subtle. Rounded corners (4px).
- **Background:** Semi-transparent dark (e.g., rgba(30, 30, 30, 0.85)) so the 3D scene is faintly visible through the card.
- **Shadow:** Subtle drop shadow for visual separation from the scene.

Sub-cards when attached flush to a parent: the adjacent borders are hidden (the cards appear to merge visually). When detached, the full border reappears.

### ScanPin Card Structure

When a ScanPin is selected, three cards are created:

**1. Stratigraphy Card (main card)**
- Anchor: `AnchorToWorldPoint(pin.AnchorPoint)`
- Size: e.g., 400×300 px (the stratigraphy diagram needs horizontal space for angular resolution and vertical space for axis range).
- Content: the stratigraphy diagram (OpenGL render target), plus the cut plane mode toggle (AcrossAxis / AlongAxis) as a small button bar at the top of the content area.

**2. Controls Card (sub-card)**
- Anchor: `AnchorToCard(stratigraphyCardId, Bottom)`
- Size: e.g., 400×100 px
- Content: toggle checkboxes (Ghost Clip, Cut Lines, Cylinder Edge Lines), Explosion enable + slider, Stratigraphy display mode (Undistorted / Normalized).

**3. Ranking Card (sub-card)**
- Anchor: `AnchorToCard(stratigraphyCardId, Right)`
- Size: e.g., 200×300 px
- Content: scrollable dataset ranking list with drag-reorder, visibility cutoff, count dropdown.

---

## 5. Removed / Replaced Elements

| V3 Element | V3 Cleanup Action |
|---|---|
| Rail-and-handle 3D slider along cylinder axis | **Remove.** Replaced by hull picking (Section 2). |
| Cap-disk angle picker at cylinder top | **Remove.** Replaced by hull picking (Section 2). |
| Solid white cut plane quad | **Replace** with outline + gradient + measurement ticks (Section 3). |
| Fixed-position HTML detail panel | **Replace** with card system (Section 4). |
| Cut plane slider in HTML panel | **Remove.** Replaced by hull picking + stratigraphy diagram interaction. |

---

## 6. Implementation Priority

### Phase A: Card System (independent — do first)
1. **Card data model and messages.**
2. **Card rendering** — HTML/CSS divs with title bar, content area, border, background, shadow.
3. **Drag interaction** — title bar drag, cursor tracking, snap zone visualization.
4. **Attach/detach logic** — snap zone hit test on release, state transitions.
5. **Leader lines** — SVG/CSS bezier curves from detached cards to anchors.
6. **Viewport clamping** — all card states clamped to viewport boundaries. Leader line for clamped attached cards.
7. **Z-ordering** — bring-to-front on interaction.
8. **Sub-card docking** — flush edge attachment, stacking, detach/reattach to parent edges.
9. **Migrate ScanPin GUI to cards** — stratigraphy card, controls card, ranking card.

### Phase B: Interaction Model
10. **Left-click decomposition** — implement the timing-based gesture recognition (short click, drag, double-click, long-press reserved).
11. **Drag capture logic** — priority-based drag target resolution (widget > hull > Ctrl+flashlight > orbit).
12. **Flashlight mode** — Ctrl+drag to reposition pin during placement (desktop only).

### Phase C: Hull Picking
13. **Unified hull picking** — click/drag on cylinder hull sets cut plane coordinate per mode.
14. **Hull cut plane indicators** — horizontal ring (AcrossAxis) and vertical line (AlongAxis) on cylinder hull, no depth test.
15. **Stratigraphy diagram angle picking** — horizontal click/drag in AlongAxis mode sets cut plane angle. Vertical line indicator in diagram.
16. **Remove old controls** — delete rail slider, cap-disk picker, HTML cut plane slider.

### Phase D: Visual Polish
17. **Cut plane rendering** — outline rectangle, gradient fill, measurement ticks with dot placeholders.
18. **Text rendering stub** — interface definition, placeholder dots at label positions.

### Phase E: Deferred Stubs
19. **3D text rendering integration** — leave as stub with dot placeholders.
20. **Radial menu component** — note in the card system code that a popup overlay system will be added in V4.
