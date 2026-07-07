# GUI & Interaction Reference (as-is)

An honest, complete description of the user interface, its interactions, and how
every visualization is actually computed and displayed. Written for review of the
current build; no aspirational content. Where a mechanism has a known
approximation, cap, or inconsistency, it is stated as such.

The application registers and inspects multi-epoch 3D scans of the same terrain:
several textured meshes ("epochs") are loaded into one shared world, one of them
is designated the **reference**, correspondence points are placed, a rigid
alignment is solved per moving mesh, and the residual differences are inspected
as false-colour layers and distributions.

---

## 1. Screen layout

```
┌───────────────────────────────────────────────────────────────────────┐
│ top bar: ☰ · ◎Isolate · 🎨Overlays · Before/After/Peek · coords · ⚙   │
├───────────┬───────────────────────────────────────────┬───────────────┤
│ left rail │                                           │ focus panel   │
│ (3-step   │         main 3D viewport                  │  large 2D     │
│ workflow) │         (orbit camera, WebGL)             │  single view  │
│           │                                           │  + mesh tiles │
├───────────┴───────────────────────────────────────────┴───────────────┤
│ bottom dock: mode-contextual (summary · pin manager · distributions)  │
└───────────────────────────────────────────────────────────────────────┘
```

- **Top bar** — global toggles and holds, live cursor coordinate, settings gear.
- **Left rail** — the workflow stepper (Overview · Correspondence · Inspect);
  exactly one step's body is expanded. Collapsible via the burger button;
  entering pin placement forces it open and restores its prior state afterwards.
- **Main 3D viewport** — the only orbit-camera view; fills the space between the
  bars. Its height is tied to the dock height, so resizing the dock resizes it.
