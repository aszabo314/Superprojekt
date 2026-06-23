# Agent Spec — Correspondence-Point Detail View

Build a second, isolated, orthographic viewport in the pin card showing ONE registration pin's correspondence markers to-scale, with a 2D SVG overlay for precise markings. The mesh surface is shown **symbolically** (contour + ridge/valley lines) — no mesh copy, no ghost.

- **3D** = parallel Aardvark RenderControl.
- **2D** = SVG layered over it.
- **REUSE** = find in code (WP0).

**Adaptation rule:** every name/path/type/message/signature below is a *hypothesis*. Verify against the actual code; where the real interface differs, adapt and record the deviation in `IMPLEMENTATION_NOTES.md`. Prefer observed conventions over literal text here. Do not invent data obtainable from code. Build order WP0..WP10. After each WP: build clean, tests pass, commit `DVW<n>: <msg>`.

**Terrain assumption (fixed):** meshes are height fields (z = up = elevation, no overhangs), in metres, relatively flat. Contour extraction = marching-squares on sampled elevation.

---

## REVIEW NOTES — 2026-06-23 (pre-implementation, Claude)

> Verdict: the *content* design (orthographic to-scale view, symbolic contour/ridge/valley lines, height-modulated colour, measurement overlay, strike/dip) is sound and almost fully specified. One central architectural choice is wrong for this stack, and ~5 "REUSE" items are actually "build-new" or need a known pattern. Resolve A1 before any code; B/C below are concrete spec edits.

### A. Architecture — resolve first

**A1 — Drop the second 3D RenderControl (WP2). Render the whole detail view in the SVG layer.**
WP2 mandates a *second parallel Aardvark RenderControl* and treats "concurrent unsupported" as a fallback. On this Aardworx WebGL backend that is the **primary** case, not the fallback: a documented finding (memory `patch-picker-html-canvas.md`, June 2026) is that a single extra live render control holding ~40k verts dropped the main view from ~50→7 fps **while idle**, persisting until page reload — and the user then deliberately reverted the patch picker *back* to HTML/SVG "due to the styling options". This spec's content is **100% 2D-projectable**: symbolic lines are world-space polylines, glyphs are screen-constant marks, and WP5.5 `project(worldPt)` is derived from *our own* ortho camera params (it does **not** need a RenderControl — the spec already says so). So the "3D canvas" adds nothing the SVG can't do, while reintroducing the exact perf risk we just removed.
  - **Recommendation:** delete WP2's RenderControl; render symbolic lines + glyphs + strike/dip + rulers/callouts all as SVG, painter-ordered, projected via WP5.5. This also deletes the R2/R11 risk, the FShade-float32 / `Sg.DepthMask` / on-demand-render concerns, and the GL-lifecycle leak surface (WP1/WP10.8). Keep WP4.2/4.3/4.4 *geometry math* verbatim — only the rasterisation target changes (Aardvark Sg → SVG polylines/markers).
  - Symbolic line geometry is light (low thousands of segments). *If* a literal 3D look is later wanted, spike one real ortho control with representative geometry and measure main-view fps **before** building WP3–WP10 on it. Don't assume.

### B. "REUSE" items that are actually build-new / partial (corrects WP0)

Pre-filled discovery (verified against code — use instead of re-deriving):

