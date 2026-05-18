# V5 → V6 Mapping

Working reference for the V5→V6 migration. Lists where each V5 feature lives
in the current codebase. Updated as each phase lands.

## Phase 8 status (Fusion mesh, §D.10)

Phase 8 adds a per-pixel composite mode that picks the lowest-total-
error mesh from the visible set instead of the front-most one. The
winner-ID buffer + click-pickability machinery is deferred to polish.

V6 references active after Phase 8:
- `Model.fs`: `FusionMode : bool`.
- `Update.fs`: `ToggleFusionMode` message.
- `Shader.fs`: `readArray` learned a fusion branch (gated on
  `FusionMode = 1`) that re-uses the same per-mesh dataset /
  algorithm / conditioning values the heatmap uses. For each visible
  mesh with a hit at the pixel, computes `total = dErr + aErr +
  cond * 0.01` and keeps the winner. Both `minDepth` and `color`
  follow the winner, so the rendered surface stays geometrically
  coherent.
- `MeshView.composeMeshTextures`: threads a `fusionMode : aval<int>`
  uniform.
- `Gui.topBar`: "◈ Fusion" toggle button next to "◯ Pin".

V6 references still **deferred**:
- Winner-ID buffer + click-pickability. Requires WebGL2 MRT with an
  R32_UINT attachment plus CPU-side per-click readback. The
  prototype's fusion view shows the surface; clicking falls through
  to the front-most mesh's pick path (same as non-fusion). A polish
  pass can add the winner-ID flow alongside a "Place anchor on
  fusion source" affordance.
- All other §D.x — Phase 9 per Part F.

## Phase 7 status (Error provenance, §D.9)

Phase 7 wires three error sources (dataset / algorithm / conditioning)
into the pin card and a global heatmap. Two of the three §D.9
granularities are wired (per-pin stacked bar + global heatmap toggle);
per-point Ctrl-click hover readout is deferred.

V6 references active after Phase 7:
- `Model.fs`: `SensorType` DU (Rover / Sat / Photo / LiDAR / Unknown),
  `Provenance` helper module (`defaultDatasetError`, `datasetError`,
  `localConditioning`, `sourcesAt`, `dominantSource`), per-mesh state
  `MeshSensorTypes` / `MeshDatasetErrors` / `MeshAlgorithmResidual`,
  global toggles `ProvenanceHeatmap` / `FalloffZoneOnly` /
  `ProvenanceThreshold`.
- `Update.fs`: `SetMeshSensorType`, `SetMeshDatasetError` (passes
  `None` to revert to the sensor default), `ToggleProvenanceHeatmap`,
  `SetProvenanceThreshold`, `ToggleFalloffZoneOnly`. The
  `RegistrationComplete` handler now also stashes the per-mesh
  post-solve RMS into `MeshAlgorithmResidual`.
- `Cards.fs`: Point-payload section renders a real stacked bar with
  data-driven segment widths (computed via OnBoot JS over a
  `data-prov` attribute carrying [%dataset, %algo, %cond]). A numeric
  readout `D %.3fm • A %.3fm • C %.0f` sits below the legend.
- `Gui.fs`: Scene tab grows an "Error metadata" expander with a row
  per mesh (sensor segmented selector + dataset-error override slider
  + revert button), plus an "Error provenance" expander with the
  heatmap toggle, falloff-zone toggle, and threshold slider.
- `Shader.fs`: `readArray` learned a provenance branch that runs
  when `ProvenanceEnabled = 1`. For the front-most mesh at each
  pixel, computes `(dErr, aErr, cond)` from per-mesh uniform arrays
  and an anchor list, picks the dominant source, and tints the
  pixel red / green / blue. `FalloffZoneOnly = 1` gates pixels by
  anchor weight > 0.05.
- `MeshView.composeMeshTextures`: threads `ProvenanceEnabled`,
  `ProvenanceThreshold`, `FalloffZoneOnly`, `ProvenanceDataset`,
  `ProvenanceAlgorithm`, `ProvenanceAnchorCount`, `ProvenanceAnchors`
  through to the composition shader.
- `SceneGraph.fs`: builds the provenance uniform arrays (per-mesh
  dataset / algorithm errors indexed by MeshOrder; per-anchor
  (centre.xyz, sigma) packed into V4d).
- `wwwroot/style.css`: `.pc-bar`, `.pc-provenance-readout`,
  `.lp-err-meta`, `.lp-err-mesh-row`, `.lp-err-mesh-name`,
  `.lp-err-override`, `.lp-prov-body`.

V6 references still **deferred**:
- Per-point hover readout (Ctrl-click / long-press to sample
  provenance at an arbitrary surface point). The infrastructure is
  there — the shader computes the same values per-fragment — but
  there's no UI wired yet to display the readout.