- **Focus panel** (right) — a large 2D single-mesh view over a strip of one
  thumbnail tile per mesh. Resizable by dragging its left edge (width
  280–820 px; the single's height stays locked to 0.72 × width).
- **Bottom dock** — always mounted, full width; content cross-fades with the
  workflow step. Resizable by dragging its top edge (120 px to 60 % of the
  window); the 3D viewport and all bottom-anchored overlays follow.
- Floating overlays over the 3D view: scale bar (bottom left), colour legend
  (bottom centre, Inspect only), orientation gizmo (bottom right), toast
  messages, and — while the Overlays hold is down — 2D pin name tags.

---

## 2. The interaction grammar

One grammar is used everywhere; every panel is expected to obey it:

- **Hover = peek.** Hovering a mesh row, tile, matrix column, or pin chip
  momentarily emphasizes that object everywhere (3D isolation ghosting, chart
  layer highlight, matrix row highlight) and never changes state.
- **Click = select / promote.** Single click sets selection (focused mesh,
  selected pin) and applies mode policies. A click never moves a camera.
- **Double-click = zoom.** Double-click is the only mouse gesture that moves
  cameras: it re-frames the 3D camera and, where linked, the focus panel too.
- **Drag = edit.** Dragging manipulates (camera, brush range, panel sizes).
- **Hold = spring-loaded modifier.** Several buttons/keys act only while held
  and revert on release (Peek, Isolate, Overlays, hide-annotations).

Where a single click *toggles* state (matrix cells, 3D pin dots), the click is
deferred by one double-click window so a double-click does not toggle twice on
its way to the zoom. Honest caveat: a *slow* double-click can still let the
deferred single click fire in between; double-click handlers are therefore
written to end in an absolute state (select + zoom) rather than a toggle, so the
end state is correct even when that happens — but a brief flicker of the
intermediate state is possible.

Selection and camera are strictly separated in the architecture: selecting
never moves a camera; camera motion only ever originates from double-clicks
(or the automatic fly on dataset load / pin placement).

---

## 3. Global state that shapes everything

- **Workflow step** (Overview / Correspondence / Inspect) — switches the rail
  body, the dock content, the 3D ghosting policy, and which false-colour layers
  can paint. Switching steps ends mesh isolation (see below), clears the hover,
  any armed correspondence edit, the sample brush, and any "locate" state.
- **Selection** — one shared record: the selected pin, the focused mesh, and the
  hovered target (a pin, a mesh, or a pin×mesh cell). All cross-panel linking is
  a consequence of panels reading this one record; no panel talks to another
  directly.
- **Visibility** — a per-mesh show/hide toggle set, plus a separate one-mesh
  **isolation** overlay ("◐"). Isolation never edits the toggles while active,
  but *exiting isolation resets every toggle to ON* — a hidden-mesh set is not
  restored (deliberate, but lossy). While isolated, the visibility toggles are
  locked. Everything that decides "shown/clickable" (rendering, ray picking,
  contact-ring display, constellation, distribution lanes) goes through the same
  isolation-then-toggles rule.
- **Registration poses** — every mesh has an immutable load pose and, once
  solved, a solved pose. A global **Before/After** toggle selects which one is
  displayed; it is disabled until at least one mesh has been solved. Solving
  automatically switches the view to After. A spring-loaded **Peek** hold shows
  the *other* pose momentarily; the peek is purely visual — no query, statistic,
  or stored value ever reads the peeked pose.
- **Reference mesh** — chosen by the ★ button on a focus tile. All error is
  relative to it. Changing the reference discards any existing solve, snaps the
  view back to Before, invalidates all probes and contact rings, and re-seeds
  correspondence markers against the new reference.

### Spring-loaded holds (all revert on release)

| Hold | Where | Effect |
|---|---|---|
| **Peek** | top bar button | Displayed poses flip Before↔After; the dock histogram flips which pose is emphasized. Visual only. |
| **◎ Isolate** (key `I`) | top bar button | Forces pin isolation on in modes where it defaults off: terrain drops to ghost except inside pin regions. |
| **🎨 Overlays** (key `O`) | top bar button | Meshes paint plain white (shading kept), pin flag poles become thick and pin-coloured, and 2D name tags appear at the flag tips. Every false-colour layer is overridden while held. |
| **Space** | keyboard | Hides all 3D annotation (pins, rings, constellation, coordinate cross, axis labels, reference/focus outlines) — a clean terrain view. |
| **Alt** | keyboard | Activates the "picking layer": the layer mesh renders solid, the rest ghost; picks prefer it. Alt+wheel cycles which mesh is the layer (meshes stacked under the cursor first, else all visible ones); a small label at the cursor names the current layer. |

---

## 4. The main 3D viewport

### 4.1 Camera and navigation

Orbit camera around a centre point: **left-drag rotates**, **middle-drag pans**
(pan re-derives its centre from the surface under the cursor so the world
appears to stick to it), **wheel dollies** in/out, **double-click re-centres**
the orbit on the surface point under the cursor (animated). Rotation speed is
adjustable in the gear menu. On dataset load the camera flies to frame the
whole scene, resting on the first mesh's panorama centre. The projection is a
perspective camera with a 90° *horizontal* field of view.

### 4.2 Picking (how clicks find the surface)

Two mechanisms cooperate:

- A **GPU pixel pick**: the renderer maintains a picking buffer alongside the
  colour image; the click reads the front-most depth at the pixel. Fully
  transparent ("ghost") surface fragments deliberately do not register in it, so
  a pick falls *through* ghosted meshes.
- A **server ray cast**: the cursor is inverted to a world ray and intersected
  with the actual mesh geometry on the server (a BVH intersection), ignoring
  all display transparency. Used as the fallback whenever the GPU pick misses
  (background or ghost), and as the primary mechanism wherever the GPU pick is
  unusable (pin placement, the focus panel).

Both honour the displayed Before/After pose: the ray is transformed into the
mesh's own frame before intersection and the hit transformed back, so picking is
pose-correct in both views. Hover-driven ray casts are throttled to roughly one
request per 60 ms with stale-response protection, so a fast mouse can be up to
one throttle interval behind. With Alt held, picks prefer the active picking
layer if the cursor ray passes through its bounding box, regardless of what is
in front.

Clicking terrain (not placing, not editing): the click focuses the front-most
mesh under the cursor. Double-click re-centres the orbit. There is no
click-to-deselect on background.

### 4.3 Surface rendering

One forward render pass draws all meshes, then pin geometry, then always-on-top
annotation (coordinate cross, axis labels, overlay lines). Base surface colour
comes from one of three global modes (gear menu):

- **Textured** — the photo texture.
- **Shaded** — flat per-mesh palette colour.
- **Slope** — colour by surface-normal verticality: flat areas fade white,
  steeper-than-threshold areas fade to a warm tone through blue, with a
  smoothstep transition around a user-set slope threshold (default 15°).

Diffuse-style shading (strength adjustable, default subtle) multiplies the base
colour. Every mesh also renders when *not* emphasized as a **ghost**: a uniform
per-mesh-colour silhouette at a global ghost opacity (default 0.12). If the
"ghost silhouette" toggle is off, de-emphasized meshes are hidden entirely
instead of ghosted. Ghost fragments always use the flat palette colour (never
texture or false colour) so a ghost reads as one shape, and they are excluded
from picking (see 4.2).

**Pin isolation** ("isolate pins") ghosts everything *outside* pin regions: a
fragment is solid only if it lies inside any pin's sphere of influence (up to 32
pins are honoured by the shader; further pins do not isolate). It is the default
in Correspondence, off in Overview/Inspect (the `I` hold forces it), forced on
during pin placement (a "flashlight" that previews the exact region a click
would claim), and toggled automatically by Inspect's selection policies (see §6).