| ID | Reality | Handle |
|----|---------|--------|
| R1 | **Partial.** No reusable *per-instance ortho 3D camera controller*. `OrbitController`/`OrbitState` is bound to the single global `Model.Camera` (state in the model, `isOrtho` flag exists but is non-adaptive/unused) — two instances can't share it. The pan+zoom that *is* reusable is the **patch picker's view-local JS/cval 2D pan+zoom** (`CardsPin.fs`, `__ppv` state: `st.cx/st.cy/st.z`). With A1, reuse *that* pattern. | `OrbitController.fs:OrbitMessage`, `CameraModel.fs:OrbitState`; 2D: `CardsPin.fs` patch JS |
| R6 | **Present.** Marker glyphs = wireframe tetra + line to ref, colour from `pin.DatasetColors : Map<string,C4b>`, hover-brighten `c*0.45 + white*0.55`. Reference is currently just the line endpoint — the "ring+cross" ref glyph is **new geometry** (small). | `ScanPinScene.fs:anchorGlyphs` (~289) |
| R7 | **Present, render-space.** `Model.MeshTransforms : Map<string,Trafo3d>`; `ModelTransforms.effectiveRender = committed * delta` (postfix). **Caveat:** markers (`MeshAnchor.Point`) are stored **world-space at committed pose**; previewing pending must apply the **world delta** (see `bakeAnchors`), NOT multiply render trafos. See C2. | `Model.fs:MeshTransforms`, `RegistrationModel.fs:RegLog.effective` |
| R8/R14 | **Absent → build-new.** No endpoint returns a grid `z(x,y)`. `/query/patch` returns *triangles-in-sphere* (world pos + UV); `/region-distance` returns per-vertex signed scalars; `/probe` returns stats. See C1 for the recommended new server endpoint. | `MeshAnalysis.fs:patch`, `QueryHandlers.fs` |
| R9 | **Present.** `SetCorrMarkerHover of (ScanPinId*string) option`, `SetChartHoverMesh of string option`, `SetWorkflowPinHover`. Store-only reducers; 3D highlight reads model in `ScanPinScene.fs`. Reuse `SetCorrMarkerHover` for the table-row→3D/violin link. | `Messages.fs` ~102–104 |
| R10 | **Absent.** No stored North/bearing. Datasets are UTM, so **+Y = North** is a safe optional assumption if a compass is wanted; spec's "omit if absent" is acceptable. | — |
| R11 | **Absent as an accessor** (moot under A1). View/Proj computed locally (`Cards.fs:projectToScreen`, `View.fs:pickRay`); derive ortho world→clip from our params. | `Cards.fs:projectToScreen` |
| R12 | **Present.** `CardsPin.fs:pinCardBody` builds `div`/section subsections (`pc-readout`/`pc-probe`/`pc-corr`/`pc-patchpicker`). Collapse state = view-local `collapsedSet : cval<HashSet<CardId>>` in `Cards.fs`. | `CardsPin.fs:pinCardBody`, `Cards.fs:renderCards` |
| R13 | **Present.** `Correspondence = { Enabled; RefAnchor : V3d option; RefDistance; Anchors : Map<string,MeshAnchor>; Residuals : Map<string,float> }`; `MeshAnchor = { Point : V3d; Source }`. Effective pin = `ScanPinModel.effectivePinId` (adjusting-or-selected). | `RegistrationModel.fs:Correspondence/MeshAnchor`, `ScanPinModel.fs:effectivePinId` |
| R15 | **Present.** `Animation` easings (Tanh default) + `FlyToMath`; per-frame interp in `OrbitController` Rendered branch. For a 2D-SVG view, a frame-loop lerp on the local cvals is enough. | `CameraModel.fs:Animation`, `RegistrationModel.fs:FlyToMath` |

### C. Integration-pattern gaps (add to the relevant WPs)

- **C1 — Elevation sampling (WP4.2):** add a server endpoint returning a regular `z(x,y)` grid for a mesh in a footprint (Embree ray-down server-side — fast, keeps the client thin per the architecture rule). Marching-squares + curvature run client-side on the returned grid. The spec's "client ray-cast against triangles" fallback violates "push heavy compute to the server" and the client has no convenient triangle store. One grid request per marker.
- **C2 — Lazy/debounced/postlude (WP1/WP4.2):** the per-marker grid fetch MUST follow the established `ensureProbe`/`ensureRings` pattern — a debounced (250 ms) postlude after the reducer, per-generation `CancellationTokenSource`, running-guard to drop stale responses, invalidate→refetch on pin/marker/reference/transform/preview change. The spec's bare "recompute on change" will churn without this.
- **C3 — State placement (Data model §):** keep DetailView camera state (`panOffset/zoomExtent/camAzimuthRad/userAdjusted/hoverMarker`) as **view-local cvals** (like patch picker `__ppv` + `patchHover`), NOT in the Elm `Model` — otherwise every pan/zoom is a full model replace (violates the adaptive-perf rule). Only `hoverMarker` crosses into the reducer, and that goes through the existing `SetCorrMarkerHover`. `collapsed` → reuse `collapsedSet` (clarify "per session" = in-memory across open/close, not workspace JSON).
- **C4 — Pure math is testable:** put marching-squares, `niceStep`, dip/strike, PCA-azimuth in a **WASM-free** module (compiled into `Supertests`, like `RegMath`/`RegistrationModel`) so WP10 #1/#3/#5 become unit tests, not just manual checks. New `.fs` files slot into `Superprojekt.fsproj` order: pure-math near `RegistrationModel.fs`; the SVG/section view after `CardsPin.fs`, before `View.fs`.