- Principled Jacobian-based conditioning ("Compute Detailed
  Conditioning" button in the Registration panel). The current
  shader uses `1 / (density + ε)` from anchor weights as the fast
  heuristic; the principled version requires per-vertex Jacobian
  assembly + eigendecomposition.
- All other §D.x — Phases 8–9 per Part F.

## Phase 6 status (Registration solver, §D.8)

Phase 6 ships per-mesh registration transforms, a server-side
point-to-point ICP endpoint, and a floating Registration solver card
that exposes Traditional and Region-restricted modes end-to-end.
Point-pair + refinement is **deferred** — the model accepts the
mode and the button is greyed because the spec requires an
anchor-correspondence-linking UI that V6 has not yet added.

V6 references active after Phase 6:
- `Model.fs`: `RegistrationMode`, `RegistrationIteration`,
  `RegistrationState`; `Model.MeshTransforms : Map<string, Trafo3d>`
  (per-mesh render-space rigid transform applied on top of the
  dataset-scale + centroid-offset pipeline); `Model.Registration`.
- `Update.fs`: `SetRegistrationMode`, `SetReferenceMesh`,
  `RunRegistration`, `RegistrationComplete`, `RegistrationFailed`,
  `ResetMeshTransforms` messages plus the handler that fires
  per-peer `Query.runIcp` tasks and collects results.
- `MeshView.renderMesh` accepts a `meshTransform : aval<Trafo3d>`
  parameter and applies it as the outer-most factor in the
  composition; `buildMeshTextures` projects `model.MeshTransforms`
  per mesh, defaulting to `Trafo3d.Identity`.
- `WavefrontLoader.Query.runIcp` wraps the server endpoint and
  returns `(Trafo3d, conv, residuals)`.
- `MeshCache.runIcp` (server): point-to-point ICP with the small-
  rotation Rodrigues approximation per iteration, 6×6 Gauss-elim
  solve, Embree `GetClosestPoint` for correspondences, optional
  anchor-Gaussian weighting for region-restricted mode.
- `Handlers.icpHandler` (server) + `/api/query/icp` route +
  `IcpRequest` record.
- `Gui.registrationCard` + `Gui.registrationToggleButton` (the
  top-right "⚙ Registration" toggle).
- `wwwroot/style.css`: `.registration-card`, `.lp-mesh-list`,
  `.lp-mesh-btn`, `.reg-residual-stats`, `.reg-residual-histogram`,
  `.reg-convergence-log`.

V6 references still **deferred**:
- Point-pair + refinement solve mode. Requires anchor-to-anchor
  correspondence linking UI (§D.6.5: "Mark Correspondence" button +
  "Group as correspondence" multi-select on the Pins tab). Mode is
  selectable in the flyout but the Run button no-ops on it.
- Streaming progress updates during a solve. The server returns the
  full result in one round-trip; the `RegistrationProgress`
  message is wired but currently unused.
- All other §D.x — Phases 7–9 per Part F.

## Phase 5 status (Dual-signal Explore + ghost silhouette enhancement, §D.4 + §D.2)

Phase 5 reshapes the Explore card from a single highlight signal into
two independently-toggled signals (Feature confidence + Disagreement)
with three composition modes, and gives the Ghost-silhouette control
a three-level detail selector.

V6 references active after Phase 5:
- `Model.fs`: `SignalState`, `MixMode`, `GhostDetail`. `ExploreMode` is
  rebuilt around two `SignalState`s + `MixMode`; `ExploreMode.initial`
  defaults to both signals on, MixMode = Blended. `Model.GhostDetail`
  added alongside the existing `GhostSilhouette` toggle (the toggle is
  the enable, GhostDetail is the level).
- `Update.fs`: new `ExploreSignal` DU and `SetSignalEnabled` /
  `SetSignalThreshold` / `SetSignalColor` / `SetMixMode` messages
  replace `SetHighlightMode` / `SetSteepnessThreshold` /
  `SetDisagreementThreshold`. New `SetGhostDetail` message at the top
  level.
- `Shader.fs`: `BlitShader.exploreHeatmap` rebuilt around the dual
  signals. Feature-confidence score is `curvature × steepness` —
  curvature estimated from the angular variation of the centre normal
  against four neighbour normals (depth-derivative reconstruction);
  steepness is `1 - |dot(N, refAxis)|`. Disagreement keeps the V5
  depth-stddev formula. Both scores feed per-mix-mode compositing
  (`SideBySide` = 8-px stripe pattern, `Blended` = colour-weighted
  mean, `Alternating` = 1 Hz colour flip driven by an `ExploreTime`
  uniform sourced from wall clock).
  `BlitShader.readArray` learned to modulate the ghost composite with
  a screen-space depth-Laplacian curvature term when
  `GhostDetailMode > 0`. Mode 1 = "+ Curvature" cool→warm gradient
  blended at 35 %; mode 2 = "+ Terrain features" widens the
  high-curvature band so ridges crest through (cheap surrogate for
  the spec's polyline rasterisation).
- `MeshView.composeMeshTextures` threads the new `ghostDetailMode`
  uniform through to `readArray`.
- `SceneGraph.exploreTex` re-wires the explore shader bindings:
  `FcEnabled` / `FcThreshold` / `FcColor`, `DgEnabled` / `DgThreshold`
  / `DgColor`, `MixModeInt`, `ExploreTime`, plus the shared
  `ReferenceAxis` and `HighlightAlpha`.
- `Gui.fs`: `exploreCard` rebuilt as a two-row layout — each row has
  its own toggle + sensitivity slider, with a Mix selector
  (Blended / Side-by-side / Alternating) that only shows when both
  signals are on. Scene tab grows a "+ Curvature / + Terrain"
  segmented selector below the Ghost silhouette toggle, hidden until
  the toggle is on.
- `wwwroot/style.css`: `.explore-signal-row`, `.explore-signal-controls`,
  `.explore-mix-row`, `.lp-ghost-detail`.

V6 references still **deferred**:
- Curvature data computed in mesh space (cached per mesh) — Phase 5
  uses a screen-space curvature proxy, which is enough for the spec's
  acceptance criteria but costs accuracy when the camera is grazing.
  A mesh-local curvature texture is a Phase 9 polish target.
- True terrain-feature polylines (ridge lines rasterised onto the
  ghost slice). The current "+ Terrain features" mode widens the
  high-curvature band as a cheap visual surrogate.
- All other §D.x — Phases 6–9 per Part F.

## Phase 2 status (Anchor Sphere primitive, §D.6)

Phase 2 reshaped `ScanPin` from a selection-prism cylinder into an
anchor sphere with `Centre` / `Radius` / `Sigma` / `Payload` / `HostMeshName`
/ `CorrespondenceLinkId` / `CreatedAt`. `SelectionPrism` and
`FootprintPolygon` are gone. The single-click placement gesture is wired
through `Sg.OnTap` in `View.fs`, the ghost-preview cval `placementHover`
threads into `SceneGraph.build` → `ScanPinScene.build`, and the top-bar
`◯ Pin` button toggles `AnchorPlacement`. Adjustment flyout exposes
Radius + σ sliders; σ clamps to ≤ Radius in `SetAnchorSigma`.

V6 references active after Phase 2:
- `ScanPinModel.fs`: `PointPayload`, `PayloadType`, `CorrespondenceLinkId`,
  reshaped `ScanPin`, simplified `PlacementState = PlacementIdle |
  AnchorPlacement | AdjustingPin of ScanPinId`.
- `PinGeometry.buildIcosphere` / `buildSphereOutline` — the
  anchor-sphere primitive.
- `ScanPinScene.sphereShell` — translucent outer (Radius) + inner (σ) +
  centre marker + great-circle outline.
- `Update.fs`: `EnterAnchorPlacement`, `PlaceAnchor`, `SetAnchorRadius`,
  `SetAnchorSigma`, `ScanPinUpdate.defaultRadius`, `makeAnchor`.

V6 references still **deferred**:
- Lasso placement (§D.6.1's lasso variant of anchor placement) — Phase 3
  wires up the §D.3 clip lasso but not yet the anchor-placement lasso.
- Payload-specific cards (§D.7) — Phase 4.
- `CorrespondenceLinkId` issuance / management (§D.6.5) — Phase 4.
- True Gaussian-modulated volume rendering (§D.6.3 in its full form)
  — Phase 2 ships the two-shell approximation the spec describes as
  "translucent outer + inner hard-edged sphere at σ contour". A real
  per-fragment Gaussian alpha rebuild is reserved for a later polish
  pass if visual evaluation flags the approximation.

## Phase 4 status (Payloads, §D.7) — in progress

Phase 4 in the original plan covers Point / Line / Patch payloads plus
2D-3D linkage (§D.12). To keep each landing testable, Phase 4 is split
into four sub-phases:

- **4a: Point payload + payload-type selector** — landed.
  Card body now renders numeric readout (Centre / Radius / σ), an
  error-provenance stacked bar (placeholder, real data lands in Phase
  7), and a reliability-weight slider. The Adjustment flyout grows a
  Payload-type segmented selector (Point / Line / Patch) and a
  Reliability slider that is only shown when the active pin has a Point
  payload. `PayloadType` is extended to `Point | Line | Patch` with
  full record types (`LinePayload`, `PatchPayload`) so later sub-phases
  fill in geometry without touching the DU shape again.
- **4b: Line payload — elevation isoline sub-mode** — landed.
  Server gained `/api/query/isoline` (Embree-backed marching: triangle-
  edge straddle test → segment soup → edge-keyed adjacency graph →
  connected-component walk → longest line nearest seed). Client
  `Query.isoline` wraps the endpoint. Flyout grows a Line-mode toggle
  (Elevation / Ridge — Ridge greyed) plus an elevation slider centred
  on the pin's render-space Z. `Update.fs` reacts to `LineKind`
  switches or elevation drags by firing the query in a background
  task; the result lands as `IsolineComputed(id, V3d[], elevation)`.
  3D rendering: `ScanPinScene.pinLines` draws the polyline as
  pixel-constant lines (Lines.render, BlendMode.Blend, DepthTest.None,
  passOne) coloured from `DatasetColors[HostMeshName]` or yellow when
  selected. Card body: SVG arc-length × elevation plot via OnBoot JS
  + MutationObserver pattern.
- **4c: Line payload — curvature ridge sub-mode + cross-mesh tracing**
  — landed. Server added `/api/query/curvature-ridge` (dihedral-angle
  edge classification: per-edge angle between the two adjacent triangle
  normals, edges above threshold become ridge edges, vertex-keyed
  adjacency walk produces the polyline). The ridge endpoint also
  returns per-vertex peak dihedral as scalars so the card plot's y-axis
  carries the "curvature" signal the spec asks for. Cross-mesh tracing
  is wired for **both** sub-modes: on any payload-kind change or
  line-mode change, the Update handler fans out the query to every
  other visible mesh in the dataset using the same world-space seed
  point. Results land via `LineCrossMeshComputed(id, mesh, pts,
  scalars)` and are stored in `LinePayload.CrossMeshTraces`. The 3D
  renderer iterates host + cross-mesh traces; each polyline is
  coloured from its host mesh's palette colour (yellow when that pin
  is selected and the line is the host trace). The card SVG renders
  every trace plus a small per-mesh legend with palette swatches; the
  host trace is starred and drawn thicker.
- **4d: Patch payload + 2D-3D linkage (§D.12)** — landed.
  Server added `/api/query/patch` doing the azimuthal-equidistant unwrap:
  BVH-sphere triangle query, average-triangle-normal tangent plane,
  Dijkstra on the edge graph for geodesic distance, then
  `patch_coord = (d cos θ, d sin θ)` with θ measured from world +Y
  projected into the tangent plane. Response carries `refDirWorld` +
  `normalWorld` so the 3D footprint can be rebuilt without re-deriving
  the tangent plane. Client adds `Query.patch`, `PatchComputed`
  message, and a fan-out from `ChangePayloadType(_,PatchKind)`.
  `PatchPayload` gains `RefDirWorld` + `NormalWorld` fields beyond the
  spec's §C.3 shape (additive, not breaking).
  Cards: patch section renders an SVG scatter of projected points
  with elevation-coloured fills (cool→warm), a coloured ring matching
  the host mesh palette, and a compass-rose arrow toward project
  north (`CompassNorth` direction).
  ScanPinScene: per-pin `pinPatchRings` draws a great-circle ring in
  the tangent plane plus a compass arrow at the anchor, both linked
  to the same palette colour as the card's frame.
  §D.12 coloured frame: every pin card grows a `.pin-card-color-bar`
  4px strip at the top with `background = DatasetColors[host]`, so
  Point / Line / Patch cards all link visually to the 3D rendering
  regardless of which payload they carry.

V6 references active after Phase 4a:
- `ScanPinModel.fs`: full `PayloadType` DU (`Point | Line | Patch`),
  `LineMode`, `LinePayload`, `PatchPayload`, `PayloadKind` tag,
  `PayloadType.kind` / `PayloadType.defaultFor` helpers.
- `Update.fs`: `ChangePayloadType` and `SetReliabilityWeight` messages;
  `ScanPinUpdate` handles payload swaps (destroys + reinstantiates with
  defaults) and Point-only weight edits.
- `Gui.fs`: payload-type selector + reliability slider in the placement
  flyout, both gated on the pin's active payload kind.
- `Cards.fs`: per-payload card body, with Point fully wired and Line /
  Patch showing "coming in Phase 4b/4d" stubs.
- `wwwroot/style.css`: `.pc-readout`, `.pc-provenance`, `.pc-bar*`,
  `.pc-legend-item*`, `.pc-reliability`, `.lp-reliability-row`.

V6 references still **deferred** (rest of Phase 4):
- Line payload: server endpoints (`/api/query/isoline`,
  `/api/query/curvature-ridge`), `LinePayload.Points` population, 3D
  polyline rendering, cross-mesh traces, 2D arc-length plot in the
  card.
- Patch payload: server endpoint (`/api/query/patch`), geodesic-BFS
  rasterisation, compass-rose overlay, bidirectional 2D-3D hover.
- §D.12 2D-3D linkage: coloured frame (3D rectangle + matching card
  border), compass rose in both views, bidirectional hover.

## Phase 3 status (Mesh-wheel + Polygonal lasso, §D.1 + §D.3)

Phase 3 adds the V6 mesh-wheel scroll-cycle interaction and the
polygonal-lasso clip. The previously-dummy `HostMeshName` field
(introduced placeholder-only in Phase 2) is now stamped from
`ActivePickingLayer` when an anchor is placed.

V6 references active after Phase 3:
- `Model.fs`: `MeshBounds : Map<string, Box3d>` (populated from the
  bboxes endpoint), `ActivePickingLayer : string option`,
  `LassoDrawing : LassoDraft option`, `LassoVolume : LassoVolume
  option`. New types `LassoDraft` and `LassoVolume`.
- `Update.fs`: `SetActivePickingLayer`, `LassoBegin`,
  `LassoAddVertex`, `LassoCommit`, `LassoCancel`, `LassoClear`.
- `OrbitController.fs`: wheel handler removed from `getAttributes` —
  View.fs registers its own wheel handler that arbitrates between
  zoom and mesh-wheel cycle.
- `View.fs`: `rayBoxT` slab-test, `pickRay` (re-added from V5),
  `Dom.OnMouseWheel` handler that cycles `ActivePickingLayer` when
  ≥ 2 visible mesh bboxes intersect the cursor ray (Alt forces
  zoom regardless), `cursorScreen` cval for cursor-adjacent label
  + lasso preview, `Sg.OnTap` lasso branch (adds vertex on click,
  double-tap commits with view+proj+vpSize captured).
- `Shader.fs`: `MaxLassoPlanes = 32`, `LassoPlaneCount` and
  `LassoPlanes` uniforms, fragment-shader plane sweep test inside
  `BlitShader.clippy`.
- `MeshView.fs`: pads `LassoVolume.Planes` to 32 entries and threads
  the uniforms into every off-screen mesh task.
- `Gui.fs`: `meshWheelLabel` (cursor-anchored mesh-name label),
  `lassoOverlay` (SVG polyline rendering for the in-progress
  polygon with closing dashed segment to cursor), Clip-tab "Lasso"
  collapsible section with Draw / Clear buttons.
- `wwwroot/style.css`: `.mesh-wheel-label`, `.lasso-overlay`,
  `.lp-clip-actions`, `.lp-sublabel-hint`.

V6 references still **deferred**:
- Anchor-placement lasso variant (§D.6.1's "Lasso placement —
  user draws a closed 2D polygon on the viewport; the sphere is
  fitted to enclose the back-projection of the polygon"). The §D.3
  *clip* lasso is in place; the §D.6.1 *anchor-placement* lasso
  reuses the same gesture but produces an anchor instead of a clip
  volume. Deferred to Phase 4 alongside payload work, since the
  anchor-placement lasso most cleanly slots in alongside the
  patch-payload UI.
- Touch-device two-finger swipe substitute for scroll-wheel cycling
  (§D.1 specifies it; Phase 3 ships only the desktop scroll-wheel
  path).
- Lasso self-intersection detection (§D.3 edge case). Phase 3
  accepts any 3+-vertex polygon; user can re-draw if the result is
  wrong.
- All other §D.x — Phases 4–9 per Part F.

Conventions:
- All paths relative to `src/Superprojekt/` unless noted.
- `Cards.fs` is the current home of the pin floating card (the old
  `GuiPins.fs` referenced in `CLAUDE.md` no longer exists — pin GUI was
  split across `Gui.fs` (left-panel pin section, placement flyout, mode
  buttons) and `Cards.fs` (floating diagram card)).
- The previously-described "core sample mini 3D viewport" inside the pin
  card has already been removed in a prior cleanup; only the helper
  `PinGeometry.coreSampleTrafo` remains.

---

## §B.1 — Cut plane as placement-driving primitive — REMOVE

**Types / fields (V5-only):**
- `ScanPinModel.fs:24-27` `CutPlaneMode = AlongAxis | AcrossAxis`
- `ScanPinModel.fs:29-32` `CutResult` (cut-polylines per mesh)
- `ScanPinModel.fs:89-95` `ExtractedLinesMode { ShowCutPlaneLines; ShowCylinderEdgeLines }`
- `ScanPinModel.fs:97-99` `CutAspectMode = CutAspectFit | CutAspectOneToOne`
- `ScanPinModel.fs:101-107` `CutLineHover`
- `ScanPin` fields (`ScanPinModel.fs:113,115,116,122,123,125,126`): `CutPlane`,
  `CutResults`, `CutResultsPlane`, `GhostClipCutPlane`, `ExtractedLines`,
  `CutAspect`, `CutLineHover`

**Messages (Update.fs):**
- L34 `CutResultsLoaded of ScanPinId * Map<string,CutResult>`
- L83-85 `SetCutPlaneMode | SetCutPlaneAngle | SetCutPlaneDistance`
- L94-95 `SetGhostClipCutPlane | SetShowCutPlaneLines`
- L96 `SetShowCylinderEdgeLines`
- L101-102 `SetCutLineHover | SetCutAspect`

**Update handlers:**
- `Update.fs:298-299` pin creation populates CutPlane / CutResultsPlane
- `Update.fs:316,331-332` Auto/Plan/Profile cut-plane initialisation
- `Update.fs:340-347` SetCutPlaneMode / Angle / Distance handlers
- `Update.fs:384-388` SetGhostClipCutPlane, SetShowCutPlaneLines
- `Update.fs:390-391` SetShowCylinderEdgeLines
- `Update.fs:416-420` SetCutLineHover, SetCutAspect
- `Update.fs:600-602` `EditPin` infers mode from CutPlane variant
- `Update.fs:616-622` `CutResultsLoaded` handler
- `Update.fs:727-786` debounced batched plane-intersection query (uses
  `Query.planeIntersectionBatch`, emits `CutResultsLoaded`)
- `Update.fs:747-755` plane-frame math reading `pin.CutPlane`

**Geometry / rendering:**
- `PinGeometry.fs` — search for `cut|Cut` finds the cut-plane primitives
  (`buildCutPlaneQuad`, `buildCutPlaneFill`, `buildCutPlaneEdges`,
  `cutPlaneCorners`, `cutPlaneFrame`, tick label helpers).
- `ScanPinScene.fs` — cut plane quad rendering + drag-pick + edits preview.
- `MeshView.fs` — `GhostClipCutPlane` participates in clip uniform packing
  (cylinder clip `M44d` row 0 fourth element).

**Off-screen pipeline interaction:**
- `Shader.fs::BlitShader.clippy` reads `CylClip` uniform; the cut-plane
  contribution is a single float (forward extent) inside that uniform
  matrix — needs to keep the rest of `CylClip` but zero out the cut-plane
  half-plane test.

**UI (Gui.fs / Cards.fs):**
- `Gui.fs:358-391` "Cut Plane" sub-section in placement flyout
  (Vertical / Horizontal mode buttons).
- `Gui.fs:401-419` Ghost-clip "+ Cut" toggle in placement flyout.
- `Cards.fs:65-130` `encodeDiagramJson` (SVG cross-section)
- `Cards.fs:180-239` `computeCutSnap` (mouse→nearest polyline)
- `Cards.fs:243-393` `diagramSvg` (entire 2D cross-section card section
  including OnBoot JS that renders the SVG)
- `Cards.fs:399` `diagramSvg env selectedPin` call inside `pinCardBody`
- `Cards.fs:424-444` "1:1 / Fit" CutAspect toggle
- `Cards.fs:482-493` Ghost-clip "Cut" toggle in pin card

**Server endpoints:**
- `Handlers.fs` `/api/query/plane-intersection` and `/api/query/plane-intersection-batch`
  + the `PlaneIntersectionRequest` / `PlaneIntersectionBatchRequest` types.

**Shared infrastructure to PRESERVE:**
- `PinGeometry.axisFrame` (orthonormal frame helper).
- `Cards.shortName`, `Cards.c4bToHex`, `Cards.niceTicks`, `Cards.parseFloat`,
  `Cards.checkedIf` — generic utilities.
- The card drag / collapse / reattach machinery in `Cards.renderCards`
  (the "floating card pattern"); only the SVG-diagram body inside it
  is V5-specific.
- The flyout pattern (`.placement-flyout` container, `placementHint`,
  `Commit` / `Discard` row in `Gui.fs:421-433`).

---

## §B.2 — Stratigraphy diagram (cylindrical unwrap) — REMOVE

**Files to delete wholesale (after callers are gone):**
- `Stratigraphy.fs` (entire module — `compute`, `tryBracket`,
  `floodContinuousBand`, `floodContinuousBand3D`, `buildBandCache`,
  `lookupBand`, `lookupBand3D`)
- `StratigraphyView.fs` (entire diagram renderer)

**Types in ScanPinModel.fs:**
- L51-65 `StratigraphyColumn`, `StratigraphyData`
- L67-73 `BandCache`
- L75-77 `StratigraphyDisplayMode = Undistorted | Normalized`
- L79-83 `BetweenSpaceHover`
- ScanPin fields L118-120,124: `Stratigraphy`, `BandCache`,
  `StratigraphyDisplay`, `BetweenSpaceHover`
- ScanPinModel field L166: `BetweenSpaceEnabled`
- `CardContent = StratigraphyDiagram of ScanPinId` (L200-201) — only payload
  type; the entire `CardContent` discriminated union loses its lone case.

**Messages (Update.fs):**
- L35 `StratigraphyComputed of ScanPinId * StratigraphyData * BandCache`
- L92 `SetStratigraphyDisplay of ScanPinId * StratigraphyDisplayMode`
- L97 `ToggleBetweenSpaceEnabled`
- L98-100 `HoverBetweenSpace | PinBetweenSpaceHover | ClearBetweenSpaceHover`

**Update handlers:**
- `Update.fs:378-379` SetStratigraphyDisplay
- `Update.fs:393-414` between-space hover handlers
- `Update.fs:623-629` StratigraphyComputed handler (pin field write)
- `Update.fs:787-820` debounced background `Stratigraphy.compute`
  + `Stratigraphy.buildBandCache` task
- Pin creation in `Update.fs:185-187` initialises Stratigraphy/BandCache/Display

**Geometry / rendering:**
- `PinGeometry.buildBetweenSpaceSurfaces` (PinGeometry.fs:25-107)
- `ScanPinScene.betweenSpaceBand` (scene-graph node — confirm exact line)

**UI:**
- Cards.fs:402-420 "pin-card-strat" section (calls `StratigraphyView.render`,
  hover-tip label readout)
- Cards.fs:445-462 "Flat / Norm" toggle
- Cards.fs:464-468 "Gap" between-space toggle

**Server endpoints:**
- `/api/query/cylinder-eval` (Handlers.fs) — Stratigraphy module calls it.

**CardSystem interplay:**
- `CardUpdate` (Update.fs:104-153) only knows `StratigraphyDiagram` cards;
  when `CardContent` loses its sole case, the card-system code shrinks to
  a no-op stub. Plan: keep `Card` / `CardSystemModel` infrastructure (used
  for floating-card pattern that V6 §D.7 needs) but adapt or stub the
  `CardContent` type until V6 introduces real payload-specific cards.

**Shared infrastructure to PRESERVE:**
- The card-system data model (`Card`, `CardAttachment`, `CardSystemModel`,
  Card drag/redock/collapse logic) — V6's floating pin card reuses it.

---

## §B.3 — Core sample (mini 3D viewport) — REMOVE

**Already mostly removed.** Only residue:
- `PinGeometry.coreSampleTrafo` (PinGeometry.fs:14-21).
- `CoreSampleViewMode` / `CoreSampleRotation` / `CoreSamplePanZ` /
  `CoreSampleZoom` — described in CLAUDE.md but NOT present in current
  `Model.fs` (already gone).
- `BlitShader.coreClip` — described in CLAUDE.md; check `Shader.fs` for
  residue.

**Action:** delete `coreSampleTrafo`; if `BlitShader.coreClip` exists,
delete it; remove any "core sample" CSS classes from `style.css`.

---

## §B.4 — Profile / Plan / Auto placement modes — REMOVE

**Types in ScanPinModel.fs:**
- L129-132 `PlacementMode = ProfileMode | PlanMode | AutoMode`
- L134-136 `ProfilePlacementState`
- L138-140 `PlanPlacementState`
- L142-148 `AutoPreview`
- L150-151 `AutoPlacementState`
- L153-158 `PlacementState` — the four non-Idle cases plus
  `AdjustingPin of ScanPinId * PlacementMode`
- L165 ScanPinModel field `LastPlacementMode`

The `Placement` field on `ScanPinModel` and the `AdjustingPin` case are
KEPT — Phase 2's V6 single-click placement reuses them; only the three
mode-specific gestures go.

**Messages (Update.fs):**
- L66 `SelectPlacementMode of PlacementMode`
- L69-71 ProfileClick / Preview
- L73-76 PlanDragStart/Update/End/MedianElevationLoaded
- L78-80 AutoHoverUpdate / AutoClick / AutoDerivationComplete

`CancelPlacement` (L67) and `CommitPin` (L88) stay.

**Update handlers:**
- `Update.fs:212-216` `initialStateFor`
- `Update.fs:220-222` SelectPlacementMode
- `Update.fs:228-332` all the Profile/Plan/Auto handlers
- `Update.fs:649,658-726` post-creation camera centring + server kickoffs

**Geometry:**
- `PinGeometry.fs` — `placementPreviewPrism`, `deriveAutoPreview`,
  `autoPreviewPrism`. (Preserve the *placement preview* style of conditional
  sg rendering for V6 reuse.)

**UI (Gui.fs):**
- L68-102 `placingMode` AVal + segmented `modeButton` + the
  Profile / Plan / Auto button group.
- L313-320 placement hint text.

**View.fs (3D pointer wiring):**
- The ProfileClickFirst/ProfileClickSecond/ProfileHover/PlanDrag/AutoHover/
  AutoClick handlers in the renderControl pointer events.
- Auto-hover ray-grid debounce + Query.rayGrid.

**Shared infrastructure to PRESERVE:**
- The translucent ghost-preview render path (used in V6 by §D.6 single-click
  ghost preview).
- `PinGeometry.axisFrame` and the prism-radius / extent slider helpers in
  `Update.fs:158-204` (`circleFootprint`, `setRadius`, `makePin` shape).

---

## §B.5 — Revolver disk — REMOVE

**Types / fields (Model.fs):**
- L27-31 `RevolverSettings`
- L33-34 `RevolverSettings.initial`
- Model fields L79,81,100,124,126,144 — `RevolverOn`, `RevolverCenter`,
  `RevolverSettings` (+ initial values).

**Messages (Update.fs):**
- L18 `CycleMeshOrder of int` (only used by revolver UI — verify)
- L19 `ToggleRevolver`
- L21 `SetRevolverCenter of V2d`
- L48 `SetRevolverRadius of float`

**Update handlers:**
- L486-489 `CycleMeshOrder`
- L490-491 `ToggleRevolver`
- L494-495 `SetRevolverCenter`
- L592-593 `SetRevolverRadius`

**UI (Gui.fs):**
- L193-213 `revolverBar` (cycle buttons + size slider)
- L274-280 Revolver toggle inside Mesh section header
- Top-level layout wiring (View.fs) — `revolverBar` is mounted as overlay.

**View.fs:**
- Shift-key revolver activation, pointer-move `SetRevolverCenter`.
- `revolverActive` / `revolverBase` aval construction.

**Scene graph (SceneGraph.fs):**
- The `disk` function — confirms ring/circle rendering.
- Calls in the main build pipeline that place the disk on the composition.

**Shader:**
- `BlitShader.readArraySlice`, `BlitShader.readArraySliceColor` —
  confirm if they survive (the revolver uses them; the fullscreen tile mode
  uses `readArraySlice` too). Likely keep `readArraySlice*` for fullscreen,
  remove only revolver-specific call paths.

**CSS:** `.rev-bar` and related classes in `wwwroot/style.css`.

**Shared infrastructure to PRESERVE:** none — V6 mesh-wheel (§D.1) is a
fresh implementation.

---

## §B.6 — Summary mesh generation — REMOVE

**Status:** no matches for `[sS]ummary` anywhere in `src/`. The feature
is a placeholder mentioned only in `description.md` ("What the app is
*not*: …summary mesh generation…"). Nothing to delete in code; the
description.md prose is V5-era documentation and out of scope to mutate.

---

## §B.7 — Pin "adjustment" flyout (V5 controls) — REMOVE V5 CONTROLS

**The flyout container survives.** What goes:

**UI (Gui.fs:301-434, the `placementFlyout` function):**
- Survives: `flyoutClass` plumbing, "Placing Pin" title, hint text,
  Commit / Discard row (L421-433).
- Goes: Radius slider (L334-340) **stays for V6**, Length range slider
  (L342-352) **stays** as a V6 anchor-sphere candidate? — *Spec §B.7*
  says only "Radius, Length-above/below, Cut-plane mode/slider, Ghost-clip
  controls all go." Per the spec, **Length goes**. Radius **stays**
  (V6 §D.6.4 lists "Radius slider" first).
- Goes: Cut Plane section (L358-391).
- Goes: Ghost-clip row (L393-419).

**Update handlers:**
- Keep: SetFootprintRadius, CommitPin, CancelPlacement.
- Remove: SetPinExtent (L87), SetCutPlaneMode/Angle/Distance (L83-85),
  SetGhostClip(L93)? — note Solo *Ghost-clip* survives per the spec wording
  "Length-above/below, Cut-plane mode, Cut-plane slider, and the
  Ghost-clip controls all go" — i.e. **all** ghost-clip controls go. So
  `SetGhostClip` (Solo) goes too. `SetGhostClipCutPlane` (+ Cut) goes.
  `SetFootprintScale` (L86) — used only by an alias that mirrors radius
  in V5; goes with Length.

**Shared infrastructure to PRESERVE:**
- `.placement-flyout` CSS layout, `Commit` / `Discard` button styling
  in `wwwroot/style.css`.
- The `placementHint` AVal pattern (V6 single-click placement reuses it
  with a single hint string).
- `Primitives.compactButtonBar`, `Primitives.compactToggle`,
  `Primitives.inlineSlider`, `Primitives.inlineRangeSlider` —
  generic UI helpers, all stay.

---

## Verification anchors (§B.8 smoke test)

After each deletion run:
1. Build: `dotnet build src/Superprojekt/Superprojekt.fsproj` clean.
2. Run: server (`Superserver`) + client; confirm dataset loads, scene
   renders, the left panel and top bar render (mode buttons absent),
   no missing-method errors in the browser console.
3. No occurrences of deleted type names (`CutPlaneMode`, `StratigraphyData`,
   `RevolverSettings`, `PlacementMode`, …) remain in build output.

## Non-source artefacts referenced

- `description.md` — V5 prose description; left intact (read-only baseline).
- `scanpin_v6_architecture.md` — V6 spec; source of truth.
- `CLAUDE.md` — partially outdated (mentions `GuiPins.fs`, core sample, etc.
  that have shifted); will be brought back in line with V6 once the code
  is reshaped.