**Per-mode emphasis** (who is solid vs ghost) resolves in this order:
1. a hover/Alt/armed-edit isolation target (that mesh solid, rest ghost);
2. mesh isolation ◐ (isolated mesh solid, rest ghost);
3. per-mesh visibility toggles, and in Inspect's ensemble view all moving
   meshes drop to ghost (the reference carries the aggregate layer) unless a
   mesh has its own intrinsic heatmap enabled, which keeps it solid.

### 4.4 False-colour layers — what is actually computed

**One shared error scale.** All Inspect error maps read on a single signed
range derived from the pins: the minimum and maximum of every ready pin's
region samples on the moving meshes, always spanning zero, hard-capped at
±0.5 m. With no pins, the full ±0.5 m is used. Values beyond the range clamp to
the end colours. There is deliberately no per-mesh or per-tile normalization
and no user range slider: two views showing the same colour mean the same
number of metres. The bottom-centre legend displays the active map's gradient
and exact range with rounded ticks.

**The diverging difference map** (used by the 3D difference layer, the focus
difference tiles, the matrix cells, the legend, and the brushed sample dots) is
*asymmetric piecewise*: a light **yellow** is welded to exactly 0 (distinct from
the white page — a grey/white centre vanished there); positive values ramp
yellow→orange→dark red normalized by the positive end of the range, negatives
yellow→steel blue→dark blue by the negative end — each sign by its own end, so
an asymmetric range does not shift the zero point. The ramp applies a 0.6-power
boost near zero (small differences become visible earlier than a linear ramp
would show them). Grey now has exactly one meaning on this map: *no signal*
(within the level of detection, or no data) — never "zero".

**Difference isolines.** Wherever the difference map paints, dark contour
lines mark constant difference values at a rounded step (a 1/2/5-rounded step
of roughly an eighth of the shared range, so zero is always a contour). The
lines are drawn in the shader from the interpolated per-vertex field with
screen-space-derivative antialiasing (roughly constant on-screen width), and
are suppressed where the value runs past the range and the colour clamps —
lines there would alias into noise while the colour can no longer distinguish
values anyway.

The layers:

- **Difference (per moving mesh)** — a signed per-vertex distance of the moving
  mesh to the reference, computed on the server. Two sub-modes: an M3C2-style
  signed distance, or a plain vertical Δz. Both share one support rule: a vertex
  gets a value only where the vertical world line through it actually pierces
  the reference surface (Z-overlap); elsewhere a sentinel is returned and the
  vertex keeps its base colour — the map never fabricates error in fringe areas
  the reference does not cover. Painted on the isolated moving mesh in Inspect,
  and on the focus tiles/single.
- **Disagreement / variance (ensemble)** — per *reference* vertex, the standard
  deviation of the visible moving meshes' signed distances at that vertex
  (population σ; requires at least two valid values, sentinels skipped — so
  only meshes overlapping there aggregate). Requires ≥2 visible moving meshes.
  Painted on the reference in Inspect whenever no mesh is isolated, on a
  sequential light-grey→red ramp saturating at the shared range's larger end.
- **Displacement (per solved mesh)** — the per-vertex magnitude of the solved
  pose change (|load→solved|), computed client-side. Its saturation end is *not*
  the shared error range: it is the exact global maximum displacement over every
  solved mesh (evaluated at bounding-box corners, which is exact for rigid
  motion), uncapped — a legitimate solve may move a mesh metres. Sequential
  light→dark blue.
- **Intrinsic per-mesh heatmaps** (Overview mesh list, honoured in 3D and the
  focus views; suppressed while that mesh paints a difference/displacement
  field): **Incidence** — |cos| of the angle between the surface normal and the
  direction to the mesh's scan sensor, red (grazing) → yellow → green (head-on).
  **Range** — distance to the sensor over the mesh's maximum extent from it,
  blue (near) → red (far). **Shape** — per-vertex mean triangle quality
  (4√3·area/Σedge², 1 = equilateral), red (degenerate) → green, with the ramp
  deliberately reaching full green at 0.75 so reasonable meshes read healthy.
  The "sensor" is the mesh's calibrated panorama centre when provided in the
  dataset, else the mesh origin.

All false-colour painting applies only to solid (above-ghost) fragments; ghosts
stay uniform.

### 4.5 Outlines and elevation isolines

An always-on image-space pass draws each mesh's silhouette in its palette
colour plus faint grey elevation contours, over the normal rendering:

- The scene is first rendered off-screen to a small geometry buffer (per-pixel
  depth, mesh colour, and an elevation band flag).
