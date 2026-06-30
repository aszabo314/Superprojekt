# Superprojekt — GUI & Interaction Specification

This document specifies, exactly, what the client presents and how the user interacts with
it. It is organised by **workflow state** (the three modes), and within each state by
**region** (workflow panel → focus panel → selection dock → 3D view). A final part
specifies every 3D visualization and overlay and how it is computed.

It describes the code as it is (`src/Superprojekt/*`), not an idealised design.

---

## 0. Frame of reference

### 0.1 The five fixed containers

The app is a single full-window layout. **Containers never move when the mode changes** —
only their *content* and which affordances are interactive change (cross-faded).

| Region | Module | Role |
|---|---|---|
| **Top bar** | `GuiTopBar` | global, mode-independent controls + cursor readout |
| **Left rail** (*workflow panel*) | `GuiRail` | *what object I work on* — one of three modes |
| **Right focus panel** | `GuiFocus` + `FocusScene` | *per-mesh spatial work* — WebGL single + tile strip |
| **Bottom dock** (*selection dock*) | `GuiInspector` | *the parts of the selected object* — mode-contextual |
| **Central 3D viewport** | `View` + `SceneGraph` | the main WebGL scene |
| **Overlays** | `GuiOverlays` | toast, scale bar, orientation gnomon, wheel label |

The left rail is shown only while `Model.MenuOpen` is true (hamburger toggles it).

### 0.2 Global state that drives everything

- `WorkflowStep ∈ { Overview, Correspondence, Inspect }` — the mode (left-rail selector).
- `RegView ∈ { RegBefore, RegAfter }` — global before/after toggle, **disabled until any
  mesh is solved**; flips the 3D view, the focus panel, and the dock together.
- `Registration.ReferenceMesh : string option` — the ★ reference; every error metric is
  relative to it.
- `MeshVisible : Map<mesh,bool>` and `MeshSolo : NoSolo | Solo(name, restore)` — visibility
  and hard isolation.
- `Selection = { SelectedPin; FocusedMesh; SelectedPoint; Hovered }` — the **one shared
  selection record**. Every region binds to it; linked highlighting across regions is a
  *consequence* of that binding, not a panel-to-panel event.

### 0.3 The interaction grammar (everywhere)

- **hover = peek** → writes `Selection.Hovered` via the single `SetHovered` message.
- **click = select / promote** → writes `SelectedPin` / `FocusedMesh` / `SelectedPoint`.
- **drag = edit** (camera, pan/zoom, radius slider).

`Selection.Hovered = HoverMesh m` (or `HoverPoint(_, m)`) **peek-isolates** mesh `m` in the
3D view: `m` renders solid, every other mesh drops to ghost. This is computed in
`View.wheelIsolation` and is **mode-independent** — it works from any list that emits
`SetHovered (HoverMesh …)`.

---

## 1. Global controls (identical in all three modes)

### 1.1 Top bar (`GuiTopBar`)

Left to right:

| Control | Message | Behaviour |
|---|---|---|
| **☰ hamburger** | `ToggleMenu` | show/hide the left rail (`MenuOpen`). |
| **⟲ camera reset** | `ResetCamera` | recenter the orbit camera on the first mesh's panorama centre; radius from scene bounds. |
| **👁 Peek** (hold) | `SetReferencePeek true/false` | spring-loaded: while held, only the reference mesh is solid; all others ghost at α 0.12. Released on pointer-up **and** mouse-leave so it cannot stick. Hotkey **R**. |
| **Before / After** | `SetRegView` | two buttons; the whole pair is disabled (`tb-regview-off`) until a `SolvedTransform` exists. Clicking sets `RegView` only when something is solved. |
| **cursor readout** | — | `world X Y Z` = metric-world point under the cursor (from `hoverCoord`); when a mesh is focused, also `shortname X Y Z` = that point minus the mesh centroid (its own frame). When the cursor ray misses every mesh, the readout drops to the render `Z=0` plane (dataset mean elevation). |
| **⚙ gear** | `ToggleGearPopover` | opens the settings/debug popover (below). |

### 1.2 Gear popover (settings & debug)

Each row, in order:

- **Dataset** — one button per dataset (`SetActiveDataset` + reload). Active highlighted.
- **Rendering** — `Textured` / `Shaded` / `SlopeColor` (`SetRenderingMode`).
- **Outline edge threshold** — slider `0.0001 … 0.01`, step `0.0001` (`OutlineThreshold`).
- **Isolines over Z range** — slider `4 … 2000`, step `1` (`IsolineBands`).
- **Difference heatmap range** — slider `0.1 … 5.0`, step `0.05`, label `×N.NN`
  (`DiffRangeScale`); multiplies the per-tile range of the Inspect difference map.
- **Camera speed** — slider `0.05 … 2.0` (`Camera.speed`).
- **Ghost silhouette** toggle + **Ghost opacity** slider `0 … 1` (`GhostSilhouette`,
  `GhostOpacity`).
- **Isolate pins** toggle (`AnchorGhostMode`) — auto-suspended (shown off + inert) while
  placing a pin.
- **Shading strength** slider `0 … 1` (`ShadingStrength`).
- **Slope threshold (°)** slider `1 … 89` (`SlopeThresholdDeg`).
- **Quick-pin radius (m)** number input `0.01 … 50`, step `0.005` (`QuickPinRadius`).
- **Dataset** info line (bounds + centroid), **per-mesh centroid** list, **debug log**.

### 1.3 Camera & viewport input (`View` + `OrbitController`)

`OrbitController` (a project file, not the library one):

- **Left drag** = orbit/rotate.
- **Middle drag** = pan, **locked to the world XY plane** (constant Z, mean-elevation).
- **Shift + left drag** = pan alias (for trackpads with no middle button; the mode is
  locked at press time).
- **Scroll** = zoom; speed scaled by `Camera.speed`.
- **Double-tap** (`Sg.OnDoubleTap`) = recenter: resolves the surface point under the cursor
  (ghost-aware, §6) and animates the orbit centre to it (`SetTargetCenter`, Tanh easing).

Held keys / modifiers on the canvas:

- **Space** held → `fullscreenActive`: a clean 3D view — the pin scene, coordinate cross,
  axis labels, and reference outline are hidden (`Sg.Active notFullscreen`); meshes and
  image-space outlines stay.
- **Alt / Option** held, or **Alt + wheel** → **layer isolation** (`ActivePickingLayer`):
  the wheel cycles the isolated mesh, preferring meshes stacked under the cursor (≥2 there),
  else all visible meshes in panel order. The isolated mesh is solid; others ghost at α 0.15.
  A floating **wheel label** shows the isolated mesh's numbered name by the cursor. With a
  layer active, picks prefer that layer's surface (server raycast) over the frontmost.
- **R** held → reference peek (same as 👁 Peek).
- **Esc** → cancel pin placement (`CancelPlacement`).

### 1.4 Overlays (`GuiOverlays`, DOM, always mounted)

- **Toast** — transient message bubble (`Model.Toast`), auto-clears; used for blocked/failed
  actions.
- **Scale bar** — a "nice round" metric length sized to ~100 px (§5.11).
- **Orientation gnomon** — a 60×60 SVG showing the world X/Y/Z axes projected through the
  current view (X red, Y green, Z blue), drawn back-to-front (§5.11).
- **Mesh-wheel label** — see Alt-wheel above.

---

## 2. Mode: **Overview**

> Purpose: pick the dataset's reference, set visibility/sensor metadata, get oriented.

### 2.1 Workflow panel (left rail) — *mesh list*

A row per mesh (`GuiRail.meshRow`). The whole row dims (`rail-row-dim`) when the mesh is
hidden and lights cyan (`rail-row-hover`) on hover. Contents and interactions:

| Element | Interaction |
|---|---|
| colour swatch + 1-based number | identity only |
| **name** | **click** → `SetFocusedMesh` (focus the panel on it) |
| **★ / ☆** ref button | **click** → toggle `SetReferenceMesh` (this mesh ⇄ none) |
| **● / ○** visible | **click** → `SetVisible` |
| **◐** isolate | **click** → `ToggleMeshSolo` (hard solo; click again restores) |
| **sensor** (Rover/Sat/…) | **click** → cycle `SetMeshSensorType` |
| **⌖** frame | **click** → `FlyTo` this mesh's bounds |
| (row) **hover** | `SetHovered (HoverMesh name)` → peek-isolate in 3D |

### 2.2 Focus panel

Head: **Focus** title · **Pano / Top** projection toggle · **⟲ reset** (pan/zoom).
(The **⊕ set point** and **⇄ ref** buttons are hidden outside Correspondence.)