### D. Minor
- rvThresh=0.02/m and the WP9 defaults are "flat-terrain" guesses — expect a tuning pass on real `JOB_lowpoly2` + `glacier` data; not a blocker.
- Reference "ring+cross" glyph is new (small) geometry (R6 note above).

> Net: green-light the design, but the agent should execute with **A1 applied** (SVG-only, no second RenderControl), the WP0 table above pre-filled, and C1–C4 folded in. With those, integration friction is low and contained to: one new server endpoint, one new view section + pure-math module, and reuse of existing hover/transform/marker/colour plumbing.

---

## WP0 — Discover (fill into `IMPLEMENTATION_NOTES.md` before coding)

Per item record: real handle (`file:type/member`) **or** "absent → build new", plus signature.

| ID | What to find |
|----|--------------|
| R1 | pan+zoom camera controller (recently added). Standalone or bound to main control? Drives an orthographic camera? What events/messages? Can two instances coexist (per-control state)? |
| R2 | RenderControl create/attach pattern (Blazor). Multiple concurrent controls supported? If not → render-to-texture / second-canvas fallback. |
| R6 | correspondence marker-glyph rendering (moving markers + reference anchor): geometry + color source. |
| R7 | per-mesh committed transform store + pending-preview composition ("committed ∘ pending"). |
| R8 | per-vertex elevation/normals for a mesh region (to sample for contours + ridge/valley). |
| R9 | hover/brush link path: message highlighting a marker/mesh in main view + violin (e.g. `SetWorkflowPinHover`, `SetChartHoverMesh`). Cases + payloads. |
| R10 | world→geographic-North mapping (stored bearing, or "North = +Y/+X"). May be absent. |
| R11 | view-projection accessor on a RenderControl (world→clip). If absent → derive from the ortho camera params we control. |
| R12 | pin card host (`CardsPin.fs` or successor): how a card body renders sub-sections; how per-pin/per-session UI flags are stored. |
| R13 | selected-pin + its correspondence markers: marker type (mesh id, world point, residual?), and how to enumerate "this pin's markers". |
| R14 | mesh region sampler: given mesh + world-XY center + radius, get elevation z(x,y) on a grid (for marching-squares). If only raw triangles exist → sample by ray-down. |
| R15 | animation/easing util for camera transitions (else lerp on the frame loop). |

If an R-item is absent, the section that needs it states the build-new fallback.

---

## Data model (add to app model; names adapt to conventions)

DetailView state lives only while the panel is open; persist only `collapsed`.

```
DetailView:
  pinId         : registration pin id (None => panel hidden)
  view          : Side | Top | Free        (default Side)
  camAzimuthRad : float    (Side; auto-set, user-overridable in Free)
  panOffset     : V2d screen-plane offset
  zoomExtent    : float    (ortho half-height, metres)
  userAdjusted  : bool     (true after user pans/zooms; suppresses auto-fit until markers change)
  hoverMarker   : meshId option
  collapsed     : bool     (persisted per session)
```

No decoration toggles — symbolic lines, dip, rulers, compass are always on.

Derived per render (pure fn of pin markers + transforms; recompute on change):

```
refWorld : V3d
markers  : list of {
   meshId; color;
   world   : V3d            // marker after committed o pending transform
   isRef   : bool
   euclid  : float          // |world-refWorld|   (0 for ref)
   vert    : float          // signed (world-refWorld).z
   horiz   : float          // |horizontal part of (world-refWorld)|
   azimuthRad : float       // bearing of horizontal offset vs North (NaN if no R10)
   dipRad  : float          // angle(localSurfaceNormal, up)
   strikeDirWorld : V3d      // up x normal
   symbolic : SymbolicPatch  // WP4.2
}
centroid : V3d              // mean marker world (frame center)
pca      : axes+extents of marker worlds (Side azimuth)
```