- **Silhouettes** are detected as *second differences* (a Laplacian) of the
  stored depth: depth varies linearly across a planar surface in screen space,
  so a smooth slope produces ~0 response at any viewing angle, and only true
  depth breaks (silhouettes, cliffs, occlusions) fire. A first-difference
  detector would light up every grazing surface. The stored depth has 8-bit
  precision, so the edge threshold (gear menu) must stay above that
  quantization floor or staircase artefacts on smooth slopes appear as false
  banding.
- **Isolines** are the boundaries of alternating world-elevation bands: the
  band index is a pure function of world Z (the band count over the scene's
  height range is a gear-menu setting, default 700), so contours stay welded to
  fixed elevations and do not crawl when the camera orbits. Band flips are
  detected as first differences (a step function needs no Laplacian).

Honest caveat: because these are per-pixel image-space detections, outline
width is constant in screen space and sub-pixel geometry can flicker under
motion; isolines inherit the mesh's rasterized coverage and stop at ghosted
regions' silhouettes.

### 4.6 Pins in 3D

A **pin** is a sphere-shaped region of interest placed on the terrain: centre +
radius (radius default from the gear menu's "quick-pin radius", editable later
per pin). Each pin has an immutable identity: a glyph + colour pair from a
10-slot palette (least-used slot on creation) and a random two-character name
(collision-checked against other pins and the mesh numbering).

3D representation:

- A small neutral **wire-jack marker** at the centre (fixed screen-independent
  render size; darkens on selection/hover). An invisible slightly larger sphere
  around it carries the click/double-click; it deliberately sits on top so the
  pin is clickable even against a busy surface. Click = select/deselect toggle,
  double-click = select + tight 3D zoom.
- The **equator ring** at the pin radius, perpendicular to the pin's axis, in
  the pin colour; plus a thin 1 m axis whisker. The axis is the probe's
  estimated surface normal once the probe lands (world-up until then).
- **Contact rings**: the exact intersection curves of the pin sphere with every
  mesh's surface, drawn in the pin colour on all *shown* meshes (computed on
  the server per mesh, cached until pose/radius changes). Hovering the pin's
  legend chip or matrix row makes them thick and bright.
- A **flag pole** rising along the pin axis with a small ring at the tip. The
  pole height is radius×1.5 plus *three times* the pin's error magnitude (the
  largest |median distance| across moving meshes) — an arbitrary but fixed gain
  so bad pins physically stand taller. Neutral grey normally; in the Overlays
  hold it takes the pin colour and triples in width.
- A **name label** (glyph-less, the two-character name in the pin colour)
  floating above the centre, always-on-top. Honest: it has a fixed world size
  and a fixed orientation (upright in a fixed vertical plane), so it is legible
  from typical viewing directions but foreshortens edge-on; it is hidden while
  the Overlays hold shows the 2D tags instead.
- In Correspondence only: the **constellation** — at every mesh's
  correspondence marker a small wire sphere + cross in that mesh's colour, with
  thin lines from each moving-mesh marker to the reference marker. Fixed render
  size, always-on-top; selection/hover brighten a pin's constellation;
  out-of-ROI meshes are omitted.
- While a chart brush is active: **sample dots** — small solid spheres at the
  brushed samples' true surface positions, coloured by their signed value on
  the diverging map normalized to *that pin's own* min/max envelope (not the
  shared range — deliberate, so a single pin's internal structure is visible).

While the **Overlays hold** is down, additional 2D name tags (glyph + name,
pin-coloured, on white pills) float at each flag tip, projected to screen every
frame. Overlap is accepted; tags behind the camera or far off-screen drop out.

### 4.7 Overlays and readouts

- **Cursor coordinate** (top bar): the metric-world position under the cursor,
  live on every mouse move. Off-mesh it falls back to the horizontal plane at
  the dataset's mean elevation (so it keeps reading over open ground). With a
  focused mesh it also shows the cursor position relative to that mesh's own
  origin — exact only while the mesh sits at its load pose (stated in its
  tooltip).