- **Large single** = the focused mesh, full-res, atlas-**textured** (`FocusMode = 0`), in
  render space at its displayed (before/after) pose. **Top** = strictly orthographic;
  **Pano** = cylindrical unwrap from the mesh's panorama centre. Per-mesh **pan (drag)** and
  **mouse-anchored zoom (wheel)**; `⟲ reset` clears them.
- **Tile strip** = one textured thumbnail per *visible* mesh; **click a tile** →
  `SetFocusedMesh`. The focused tile is outlined (`fm-active`).
- No surface picking here in Overview (set-correspondence is a Correspondence affordance).

### 2.3 Selection dock — *mesh roster*

A table (`ins-roster`) with header `mesh · role · sensor · size · vs ref · ●`. One row per
mesh:

- swatch + numbered name; **role** (`★ ref` / `moving`); **sensor**; **size** (`N tris`);
  **vs ref** (`overlaps ✓` / `no overlap`, from bbox intersection, hidden for the reference);
  **● visible** toggle.
- **row click** → `SetFocusedMesh`; **row hover** → `SetHovered (HoverMesh)` (peek-isolate).

### 2.4 3D view

All meshes rendered per `RenderingMode` (textured by default), each with its image-space
**silhouette/cliff outline** and **world-Z isolines** in its palette colour (always on). The
**coordinate cross + integer-metre labels** sit at the first mesh's panorama centre. The
**reference mesh's bounding box** is outlined faintly in the accent colour. Pin markers,
rings, and verdict glyphs render if pins exist; **the correspondence constellation does
not** (it is Correspondence-only). Hovering a rail/roster row solos that mesh (others
ghost); **◐** solos persistently.

---

## 3. Mode: **Correspondence**

> Purpose: place pins, seed/edit per-mesh correspondence markers, and solve the rigid
> registration.

### 3.1 Workflow panel (left rail) — *pins + readiness*

- **Pins** header with **○ Place pin** (`EnterAnchorPlacement`; label flips to
  `○ placing… (Esc)`; toggles back with `CancelPlacement`).