`up` = +Z (R14). Recompute on: pin change, marker add/move, reference change, committed transform change, pending-preview toggle.

```
SymbolicPatch (per marker, WP4.2):
  contours : list of polyline (list of V3d, world)   // marching-squares isolines
  ridges   : list of polyline                          // crest lines
  valleys  : list of polyline                          // channel lines
  zMin,zMax: float                                     // patch elevation range (for shading)
```

---

## WP1 — Panel shell + lifecycle  `[REUSE R12,R13]`

- Add collapsible "Correspondence detail" section to the pin card body (R12).
- Visible iff selected pin is a registration pin with ≥1 correspondence marker (R13). With 0 markers: show "no correspondence points yet", render nothing else.
- Layout top→bottom: **toolbar** (WP7) → **viewport** (~360px tall, full width; 3D canvas + SVG overlay stacked) → **values table** (WP6).
- Expand: create RenderControl (WP2) + controller (WP3); compute markers; auto-fit (WP5.4).
- Collapse / card close: dispose RenderControl, free GL, drop state except `collapsed`.
- Open/collapse idempotent and leak-free across repeats (verify WP10.8).

## WP2 — Parallel ortho RenderControl  `[3D; REUSE R2,R7]`

> ⚠ REVIEW A1: do NOT build a second RenderControl on this backend — render everything in the SVG layer using the ortho projection of WP5.5. The bullets below describe the ortho camera *math* (eye/up/halfH/halfW, world→clip), which is still needed to drive `project()`; only the GL render target is dropped. Keep the camera math, skip the control.

- Second RenderControl on the panel canvas (R2); fallback if concurrent unsupported (note it).
- Ortho camera: `halfH = zoomExtent`; `halfW = zoomExtent*(panelW/panelH)`; near/far enclose markers+lines with margin. `view = lookAt(eye, centroid, screenUp)`; (eye dir, screenUp) per WP5.
- All geometry world space, mesh-transformed by committed ∘ pending (R7) → agrees with main view incl. preview.
- Scene layers: A symbolic lines (WP4.2) → B marker glyphs (WP4.3) → C strike/dip glyphs (WP4.4).
- Render on demand (state/markers/transforms changed); continuous loop only during cam anim (WP5.3).
- Expose world→clip matrix for overlay (R11 or derive here).

## WP3 — Controller  `[REUSE R1]`

- Attach R1 to this ortho camera: **pan** → shift view center in screen plane (update `panOffset`); **zoom** → scale `zoomExtent` (clamp `zoomMin/zoomMax`; zoom about cursor if R1 supports, else center). Any pan/zoom → `userAdjusted=true`.
- Disable orbit in Side/Top. Free: enable R1 rotation (or add minimal azimuth/elevation drag on *this* control only). Ortho cam math here is authoritative; adapt R1 to feed it.
- If R1 is bound to main control / not per-instance reusable: extract reusable core or wrap a second instance; record approach.

## WP4 — 3D scene contents  `[3D]`

**4.1** Removed — no context mesh, no ghost.

**4.2 Symbolic patch lines**  `[3D; REUSE R8,R14]` — default, always on, no toggle.

Per marker, build `SymbolicPatch` from a sampled square neighborhood of that marker's mesh, centered at marker world-XY:

- Sample grid via R14: side = `patchSize` (m, WP9); res = `patchGridN × patchGridN`. If R14 absent: ray-cast straight down per grid cell onto the mesh triangles.
- **Contours:** marching-squares on the elevation grid at levels `z = k*contourInterval`, where `contourInterval = niceStep((zMax-zMin)/contourTargetCount)`. Emit world polylines at true z.
- **Ridge/valley:** per grid cell compute discrete curvature along the two grid axes (second difference of z). Ridge cells = both-axis curvature `< -rvThresh` (convex up = crest); valley cells = `> +rvThresh` (concave = channel). Link adjacent marked cells into polylines; drop chains shorter than `rvMinCells`. Grid-based keeps it robust on flat terrain; no per-triangle normal-flip test.
- **Holes:** cells with no mesh sample are gaps — never bridge a contour or chain across them.

Render all polylines as world-space lines (constant screen-px width), colored per 4.2-color.