- **Scale bar** (bottom left): shows a rounded distance and its screen length,
  computed from the orbit radius and the field of view at the orbit-centre
  depth. Honest defect: the math assumes the 90° field of view is *vertical*,
  but the renderer applies it *horizontally* — on a wide viewport the bar
  overstates metres-per-pixel by roughly the aspect ratio. Lengths it displays
  should not be trusted for measurement (and being depth-dependent, they are
  only valid at the orbit centre's distance anyway).
- **Orientation gizmo** (bottom right): the world X/Y/Z axes projected into
  view space, depth-sorted, labels hidden when pointing away.
- **Coordinate cross**: a world-axis cross with tick marks and metre labels,
  anchored at the first mesh's panorama centre, always-on-top. Hidden while
  Space is held.
- **Colour legend** (bottom centre, Inspect only): gradient + exact ends +
  rounded ticks for whichever map currently paints (variance in the ensemble,
  difference or displacement when a mesh is isolated).
- **Toasts**: transient bottom messages for blocked/failed actions
  (out-of-ROI pick, solve failures, missing reference…); auto-clear.

---

## 5. Workflow steps and their policies

The rail's three steps are modes, not a wizard — switching is free at any time.
Each step header shows a readiness pill (e.g. "pick a reference ★", "place ≥3
correspondences, then solve", "aligned").

### Overview
For familiarization and per-mesh assessment. Rail = the mesh roster: one row
per mesh with colour swatch, number, name, and the intrinsic heatmap switch
(Textured / Distance / Shape / Incidence). Row hover = peek-isolate in 3D;
click = focus; double-click = zoom (3D + focus panel reset). Dock = a one-line
summary of the focused mesh (colour · name · reference/moving role). The focus
panel's large single is *hidden* in Overview — the panel is the tile browser +
controls only.

### Correspondence
For placing pins, editing correspondence markers, and solving. Pin isolation
defaults ON (terrain ghosts except pin regions). Rail = Solve button +
readiness pills + "Place pin" + the **pin×mesh matrix**. Dock = the selected
pin's manager (identity chip, radius slider, marker-coverage badge "k/n").
The constellation renders in 3D. The focus single gains the correspondence
overlay and the ✎ edit-point mode (see §7).

### Inspect
For reading the result. Pin isolation defaults OFF. Rail = the same pin×mesh
matrix. Dock = the distribution chart + channel toggles + (displacement only)
the shift readout. The 3D view shows the **ensemble**: moving meshes ghost, the
reference paints the variance aggregate. Selection policies:

- **Focusing a moving mesh** (click in 3D, on a tile, or on a matrix column)
  isolates it together with nothing else — it paints its own difference or
  displacement field against the ghosted rest; pin isolation switches off.
- **Focusing the reference** returns to the ensemble.
- **Selecting a pin** (matrix row, 3D dot) drops mesh isolation and switches
  pin isolation on — the pin's region lights up on otherwise-ghosted terrain.
- Deselecting the pin returns pin isolation to the Inspect default (off).

These policies live in the state reducer, so every entry path (3D click, tile,
matrix, rail) behaves identically.

---

## 6. The left rail in detail

### The pin×mesh matrix (Correspondence + Inspect)

Rows = pins (in creation order), columns = meshes (in load order, colour +
number only). The row head shows the pin's glyph/colour/name and a delete
button (with a native confirm dialog — deletion is irreversible).

**Cells** are the per-(pin, mesh) summary: the cell colour is the pin's median
signed distance on that mesh painted on the diverging map. Honest: cell colours
are *not* on the shared Inspect range — each row normalizes by its own largest
|median| across the moving meshes, and medians within the pin's 95 % level of
detection (1.96·√(σ_ref² + σ_mesh²), from the probe's spreads) render neutral.
So cells compare *within a row*, not across rows, and deliberately don't alarm
below the detectability floor. Out-of-ROI meshes show an emptiness glyph;
pending probes a placeholder.

Cell interactions:
- **Hover** = highlight the (pin, mesh) pair everywhere (constellation glyph,
  chart layer).
- **Click** = *locate*: if a marker exists, an atomic "frame correspondence" —
  the mesh is isolated, focused, the pin selected, the focus panel forced to
  Top; the first locate snapshots the camera + isolation + visibility so a
  second click on the same located cell *backs out* — restoring all of it
  exactly. No camera motion on click. If no marker exists, plain select+focus.
- **Double-click** = zoom both viewports tightly onto the correspondence point
  (3D fly + focus pan/zoom), ending in the located state regardless of what the
  leading clicks toggled.

Column heads mirror mesh focusing (click = focus, double-click = zoom, hover =
peek); the reference column is marked.

### Solve

The Solve button is enabled once any mesh has ≥3 in-ROI markers with a
reference set; readiness pills beside it name what is missing. Solving fires
one weighted rigid least-squares fit per solvable visible mesh in parallel
(a landmark/Procrustes solve — rotation + translation, no scale — computed on
the server with an SVD, all pairs currently weighted 1). Unsolvable meshes stay
at their load pose and are reported in a toast. Results arrive asynchronously;
each writes that mesh's solved pose wholesale (a re-solve replaces, never
accumulates) and flips the global view to **After**.

---

## 7. The focus panel

### The large single

Shows the **focused mesh** (or the first visible mesh if none is focused),
full-resolution and textured, in the same world frame and displayed pose as the
main view. Two projections, toggled in the panel head:

- **Top** — a strict orthographic vertical drop. Drag pans, wheel zooms
  anchored at the cursor (the point under the cursor stays put).
- **360°** — a standard perspective camera *fixed at the mesh's panorama
  centre* (the calibrated scan-camera position when the dataset provides one,
  else the mesh origin). Drag looks around grab-the-world style (the surface
  point under the cursor tracks the cursor); wheel zooms by narrowing the field
  of view (4°–120°), anchored at the cursor. The eye never translates.

Camera state (pan/zoom or azimuth/elevation/zoom) is remembered **per mesh and
per projection**, so switching tiles or projections restores each view; a
double-click zoom elsewhere resets or retargets it deliberately. Honest
limitations of the 360° view: the correspondence overlay, the gold reference
silhouette, and the displacement arrows are all laid out in Top-view
mathematics and simply do not render there — and the Displacement channel
forces the single back to Top.

Overlays on the single:

- **Inspect recolouring** — the same difference/displacement/intrinsic layers
  as 3D, same shared scale, painted per-vertex over the texture. Honest: the
  focus difference layer has no level-of-detection gate (unlike matrix cells) —
  it shows raw signed distance colour everywhere it has support.
- **Displacement arrows** — subsampled per-vertex arrows from load to solved
  position. Honest: arrow *length* is exaggerated to a fixed fraction of the
  view for visibility (scaled by the maximum displacement in view), only the
  arrow *colour* encodes the true magnitude on the shared displacement ramp.
  The surface is forced white behind them.
- **Reference silhouette** (Top, non-reference mesh shown, gold) — the
  image-space outline of the reference at its (never-moving) pose, so the
  moving surface visibly shifts under a pose-stable outline when toggling
  Before/After.
- **Correspondence overlay** (Top + Correspondence step): each pin's ROI circle
  at true radius in the pin colour; this mesh's marker as a screen-fixed cross +
  ring glyph in the mesh colour; a live cyan aim ghost while editing.

**Correspondence editing** ("✎ edit point", panel head; offered when a pin is
selected and a mesh focused in Correspondence): arms an edit mode for that
(pin, mesh) pair. While armed: the mesh is isolated solid in the main 3D view;
moving the cursor over *either* the focus single or the 3D surface shows a live
aim ghost (cross + ring in 2D, wire sphere + cross in 3D — server ray cast,
throttled); a plain left click on either surface sets the marker at the hit
point. Picks outside the pin's radius are rejected with a toast. The mode
*stays armed* after a pick so the point can be refined; it disarms by clicking
✎ again, or automatically on selection changes, placement, delete, or step
switch. Middle-drag / Shift+left-drag still navigate while armed; plain left
drag is reserved for placing.

Focus picking honesty: this panel has no GPU picking at all — every hover ghost
and click is a server round-trip, so the aim ghost lags by network latency and
is throttled to ~16 requests/s.

### The tile strip

One orthographic thumbnail per mesh (every mesh, hidden ones dimmed), each
framed on its panorama centre and carrying the same Inspect/intrinsic
recolouring as the single. The tile *view* is the mesh selector: click =
focus, double-click = focus + 3D zoom + reset that mesh's focus cameras,
hover = peek-isolate in 3D. A control strip under each tile holds the per-mesh
controls that exist only here: **★ reference toggle**, **visibility toggle**
(locked during isolation), **◐ isolate**. Honest: thumbnails are live renders,
not cached images — many large meshes cost GPU time; they are framed top-down
regardless of the mesh's natural orientation.

---

## 8. The bottom dock

### Overview: focused-mesh card
Colour swatch, numbered name, role ("★ reference" / "moving"). Nothing else.

### Correspondence: pin manager
Identity chip (glyph · colour · name), the pin **radius** slider (logarithmic,
0.01–10 000 m; changing it invalidates and re-fires the pin's probe and rings),
and the **k/n** badge (markers placed / in-ROI moving meshes; out-of-ROI meshes
are excluded from n). Empty state prompts "select a pin".

### Inspect: distributions + channels

A head row holds: the **Difference | Displacement** channel toggle (drives the
3D isolated layer and the focus recolouring), the **M3C2 | Δz** sub-mode
(Difference only; switching refetches the per-vertex fields), the **pin
legend** chips (hover = highlight that pin's chart layer and pulse its rings in
3D — the chips are the chart's only hover handle, the canvas itself has no
hover), and the axis note.

**The distribution chart** — one lane per visible moving mesh, drawn on a
canvas:

- Each lane is a **one-sided stacked histogram** of every ready pin's region
  samples on that mesh: 48 bins over a shared signed-distance axis (mm), bars
  growing up from the lane baseline, pin segments stacked in creation order
  with the pin colours. The x-range is the 1–99 % quantile span of *all*
  samples across both poses, padded, always spanning zero.
- Histogram counts come from the **full** probe sample sets. Once a solve
  exists the same lane carries both poses: the emphasized pose (the committed
  Before/After view; the Peek hold flips the emphasis) is the filled stack, and
  the *other* pose is over-drawn as a near-black step outline of its lane
  total — shape only, no per-pin subdivision. One count→height scale is shared
  across all lanes and both poses, so areas compare.
- Per lane: a light band at ±LoD₉₅ (averaged over the pins), a per-pin median
  tick on the baseline, a zero line, rounded ticks.
- **Brushing** — dragging an x-range on the chart is the *only* way to brush
  samples. The brush selects, by value range, from a canonical sample list (see
  §9) and lights those samples up as the 3D dots; the range band with exact
  mm-labels and count is drawn on the chart; a plain click clears. Honest: the
  brush is by value, not by lane or pin — one drag selects matching samples in
  *all* lanes at once; and the brushable set is the capped display subset, not
  the full statistics set, capped again at 200 brushed samples for the 3D dots.

**Shift readout** (Displacement channel only, needs a focused solved mesh):
the mesh centroid's load→solved displacement split into total (with an 8-way
direction arrow), vertical datum shift, horizontal shift, and the rotation
angle extracted from the solved pose.

---

## 9. Pins, probes, and the data behind every number

Every pin fires one **probe** query against *all* meshes (visibility never
gates data collection — only display — so matrix cells and lanes are stable
under visibility toggling):

1. The surface **normal** is estimated at the pin: a principal-component fit of
   the reference-mesh vertices inside the pin sphere (needs ≥6; flipped to point
   upward). This normal is also the pin's displayed axis.
2. A **cylinder** along that normal (radius = pin radius, length fixed at 20 m)
   is intersected with every mesh. Each mesh surface inside the cylinder is
   sampled with a deterministic lattice over triangle interiors targeting a
   density cap (≤8192 points per mesh), so coarse and fine meshes both yield
   distributions. The signed coordinate of each sample along the axis is the
   sample value — an M3C2-style signed distance.
3. All values are **re-centred so 0 = the reference mesh's median**; per mesh
   the count, median, spread, and quantiles are computed from the full sample
   set. A subsample of ≤300 values per mesh (with their 3D surface positions)
   is returned for display and brushing.

The **level of detection** used by cells and lanes is 1.96·√(σ_ref² + σ_mesh²)
— the 95 % threshold under a Gaussian assumption on the two spreads; medians
below it are shown as "not detectable" (neutral cells, faint band).

Probes are **per displayed pose**: the committed pose's probe backs every
consumer (cells, lanes, dots, the shared error range); after a solve the
opposite pose is probed once more, and that second result feeds *only* the
histogram's outline pose. Toggling Before/After swaps a ready pair in place
(no refetch) and clears the brush (the sample identities are pose-specific);
changing radius, reference, or solving invalidates and refetches everything.
Probe/ring/field fetches are debounced (a fast slider drag coalesces to one
query) and stale responses are dropped.

The **canonical sample list** ties the chart to 3D: moving meshes in load
order × ready pins in creation order × their ≤100-per-cell subsampled values,
in a fixed order, so "sample #k" means the same surface point to the chart, the
brush, and the 3D dot layer.

**Correspondence markers** are per (pin, mesh) surface points used by the
solve. When a reference is set (or a pin placed), markers are **auto-seeded**:
the reference marker is the pin centre's closest point on the reference; each
moving mesh's marker is the closest point on that mesh to the reference marker;
candidates farther from the pin centre than the probe cylinder's reach
(√(radius² + 10²) m) are classified out-of-ROI and get no marker. Manual edits
(✎, or the 3D pick while armed) always win: re-seeding never overwrites a
manually placed marker. Markers are stored in the mesh's *own* frame, so the
Before/After toggle carries them with the mesh — no re-derivation.

---

## 10. How the interactions chain — typical flows

**Load & orient.** Dataset picked in the gear menu → meshes stream in, camera
flies to frame the scene → Overview: hover rows to flash meshes in 3D, switch a
mesh to an intrinsic heatmap to judge its quality, ★ a tile to set the
reference (first mesh is pre-set by default).

**Place a pin.** Correspondence → "Place pin" → the rail stays open, the cursor
becomes a crosshair, terrain drops to ghost and a flashlight preview (sphere +
outline at the future radius) follows the surface under the cursor via server
ray casts → click commits the pin (identity assigned, camera re-centres on it,
pin selected) → the probe and contact rings fire automatically → within a
moment the matrix gains a coloured row, the constellation gains markers
(auto-seeded), and the Inspect chart gains a layer. Esc or the button cancels
placement.

**Refine a correspondence.** Matrix cell click → locate: mesh isolated, pin
selected, focus panel on Top framing the mesh → cell double-click → both
cameras zoom to the marker → ✎ edit point → aim on the focus single or the 3D
surface (live ghost on both) → click to set (rejected with a toast if outside
the pin) → readjust radius in the dock if the region is wrong (probe refires)
→ click the located cell again to back out to the pre-locate camera and
visibility.

**Solve & judge.** Solve enabled once a mesh reaches 3 in-ROI markers → Solve →
toast reports how many meshes solve; poses arrive async; view flips to After →
Before/After becomes active; Peek flashes the other pose; the histogram
overlays the other pose's outline; the reference silhouette in the focus single
shows the shift → Inspect: ensemble variance on the reference; focus a mesh →
difference field; Displacement channel → displacement field + arrows + shift
readout; pin lanes/cells say whether the residual is above detectability.

**Chase an outlier.** Inspect chart: drag over the suspicious value range →
3D dots appear on the terrain at exactly those samples → hover pin chips to
see which pin contributes → matrix row double-click zooms to that pin →
`I`-hold to flash the pin regions, `O`-hold to identify pins by name tags,
Space-hold for a clean look at the bare terrain.

---

## 11. Input reference

| Input | Context | Effect |
|---|---|---|
| Left-drag | 3D | Orbit |
| Middle-drag | 3D | Pan (cursor-anchored) |
| Wheel | 3D | Dolly zoom |
| Double-click | 3D | Re-centre orbit on surface point |
| Click | 3D surface | Focus the mesh under the cursor |
| Click | 3D, placing | Commit the pin |
| Click | 3D/focus, edit armed | Set the correspondence marker |
| Alt (hold) | 3D | Picking-layer isolation; Alt+wheel cycles the layer |
| Space (hold) | 3D | Hide all annotation |
| `I` / ◎ (hold) | global | Force pin isolation |
| `O` / 🎨 (hold) | global | White-out + pin spotlight |
| Peek (hold) | top bar | Show the other registration pose |
| Esc | placing | Cancel placement |
| Left-drag | focus Top | Pan |
| Left-drag | focus 360° | Look around |
| Middle/Shift+left-drag | focus, armed | Navigate while the edit mode holds left-click |
| Wheel | focus | Zoom (Top: ortho, cursor-anchored · 360°: fov, cursor-anchored) |
| Drag on chart | Inspect dock | Brush a sample value range; click clears |
| Drag panel edges | dock top / focus left | Resize |

---

## 12. Honest limitations and rough edges (summary)

- The scale bar's metres-per-pixel math uses the field of view on the wrong
  axis; it overstates lengths by ~the viewport aspect ratio (see §4.7).
- Matrix cell colours normalize per pin row and gate at the detection level;
  the 3D/focus difference layers use the shared global range with no gate. The
  same data can therefore look calmer in the matrix than on the surface.
- Displacement arrows exaggerate length (colour is truthful).
- The shared error range hard-caps at ±0.5 m; larger residuals saturate
  indistinguishably (displacement, by contrast, is uncapped).
- Exiting mesh isolation resets every visibility toggle to ON, discarding any
  hidden-mesh arrangement.
- 360° focus view: no correspondence overlay, no reference silhouette, no
  displacement (falls back to Top).
- All focus-panel picking and every hover ghost is a throttled server round
  trip (~60 ms cadence + latency); the aim ghost visibly trails fast motion.
- Chart brushing selects by value across *all* lanes at once, from the capped
  display subset (≤100 samples per pin×mesh, ≤200 dots in 3D), not the full
  statistics set. There is deliberately no 3D hover-reveal of samples.
- The pin-isolation shader honours at most 32 pins.
- A slow double-click can momentarily fire the deferred single-click action on
  toggle-controls (matrix cells, 3D pin dots) before the zoom corrects it.
- 3D pin name labels are fixed-size, fixed-orientation text (not billboards);
  the 2D overlay tags exist only during the Overlays hold and may overlap.
- Probe statistics assume the pin's single PCA normal is valid across the whole
  cylinder; the 20 m cylinder length is fixed, not adaptive.
- The probe re-centres all distances to the reference *median* inside the pin,
  so a genuine uniform offset of the reference itself inside the pin cannot be
  seen — by construction 0 means "agrees with the reference's central surface".
- Solve weights are uniform (weight 1 per pair); the UI surfaces no residuals
  or conditioning diagnostics per pair, only success/failure and the shift
  readout.