- **Pin list** — a row per pin (`pinRow`): **name** (**click** → `SelectPin`), **✕** delete
  (`DeletePin`). **Hover** → `SetHovered (HoverPin id)` (peeks that pin's constellation).
  Selected row highlighted (`rail-pin-sel`).
- **Readiness diagnostics** (`rail-diags`) — the live verdict rows, each = severity icon +
  short text + optional **→** action button (`NavTo`). The text wraps (never truncates).
  Rules (`Readiness.compute`), evaluated in order:
  - no reference → blocker **"Set a reference (★)"** (→ highlight ref column).
  - no pins → blocker **"Need ≥3 pins"**.
  - per moving mesh with `<3` markers → blocker **"<mesh>: +N marker(s)"** (→ reseed it).
  - a pin missing markers on some meshes → warning **"<pin>: N without a marker"**.
  - ≥3 pins but near-collinear anchors → warning **"Pins near-collinear (…)"**.
  - reference set but no visible moving meshes → info **"No moving meshes to solve"**.
  - otherwise → ready **"Ready to align"** (→ run solve).

### 3.2 Focus panel

Head additionally shows **⊕ set point** and **⇄ ref**:

- **⊕ set point** (`ToggleCorrSetMode`) — shown only when *available*: in Correspondence,
  with a **selected pin**, a **non-reference focused mesh**, and not peeking the reference.
  Label flips to `⊙ aiming…` while on.
- **⇄ ref** (hold) — `SetFocusPeekReference`: re-renders the reference mesh in this frame
  for comparison.

The **single** (Top) overlays, for every pin: its **bounding-sphere circle** (true
`InnerRadius` footprint, render space) and a **screen-fixed cross+ring glyph** at *this*
mesh's anchor for that pin, drawn **always-on-top** (`RenderPass.passOne`, `DepthTest.None`).
Selected pin → yellow; others → mesh colour. **Top-only** (render-space lines can't ride the
Pano unwrap).

**Set-correspondence interaction** (when ⊕ is on): the cursor aims (no pan). A **move**
throttle-raycasts the cursor → `CorrPreviewComputed` (a live cyan ghost in the 3D view); a
**click** raycasts → `PickCorrespondenceAt(pin, mesh, world)`, which commits the marker
(stored mesh-local) and exits the mode. The ray is built in render space, carried to the
mesh's server frame via its displayed pose, hit on `/query/ray`, and mapped back (§6).

### 3.3 Selection dock — *correspondence manager*

If no pin is selected: **"◌ select a pin"**. Otherwise the manager (`ins-mgr`):

- **Head** — editable pin **name** (`RenamePin`) + **radius** log-slider `r`
  (`0.01 … 10000 m`, `SetInnerRadius`).
- **Reference row** — reference swatch/name + ★ + `ref`/`…` (whether its anchor is seeded).
- **Per moving+visible mesh row** (`managerRow`):
  - swatch + numbered name;
  - **state glyph**: `⊘` out-of-ROI · `✓` marker placed · `○` not yet placed;
  - **residual/spread**: solve residual in mm if present, else distance from the reference
    anchor in mm;
  - **⟳** reseed this mesh (`ReseedMesh`);
  - **⌖** focus it (`SetSelectedPoint` + `SetFocusedMesh`);
  - **⊕ / ⊙** set the point in the **3D view** (`StartCorr3DPick`) — isolates that mesh in
    the main viewport so the GPU pick lands on it alone;
  - **hover** any row → `SetHovered (HoverPoint(pin, mesh))` (brushes that marker in 3D).
- **Foot** — `k/n` (markers placed / in-ROI moving meshes) + **Solve** (`SolveCoarse`).

### 3.4 3D view

Everything from Overview **plus the correspondence constellation** (per pin: a small
wire-sphere + cross at each moving mesh's marker, a larger amber one at the reference point,
and lines from each marker to the reference). During placement the terrain drops to ghost
and the live hover shows as a blue **flashlight** preview sphere; the cyan **set-point ghost**
shows while aiming a correspondence (focus ⊕ or row ⊕). Picks fall through ghosts via a
server raycast, so placement works on ghosted surfaces.

---

## 4. Mode: **Inspect**

> Purpose: read the registration error — aggregate variance in 3D, per-mesh difference /
> displacement in the focus, distribution + shift in the dock.

### 4.1 Workflow panel (left rail)

- **Mesh list** (`inspectMeshRow`) — swatch + number + name, no reference button.
  **Hover** → peek-isolate in 3D; **click** → `ToggleMeshSolo` **and** `SetFocusedMesh`
  (isolate the mesh *and* focus the panel on it). A `◐` marks the soloed row; the focused
  row is highlighted (`rail-mesh-sel`).
- **Focused:** readout of the focused mesh's numbered name.
- **Difference** toggle — **M3C2 / Δz** (`ToggleExtrinsicZDiff`): which difference the focus
  difference channel shows.
- **Intrinsic** toggle — **Off / Incidence / Range / Shape** (`SetHeatmapMode`): the
  acquisition-quality heatmap painted on every mesh in 3D *and* the focus.

### 4.2 Focus panel

Head: title · Pano/Top · ⟲ reset (no ⊕/⇄). Tiles and single recolour by `InspectChannel`:

- **Difference** (`ChDifference`): per-vertex signed **M3C2** or vertical **Δz** to the
  reference, diverging **blue ↔ mid-grey ↔ red** about 0 (`FocusMode = 1`). The per-tile
  range is `robustHi × DiffRangeScale`. Reference tile and any mesh without data stay
  textured/grey.
- **Displacement** (`ChDisplacement`): solved moving meshes only. **Tiles** show a sequential
  **magnitude** heatmap of `|load → solved|` per vertex (`FocusMode = 2`). The **large
  single** shows a flat **white** surface (`FocusMode = 3`) plus **load→solved arrow
  glyphs** (exaggerated ~18 % of the fit extent, coloured light→dark blue by *true*
  magnitude) — forced to **Top** (arrows can't ride the Pano unwrap).

### 4.3 Selection dock

- **Head** — "Focus channel" + **Difference / Displacement** toggle (`SetInspectChannel`,
  drives the focus tiles).
- **Pin distribution** (HTML canvas) — for the selected pin's probe: per moving mesh, on a
  shared signed-distance (mm) axis, jittered raw probe samples ("rain") + a median/IQR box,
  with the **±LoD₉₅** band shaded; labelled Before/After by the current `RegView`. Empty
  states: "select a pin" / "probing…" / "no moving meshes probed".
- **Shift readout** (Displacement channel only) — the focused *solved* mesh's centroid
  displacement load→solved, split **total**, **vertical datum**, **horizontal**, **rotation**
  angle, derived client-side from its `SolvedTransform`.

### 4.4 3D view

The **central variance map** paints on the **reference** (`DistanceEncoding = 2`):
per-reference-vertex disagreement (std of every visible moving mesh's signed distance),
light-grey → red. Moving meshes drop to **faint ghost context** — *unless* an Intrinsic
heatmap is selected, which forces them solid and recolours them. The Intrinsic heatmaps
(incidence / range / shape) recolour every above-ghost fragment. Outlines, isolines, and the
coordinate cross stay on; the **constellation is hidden** (Correspondence-only). Pin verdict
glyphs remain.

---

## 5. The 3D visualizations & overlays — exact definitions

This part specifies how each pixel/line is produced. Two render passes feed the main
framebuffer (`RenderPass`): `passZero` (meshes, pins) then `passOne` (cross + labels,
always-on-top). One **offscreen** pass produces the image-space outlines.

### 5.0 Coordinate spaces (so the formulas below are unambiguous)

- **Server frame** (a mesh's own/absolute world) — OBJ coords + mesh centroid. Every
  `/query/*` coordinate is in this frame.
- **Metric world** — the app's single world (metres). Pin centres/radii, anchors-as-world,
  cursor world live here. Equals a mesh's server frame *at its load pose*.
- **Render space** — `(world − CommonCentroid) · datasetScale`, then the mesh's displayed
  rigid pose. Cameras/GPU use this.

Boundary helpers: `ScanPin.renderCentre / worldCentre / renderLength` (dataset similarity);
`RigidTransform.worldToRender / renderToWorld` (conjugate a pose); `displayedMeshT` /
`displayedWorld` (load pose, or solved pose at `RegAfter`).

### 5.1 Mesh forward pass — ghosting & α-gated depth (`MeshShader.shade`)

Per draw the shader receives `MeshActive`, `GhostOpacity` (the effective ghost α),
`RenderingMode`, `MeshColor`, the 32-slot `Blobs` + `BlobCount` + `AnchorGhost`, and the
encoding uniforms. The host computes the effective ghost α per mesh:

```
GhostOpacity_uniform =
    0.12                      if reference-peek is held and this ≠ reference
    0.15                      else if a mesh is isolated (Alt-wheel/hover) and this ≠ it
    model.GhostOpacity        else if GhostSilhouette on
    0.0                       else            (→ "no ghost" means invisible)
MeshActive =
    (peek target == this)  OR  (isolated == this)  OR  (visible AND not inspect-ghost)
```

`inspect-ghost` = Inspect mode, Intrinsic Off, and this is not the reference (so moving
meshes fade behind the variance map).

Per fragment:

```
inAnyBlob   = the fragment's world pos is within InnerRadius of any Blob
blobsActive = BlobCount>0 AND AnchorGhost≠0
blobComp    = blobsActive ? (inAnyBlob ? 1 : 0) : 1
ghost       = GhostOpacity_uniform
alpha       = MeshActive ? ghost + (1-ghost)*blobComp : ghost
if alpha < 1e-4: discard
fullySolid  = (not blobsActive) OR inAnyBlob
if MeshActive and not fullySolid: alpha = min(alpha, 0.98)     // pin ghost stays below solid
aboveGhost  = alpha > ghost + 1e-4
depth       = (alpha ≥ 0.99) ? gl_FragCoord.z : 1.0            // α-GATED DEPTH
```

The α-gated depth is the keystone: ghost/outside fragments write depth `1.0` (far), so they
never occlude and never produce pixel-picks — picks "fall through" ghosts (§6).

Colour: `color.rgb = baseRgb · shade`, where
`shade = 1 + (max(0.15, |n·toCam|) − 1)·ShadingStrength`, and `baseRgb` is chosen as:

- not `aboveGhost` → `MeshColor` (uniform palette — ghosts read as one flat silhouette).
- `RenderingMode = 1` (Shaded) → `MeshColor`; `= 2` (Slope) → `slopeCol`; else (Textured) →
  the texture sample.
- The variance / heatmap painters below then override **above-ghost** fragments.

`slopeCol` (SlopeColor mode): from `nz = |n.z|` vs `SlopeThreshold = sin(thresholdAngle)`:
above threshold smoothstep blue→white; below smoothstep blue→hot (warm).

### 5.2 Intrinsic heatmaps (above-ghost only; `SensorOrigin` = the mesh's panorama centre)

- **Incidence** (`HeatmapMode = 1`): `incid = |n · normalize(SensorOrigin − wp)|`. Piecewise
  red `(0.84,0.19,0.15)` → yellow `(0.99,0.85,0.30)` for `incid<0.5`, then yellow → green
  `(0.18,0.55,0.34)` for `incid≥0.5`. (Grazing = red, head-on = green.)
- **Range** (`HeatmapMode = 2`): `tr = clamp(|wp − SensorOrigin| / RangeMax)`, blue
  `(0.13,0.40,0.85)` → red `(0.86,0.20,0.15)`. `RangeMax` = farthest mesh-bbox corner from
  the sensor (render units), so an off-surface sensor origin doesn't skew it.
- **Shape** (`HeatmapMode = 3`): `ts = clamp(shapeQ / 0.75)`, red → green. `shapeQ` is the
  per-vertex mean over incident triangles of `4√3·Area / Σ(edge²)` (1 = equilateral,
  →0 = sliver), computed once on load.

### 5.3 Variance map (`DistanceEncoding = 2`, the Inspect central-3D aggregate)

Painted on the reference only. Per reference vertex `d = SurfaceDist` (the std of the visible
moving meshes' signed M3C2 distances): if `|d| < 1e20` (not a sentinel),
`tt = clamp(d / DistScale)`, light-grey `(0.945,0.961,0.976)` → red `(0.725,0.110,0.110)`.
`DistScale = robustHi` = the 95th-percentile of the finite `|values|` (floored 1e-3). The
`SurfaceDist` buffer aligns with `loaded.pos`; non-encoded meshes bind a zero buffer.

Data path: `region-distance` (reference vs each moving mesh, mode 0) → reduced per
reference vertex to a std by `Update.ensureVariance` (a `cnt ≥ 2` reduction that skips
sentinels), keyed by the reference mesh.

### 5.4 Image-space outlines (`OutlineView` + `OutlineGBuffer` + `OutlineEdge`)

The one offscreen pass, **always on**. Every **loaded** mesh (visible or ghosted) renders
into an MRT G-buffer:

- `target0 = (parity, 0, 0, gl_FragCoord.z)` — world-Z band parity in `.x`, **window depth**
  in `.w`.
- `target1 = (MeshColor.rgb, 1)` — palette colour + coverage mask.

A fullscreen edge-detect (`OutlineEdge`) samples the G-buffer at centre ±1 texel:

```
dEdge = max(|l.w + r.w − 2·c.w|, |u.w + d.w − 2·c.w|)     // SECOND difference of depth
iEdge = max over neighbours of |c.x − n.x|                // FIRST difference of parity
isEdge = (dEdge > OutlineThreshold) OR (iEdge > 0.5)
paint MeshColor where isEdge AND coverage(.w of target1) > 0.5
```

`dEdge` is the depth **Laplacian** (not the gradient): window-space `gl_FragCoord.z` is
linear across any planar primitive, so it is ~0 on a smooth slope at any angle/distance and
spikes only at a genuine **silhouette / cliff / occlusion** break. The useful
`OutlineThreshold` range is low because `target0` is `Rgba8` (256 depth levels;
1 LSB ≈ 0.004).

### 5.5 World-Z isolines (edge-detected in the same pass)

`parity = floor(wp.Z / ContourSpacing) mod 2` (a 0/1 step in `target0.x`). `iEdge` flags any
adjacent parity flip → a crisp 1px line welded to a fixed world-Z plane (it does not crawl as
the camera orbits). `ContourSpacing = max(1e-6, (SceneBounds.Size.Z / max(1, IsolineBands)) ·
datasetScale)`, shared across meshes so bands line up. Lines render in the mesh palette
colour. For a solved (posed) mesh the parity follows the displayed `wp.Z` (true displayed
height). Pure 1px, no AA — high `IsolineBands` will alias on steep faces by design.

### 5.6 Pin blobs (`MeshView.pinBlobUniforms`)

A 32-slot `V4f` array `(cx, cy, cz, innerRadius)` in **render space**: each pin's
`renderCentre` + `renderLength InnerRadius`, plus — while placing — the live hover as a
transient **flashlight** blob sized to `QuickPinRadius`. `AnchorGhost = 1` while placing or
when "Isolate pins" is on, else 0. These drive `blobComp` in §5.1: with isolation on the
terrain is solid only inside a pin (or the hover), ghost elsewhere.

### 5.7 Pin scene (`ScanPinScene`, render space, fixed render size independent of pin radius)

- **`pinDots`** — small **invisible** (α 0) icosphere pick proxies carrying the select tap
  (`Sg.OnTap` → `SelectPin`); written into the depth/id pass. Present in all modes
  (`notFullscreen`).
- **`pinMarkerLines`** — the visible pin-centre marker: a small **wire-box jack**, yellow if
  selected, brighter red on hover, else red. Drawn on top (`DepthTest.None`).
- **`pinRings`** — the influence ring: an **equator ring** ⊥ the probe axis at radius
  `InnerRadius`, a thin 1 m axis indicator, and the cached **sphere–surface contact rings**
  per *visible* mesh, in the pin's colour. **Depth-tested** (occlusion is the spatial cue).
- **constellation** (`constLines`) — the correspondence point markers (§3.4). **Gated to
  Correspondence mode** (`constellationActive = notFullscreen ∧ WorkflowStep=Correspondence`)
  and drawn on top. Per pin with a reference anchor: amber wire-sphere(0.07)+cross(0.09) at
  the reference point; per *visible in-ROI* moving mesh: a wire-sphere(0.055)+cross(0.07) in
  the mesh colour at the anchor's **displayed** world position + a line to the reference.
  Selection/row-hover brighten; out-of-ROI omitted.
- **`pinGlyphs`** — far-view verdict pole+ring head per pin: **green** if every moving mesh's
  `|median| ≤ LoD₉₅`, **red** if any is significant, **grey** with no probe; height grows
  with the max `|median|`.
- **`ghostPreview`** — placement-hover blue sphere shell + outline at `QuickPinRadius`.
- **`corrPreview`** — cyan wire-sphere+cross at `CorrPreview` (the live set-correspondence
  aim), drawn on top.

Marker world positions follow the displayed (before/after) transform because anchors are
mesh-local.

### 5.8 Coordinate cross + labels (`SceneGraph`, `passOne`, `DepthTest.None` → always on top)

At the first mesh's panorama centre (render space): a small centre box, three axes length 3
(X red, Y green, Z blue), tick marks every 0.25, integer-metre number labels every 4th tick,
and X/Y/Z tip letters. Hidden while Space (fullscreen) is held.

### 5.9 Reference outline (`SceneGraph.referenceOutline`)

The reference mesh's bounding-box edges in the accent colour `(0.102,0.337,0.859,0.5)`,
depth-tested (unobtrusive), following its displayed pose.

### 5.10 Focus tiles (`FocusShaders.focusColor`, `FocusScene`)

Per-vertex-coloured mesh, one fragment shader switched by `FocusMode`:

- `0` → texture pass-through.
- `1` → **difference**, diverging about 0: within `±FocusLod` → mid-grey `(0.56,0.57,0.60)`;
  else `tt = clamp(s/FocusHi, −1..1)`, grey→red `(0.863,0.149,0.149)` for `tt≥0`, grey→blue
  `(0.145,0.388,0.922)` for `tt<0`. The zero point is grey (not white) so it reads against
  the light page. `FocusHi = robustHi(FocusDist[mesh]) · DiffRangeScale` (the slider scales
  the range without re-uploading the vertex buffer).
- `2` → **displacement** magnitude, sequential neutral `(0.933,0.949,0.965)` → blue
  `(0.114,0.306,0.847)`, `t = clamp(|s|/FocusHi)`; `FocusHi = robustHi(|load→solved|)`.
- `3` → flat white (the displacement single under the arrow glyphs).
- `|s| ≥ 1e20` (sentinel) → no-data grey `(0.886,0.910,0.941)`.

`FocusDist[mesh]` comes from `region-distance` (target = the moving mesh) via
`Update.ensureFocusDist`, in the mesh's served vertex order.

**Pano vertex shader** (`FocusShaders.pano`): from the eye `PanoEye`, `u = atan2(y,x)/π`,
`v = atan2(z, hyp)/(π/2)`, mapped to clip by `(PanoCenter, PanoZoom, PanoAspect)`; depth =
normalised radial distance so the nearest surface occludes. Composed after
`DefaultSurfaces.trafo` so WorldPosition (and thus picking) survives. **Top** uses a
hand-built orthographic matrix framing the same eye.

### 5.11 Overlays — computation

- **Scale bar**: from the camera radius, 90° vertical fov, and viewport height,
  `renderPerPixel = 2·tan(fov/2)·radius / height`; the real length at the 100 px target is
  `100·renderPerPixel / datasetScale`, snapped to a "nice" 1/2/5×10ⁿ value; the bar width is
  that value back in pixels (clamped 10…400). Label in cm/m/km.
- **Orientation gnomon**: project world axes `X/Y/Z` through `view.Forward.TransformDir`,
  draw each as a 22 px line from centre (`x_screen = cx + a.x·L`, `y_screen = cy − a.y·L`),
  sorted by depth (`a.z`) so near axes draw last; label axes whose `z > −0.2`.

---

## 6. Picking & raycast resolution (how a click becomes a 3D point)

The instant **GPU pixel pick** uses `Sg.OnTap / OnDoubleTap / OnPointerMove` with
`e.Location.Depth < 0.9999` as the background/ghost gate (the α-gated depth leaves ghosts and
misses at depth `1.0`). When the GPU pick misses (cursor over a ghost or background) the
gestures that need a real 3D point fall back to a **server raycast**:

- `pickRay(cursorPx, viewportSize, view, proj)` → a metric render-space ray (cursor px is
  CSS px; `ClientSize` is used on the main control, with a framebuffer fallback).
- `resolveLayerPick` — if `ActivePickingLayer` is set and the ray hits its bbox, raycast that
  mesh on the server (un-applying its displayed pose); else the frontmost GPU pick.
- `raycastNearest` — bbox-cull visible+loaded meshes, fan out parallel `/query/ray`, take the
  **nearest** hit (first surface crossed). Ghost-agnostic (the server ignores the GPU ghost).
- `raycastMesh name` — raycast one specific mesh (for the isolated 3D correspondence pick).
- `resolvePick` — GPU/layer pick → `raycastNearest` fallback.

Each `/query/ray` converts the render ray to the mesh's server frame via `displayedWorld`
(`renderToWorld`), and maps the hit back through `displayedWorld.Forward`. What a tap does
then depends on state:

- `Corr3DPick = Some(pin, mesh)` → commit `PickCorrespondenceAt` and exit (no ROI gate).
- `AnchorPlacement` → `PlaceAnchor world` (the pin is created immediately, becomes selected,
  placement ends).
- otherwise → update the cursor world readout only (pin selection is via the invisible
  `pinDots` proxies' own `Sg.OnTap`).

The focus panel does the same conceptually but **Dom-driven** (`FocusScene.worldRayHit`):
the `Sg` pick did not fire reliably in the secondary control, so it inverts the cursor to a
render ray by hand (ortho drop for Top / pano direction for Pano), carries it
render → metric world → server frame, hits `/query/ray`, and maps back.

---

## 7. Server queries — what fires and when

All `/api/query/*` coordinates are in the queried mesh's **server frame** (the server does
`localPos = worldPos − meshCentroid`); multi-mesh queries pass each mesh's
`displayedWorld.Forward` and let the server place them.

| Endpoint | Triggered by | Feeds |
|---|---|---|
| `/query/ray` | every pick fallback (main + focus) | 3D point under cursor |
| `/query/closest` | pin placement / reference change / ⟳ (auto-seed) | correspondence `RefAnchor` + per-mesh anchors, ROI-clamped |
| `/query/probe` | pin selection / invalidation (debounced, generation-guarded) | the dock **distribution** + the ±LoD₉₅ band + `pinGlyphs` verdict |
| `/query/contact-rings` | radius/centre/pose change (debounced, per-pin) | `pinRings` contact rings |
| `/query/region-distance` | Inspect entered / `RegView` toggled (debounced) | the **variance** map (mode 0, reduced to per-vertex std) **and** the focus **difference** tiles (mode 0/1, per moving mesh) |
| `/query/lsq-pairs` | **Solve** (per visible moving mesh with ≥3 pairs, parallel) | each mesh's `SolvedTransform`; flips `RegView = RegAfter` |

`region-distance` mode 0 (M3C2) sentinels any vertex whose nearest reference point is farther
than `regionMaxDistFrac (0.02)` × the queried mesh's bbox diagonal, so non-overlapping points
paint no spurious error. All heavy queries fan out in parallel and are debounced with a
`CancellationTokenSource` + generation counter so at most one fetch is in flight per
invalidation.
