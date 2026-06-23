# Correspondence-Detail View — Implementation Notes

Built per `ScanPin_detail_view_agent_spec.md` with the review adjustments (A1: SVG-only,
no second RenderControl; build-new for the absent "reusable" pieces). Status: feature
complete; verified by unit tests + JS DOM-stub smoke test + live server endpoint. The
pixel-level WP10 visual pass needs a human in the browser (see "Verification" below).

## WP0 — discovered handles (real)

| ID | Resolution |
|----|------------|
| R1 | **Built new (SVG-local).** No reusable per-instance ortho 3D camera controller exists — `OrbitController`/`OrbitState` is bound to the single global `Model.Camera`. Pan/zoom/rotate is implemented JS-local on `el.__dv` (mirrors the patch picker's `el.__ppv`), so it never touches the reducer. |
| R2 | **N/A (dropped).** No second RenderControl — see A1. The viewport is an `<svg>` built by `Primitives.observedRender` JS, exactly like the patch picker's canvas. |
| R6 | Reused colour source `ScanPin.DatasetColors : Map<string,C4b>` (+ `c4bToHex`). Glyphs are SVG: moving = filled disc, reference = ring+cross (new, per spec 4.3). |
| R7 | Reused `Model.MeshTransforms` + `ModelTransforms`/`RegLog.effective` + `RigidTransform.renderToWorld`. Marker preview math goes own-frame → current pose: `own = committedWorld.Backward(storedPoint)`, `world = effWorld.Forward(own)` (committed ∘ pending), so it follows the registration preview without double-transforming. Grids are sampled in **own frame** ⇒ transform-independent ⇒ survive previews/commits. |
| R8/R14 | **Built new.** Server endpoint `POST /api/query/region-grid` (`MeshAnalysis.regionGrid`): n×n vertical ray-down (Embree) in the mesh's own world frame → `z[]` + `hit[]`. `Query.regionGrid` wrapper. Heavy compute server-side per the architecture rule (the spec's "client raycast" fallback was rejected — the client has no triangle store). |
| R9 | Reused `SetCorrMarkerHover` (main-view marker brighten, already wired in `ScanPinScene`) **and** `SetChartHoverMesh` (violin column). Table-row / glyph hover posts `hov|<meshKey>` to `.pc-detail-bus` → both messages. |
| R10 | **Absent → assumed +Y = North** (UTM datasets). Compass shown in Top view; `azimuth` bearings in the table use it. If a real bearing arrives, change `north` in `buildDetailJson`. |
| R11 | **N/A.** World→screen is our own ortho projection in JS (`proj`), derived from the camera params we control; no RenderControl matrix needed. |
| R12 | Section added to `CardsPin.pinCardBody` after the `pc-corr` div (`detailSection`). Collapse uses the existing card chrome; the section is gated by `showWhen detailVisible`. |
| R13 | `Correspondence { Enabled; RefAnchor; Anchors : Map<string,MeshAnchor>; … }`; `MeshAnchor.Point`. Effective pin = `ScanPinModel.effectivePinId`. |
| R15 | Camera transition is a snap + auto-fit (no eased tween). Acceptable; left as a follow-up (see deviations). |

## Data model added

- `Model.DetailGrids : Map<string, ElevGridState>` + `Model.DetailGridPin : ScanPinId option`
  (session-only). Own-frame grids for the effective pin's marker meshes + reference.
- `DetailViewMath.fs` (WASM-free, compiled into Supertests): `ElevGrid`/`ElevGridState`,
  `DetailViewMode`, `SymbolicPatch`, marching-squares contours, ridge/valley curvature,
  `niceStep`, dip/strike plane fit, PCA `sideAzimuth`, `markerMetrics`.
- `ScanPinUpdate.ensureDetailGrids` — debounced (250 ms) postlude (mirrors `ensureRings`):
  fetches missing/stale grids in parallel; **auto-invalidates** when a marker's own-frame
  centre moves (centre-mismatch check) or the pin changes; per-mesh self-correcting.
- `Message.DetailGridsComputed`; handled in `Update.updateCore` with a pin-id stale guard.

## Deviations from the spec (recorded)

1. **A1 — SVG-only, no second RenderControl.** The spec's WP2/WP3 3D control was dropped;
   the symbolic surface, glyphs, strike/dip and the WP8 overlay all render as one SVG. The
   content is fully 2D-projectable and a second live WebGL control is a known perf ceiling
   on this backend (see memory `patch-picker-html-canvas`). The WP4 *geometry math* is
   unchanged — only the draw target moved from Sg → SVG. The camera math of WP2/WP5 is
   kept (it drives `proj`).
2. **Ridge/valley test (WP4.2).** Spec said "both-axis curvature < −rvThresh", which only
   fires on domes; a *linear* roof ridge has ~0 curvature along its length. Changed to
   "strongly convex in ≥1 axis, concave in neither" (excludes saddles) so the spec's own
   "roof → one crest polyline" example works. Unit-tested (roof/channel/plane/saddle).
3. **R10 North** assumed +Y (UTM) rather than omitted, so the Top-view compass + azimuth
   column are populated. Cheap to revert.
4. **View-switch animation (R15/WP5.3)** is a snap + auto-fit, not a 0.4 s eased tween.
5. **Heavy math in F#, projection in JS.** Per review C4, marching-squares / dip / PCA /
   niceStep live in the WASM-free `DetailViewMath` (unit-tested); the JS only projects,
   pans/zooms, and builds SVG. `niceStep` exists twice (F# for contour intervals; a 3-line
   JS copy for runtime rulers, which depend on live `pxPerMetre`).
6. **detailJson recompute surface.** `detailJson` depends on the whole selected `ScanPin`
   (via `selectedPin`), so probe/ring results dirty it and re-run marching-squares. This is
   bounded (a handful of recomputes per pin selection, not per-frame) and the
   `observedRender` `raw===last` guard skips the DOM rebuild when the JSON is unchanged.
   Possible follow-up: memoise `symbolicPatch` by `(mesh, grid identity, transform)`.

## Verification done

- `dotnet run --project src/Supertests` → **171/171** (incl. new `detailViewTests`:
  niceStep, marker metrics + North bearing, tilted-plane dip 30° / parallel contours,
  flat → no ridge/valley, roof→ridge, channel→valley, holes-not-bridged, all-holes,
  sideAzimuth ⟂ spread, transform-invariant dip).
- `POST /api/query/region-grid` live on a running server: 48×48, ~1 m relief over a 4 m
  patch, holes where the tile doesn't cover the column.
- `node --check` of the extracted renderer JS, plus a DOM-stub smoke test: builds toolbar
  (4 btns) + 41 SVG nodes + table (2 markers), row-hover posts `hov|<key>` to the bus,
  wheel-zoom + Side/Top/Free/Reset redraw without exceptions.

## Verification — in-browser (WP10)

Confirmed working in the browser (looks/works correctly): the orthographic viewport,
symbolic surface, glyphs, measurement overlay, rulers, table, view switches, pan/zoom,
and the hover linking to the main 3D view + violin.