**4.2-color** Per-mesh color modulated by height:

```
base = marker.color
per polyline vertex at elevation z:
  t = clamp((z - zMin)/(zMax - zMin), 0, 1)
  shade = lerp(darkFactor, brightFactor, t)   // darker low, brighter high
  rgb = base * shade                          // clamp <= 1
```

Ridge/valley lines use the same scheme but `*ridgeWeight` (and valleys slightly desaturated) so they read as structure over contours. Contour width `contourPx`; ridge/valley width `rvPx` (thicker).

**4.3 Marker glyphs**  `[REUSE R6]` — one glyph per marker in `marker.color`; reference glyph distinct shape (ring+cross). Constant **screen** size (via WP5 projection, independent of zoom). Drawn above lines.

**4.4 Strike/dip glyph**  `[REUSE R8]` — `localSurfaceNormal` from the sampled patch (fit plane to grid, or central-difference gradient). `dipRad = angle(normal, up)`. `strikeDirWorld = normalize(up × normal)`. Draw short strike segment through marker along `strikeDirWorld` + dip tick perpendicular toward downslope (project `-normal` onto horizontal). Fixed screen length. Numeric dip in overlay (WP8.4).

## WP5 — Views, framing, projection  `[3D camera + math]`

- **5.1 Orientation** (screen-up = world up in Side & Top):
  - **Side** (default): horizontal eye dir; azimuth = 5.2. Vertical offsets read true vs the vertical ruler.
  - **Top:** eye dir = `-up` (straight down); screen plane = world horizontal; compass active.
  - **Free:** keep eye, enable rotation (WP3); rulers hidden (5.5).
- **5.2 Side azimuth (auto):** from `pca`, look along horizontal projection of the *smallest*-spread axis → largest spread across screen. Store `camAzimuthRad`; recompute on marker change unless `userAdjusted`.
- **5.3 View switch:** animate eye+up ~0.4s (R15 or frame-loop lerp), then stop loop.
- **5.4 Auto-fit:** set `zoomExtent` + center so all markers+patch bounds fill ~0.8 viewport. Run on open, marker change, view switch — *unless* `userAdjusted` (keep user pan/zoom; clear `userAdjusted` only when markers change). Center = `centroid`.
- **5.5 `project(worldPt) → (px,py,visible)`** from ortho world→clip (R11/WP2) + viewport size. Used by overlay + glyph sizing. In Free, mark rulers not-exact → hidden.

## WP6 — Values table  `[2D HTML]`

Below viewport. Row hover → set `hoverMarker` → bold its callout, emphasize its glyph, fire R9 (main view + violin). Leave → clear.

| Mesh | Euclid (m) | Z (m) | Horiz (m) | Az (°) | Dip (°) |
|------|-----------|-------|-----------|--------|---------|
| ref (pinned top) | 0 | 0 | 0 | — | shown |
| (per moving marker, color swatch in Mesh cell) | … | … | … | … | … |

Monospace numerics, mm precision. Az blank if `azimuthRad` NaN (no R10).

## WP7 — Toolbar  `[2D HTML]`

`[Side] [Top] [Free]` segmented control (active = `view`); click → set view → WP5.1 + animate + auto-fit (if `!userAdjusted`). `[Reset view]` → `userAdjusted=false` → auto-fit + re-auto-azimuth. No decoration toggles.

## WP8 — SVG overlay  `[2D]` (always-on decorations)

Transparent SVG, exact viewport pixel size, above canvas. `pointer-events:none` except table hovers (WP6). Redraw on: camera change (pan/zoom/view/anim frame), markers change, transform/preview change, resize. Throttle to animation frames. Each redraw projects marker worlds + `refWorld` via WP5.5, then draws:

| # | Element | Detail |
|---|---------|--------|
| 8.1 | Rulers + scale bar (hidden if `view==Free`) | `pxPerMetre = |project(refWorld+up)-project(refWorld)|` (Side vertical; horizontal analogue in Top). `tickStep = niceStep(pxPerMetre, targetTickPx)`. Axis line + ticks + metre labels on relevant edge(s) + corner scale bar. |
| 8.2 | Measurement lines | per moving marker: line `marker.screen→ref.screen`, label = euclid (m) at midpoint, text halo. |
| 8.3 | Per-point callouts | label box per moving marker: Euclid + Dip (default); hovered → bold + full set (Euclid,Z,Horiz,Az,Dip). Place boxes to avoid overlap (vertical fan Side / radial Top); leader line glyph→box. **Only** occlusion remedy (no geometry moves). |
| 8.4 | Dip text | dip (°) by each strike/dip glyph; in Top also strike azimuth vs North. |
| 8.5 | Compass (Top only, if R10) | North rose in a corner, rotated to `project(centroid+northDir)-project(centroid)`. Labels N/E/S/W. Omit if no R10. |
| 8.6 | Styling (always) | text halo = white stroke ~3px under fills; marker outline = stroke ring around each glyph. |

`niceStep(pxPerUnit, targetPx)`: `raw = targetPx/pxPerUnit`; pick `m ∈ {1,2,5}·10^floor(log10(raw))` nearest `≥ raw`; return `m`.

## WP9 — Constants (one module) — defaults tuned for flat terrain

| Constant | Value | Note |
|----------|-------|------|
| patchSize | 4.0 m | sampled neighborhood side per marker |
| patchGridN | 48 | 48×48 marching-squares grid |
| contourTargetCount | 8 | aim ~8 contours across patch range |
| contourInterval | auto | `niceStep((zMax-zMin)/contourTargetCount)`, floor 0.05 m |
| rvThresh | 0.02 /m | curvature mag for ridge/valley (flat → small) |
| rvMinCells | 4 | drop shorter ridge/valley chains |
| contourPx | 1.25 | contour line width |
| rvPx | 2.0 | ridge/valley line width |
| ridgeWeight | 1.15 | brightness boost on ridge lines |
| darkFactor | 0.55 | height shade at zMin |
| brightFactor | 1.25 | height shade at zMax (clamp rgb≤1) |
| glyphPx | 9 | marker glyph screen size |
| fitFill | 0.8 | auto-fit viewport fraction |
| viewAnimSec | 0.4 | view-switch animation |
| targetTickPx | 64 | ruler tick spacing target |
| haloPx | 3 | text halo width |
| zoomMin, zoomMax | 0.25, 50 m | ortho half-height bounds |

## WP10 — Verify (run yourself)

1. **Ortho/measure:** synth 2 markers 1.0 m apart → vertical ruler reads 1.0 m (Side) / horizontal (Top) at 3 zooms; pan/zoom never change table values.
2. **Frame:** screen-up == world up (Side & Top); rotating dataset changes Side azimuth only, not up; compass → R10 North in Top.
3. **Symbolic:** tilted plane → evenly spaced parallel contours; roof shape → one crest polyline + contours kinking at it; flat patch → ~`contourTargetCount` contours, no spurious ridge/valley; holes never bridged.
4. **Color:** contour vertices darker near zMin, brighter near zMax, hue = `marker.color`; two meshes distinguishable by hue.
5. **Dip:** 30° synthetic plane → dip 30.0±0.1; horizontal → 0; strike ⟂ dip tick; downslope sign correct.
6. **Overlay reg:** projected glyph centers within 1px of overlay anchors after pan/zoom/resize; callout fan removes overlap for ≥4 clustered markers.
7. **Linking:** table-row / glyph hover lights same marker in main view + violin (R9 fires).
8. **Lifecycle:** open→close ×20 leaks no GL/memory; reflects pending preview (toggle preview → marker worlds + table + lines update).
9. **Free mode:** rulers hidden; rotation works; back to Side restores exact rulers + fit.

Record in `IMPLEMENTATION_NOTES.md`: each R-handle (or built-new), concurrent-control support or fallback, interface deviations.

## Fixed (do not reinterpret)

No mesh copy, no ghost. Symbolic surface = contours + ridge/valley lines, always on, no toggles. Height-field terrain (z up, marching-squares). Per-mesh color modulated by height (dark low, bright high). World-up screen orientation; geographic-North compass (omit if no R10). PCA sets Side azimuth only. Dip = terrain vs horizontal. Orthographic camera; reuse pan+zoom (R1), orbit only in Free. No explode — label leader-lines are the sole occlusion remedy. Side default. One pin only. WP9 defaults tuned for flat terrain.
