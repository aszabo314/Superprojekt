# Worklog — visibility / linking cleanup pass

## Objectives
1. Clean up automatic visibility state changes (solo/isolation, per-mode defaults, stale caches).
2. Small-multiples ◐ isolate: toggle on/off on same button; exit resets all visibility toggles to ON; vis toggles locked while isolation active; workflow switch ends isolation.
3. Inspect mode: mesh focus (≠ ref) → false-colour on that mesh + isolate {reference, mesh}; pin focus → mesh isolation off, pin isolation on; both zoom 2D + 3D cameras tightly.
4. Audit linked interactions: mesh click ⇒ shared focus + 3D fly; pin click (rail) ⇒ 3D tight fly + focus panel zooms onto pin on current mesh; matrix cell ⇒ 3D very close + focus switches mesh & zooms onto the correspondence.
5. Fix stale state / code smells along the way.

## In progress
- Z-overlap gating for M3C2 implemented + live-verified — awaiting the user's
  visual inspection (the running dev server on :8002 has the old code; restart
  it to see the change).
- Dock resize handle implemented, build + JS parse green — awaiting visual
  inspection (grip visibility, drag feel, overlays following, chart re-render
  while dragging).
- Dead-code prune (below) — builds + tests + integration all green; awaiting a
  browser smoke pass (probe → matrix/chart, solve, seeding, no missing styles).
- 360° focus view (below) — client build green, no new shader; awaiting a
  browser pass (look-around drag feel, fov zoom anchoring, correspondence
  pick/aim-ghost in 360°, per-mesh view memory, Top view unchanged).
- Show-overlays rework (below) — client build green; awaiting a browser pass
  (white-out read, thick flags, name-tag placement/tracking while orbiting,
  in-browser compile of the edited mesh shader — ShaderCache entry is stale,
  falls back to runtime compile; rerun ./precompileShaders.sh at leisure).
- Pick-buffer fix (below) — build green; awaiting a browser check that
  double-click recenter lands on the mesh again, armed 3D-surface
  correspondence clicks land on the surface, and outlines/isolines still draw.
- Difference gradient + value isolines (below) — build green, but BOTH mesh
  shaders changed: needs the in-browser compile check (ddx/ddy → dFdx/dFdy in
  ESSL3) plus a visual pass (yellow zero readable, isoline density/width,
  clamp-region suppression, matrix-cell grey-vs-yellow gate step). ShaderCache
  entries stale again → runtime compile fallback.
- Pin cross-section charts in the Overlays hold (below) — server endpoint
  live-verified on :8004 (disc-clip radii exact, posed-mesh v-shift exact,
  contact-rings regression clean after the tracer refactor), client typecheck
  green, no shader changes; awaiting a browser pass (chart legibility /
  vertical exaggeration, faint parallel-slice read, anchor dots, Before/After
  swap + Peek flip, 3D centre-cut lines on the white-out, orbit smoothness of
  the two-attribute label renderer).
- Per-pose Inspect maps (below) — client typecheck green, no server or shader
  changes; awaiting a browser pass (Before/After toggle repaints difference +
  variance, Peek flips paint with geometry in 3D and the focus views, no
  flash-of-stale colour right after a solve).
- GUI cleanup batch (below) — client typecheck green, tests green; awaiting a
  browser pass (hotkey I peeks, Overview hold, JOB loads by default, camera
  rests on the reference, anchor XYZ inputs commit + ROI-toast, edit-point
  disarms after a pick, legend titles + zero-label offset, rail chrome).
- Before-only correspondences + solve provenance (below) — client typecheck
  green, tests green; awaiting a browser pass (place a pin in After view →
  markers appear only for Before-state overlap; edit/resize a solved pin →
  "Registration cleared" toast + snap to Before; ✎/placement/radius snap the
  view to Before; XYZ inputs read-only in After).

## Before-only correspondences + solve provenance (2026-07-08)
Triple-checked the pin zone-of-influence logic around correspondence points and
fixed four holes. New invariant set: the Before state is the single source of
truth — anchors are detected/placed/edited there only; After exclusively
displays the moved points; a solve is only as valid as the points it consumed.

- **Seeding evaluated at the displayed pose** (the bug behind "a mesh moved
  into a pin's area and gained a marker after registration"): placing a pin
  while viewing After seeded anchors against the *solved* poses.
  `seedAnchorsCore` now maps every mesh with `displayedWorldAt RegBefore` —
  detection is pose-independent, always the Before truth.
- **Two zones separated** (they were conflated): `ScanPin.roiReach`
  (≈ sqrt(r² + (probeLen/2)²), ~10 m — the probe cylinder's bounding sphere)
  stays the *measurement* membership rule (`InRoi`, matrix cell emptiness),
  but a **correspondence anchor must lie within the pin sphere itself**
  (`InnerRadius`) — enforced at seed accept (previously roiReach — an auto
  anchor could legally sit metres outside the pin), at pick (was already
  clamped), and at resize. The seeded refAnchor projection is range-checked
  too. `AnchorsSeeded` now drops every re-evaluated auto marker before
  re-adding the accepted ones, so a marker that no longer qualifies cannot
  linger.
- **Editing is Before-only**: `ToggleCorrArm` (the ✎ button), pin placement
  and the radius slider snap the committed view back to Before
  (`UpdateHelpers.applyRegView` — the extracted `SetRegView` body, so the
  pose-pair caches swap correctly on every path). `SetRegView` disarms a live
  editor; `PickCorrespondenceAt` rejects any pick while After is displayed —
  including a held Peek (the new I hotkey made hold-peek-and-click easy; the
  raycast hits the peeked solved geometry, so a commit would store an
  off-surface Before point); the dock XYZ inputs disable in After with a
  "read-only" note.
- **Solve provenance** (`Model.SolveInputs`, type in RegistrationModel.fs):
  `SolveCoarse` snapshots refMesh + every (pin, mesh) anchor point fed to the
  solver. The `ensureSolveValidity` postlude (first in the update chain)
  compares the snapshot against the live pins after every message — any
  tracked pin deleted or point moved (pick edit, XYZ edit, resize kill, pin
  delete) clears the registration wholesale (SolvedTransforms + snapshot,
  snap to Before, probes/rings invalidated, toast). Untracked pins (placed
  after the solve) don't invalidate.
- **Resize kill**: `SetInnerRadius` now drops the pin's RefAnchor and any
  anchor whose Before-pose world position falls outside the new radius — the
  validity postlude then clears a registration that consumed one.
- Docs: CLAUDE.md state-rules bullet ("Correspondences are Before-only").

## GUI cleanup batch (2026-07-08)
Sixteen small UI/UX changes in one pass:

- **Top bar**: the ◎ Isolate spring-hold is gone entirely (button, hotkey,
  `IsolatePeekHeld`/`SetIsolatePeek`, and the isolation OR-term in
  `MeshView.anchorGhost`); the gear popover's persistent "Isolate pins" toggle
  (AnchorGhostMode) survives. The **I hotkey** now drives the registration
  Peek (same solved-only gate as the button). 🎨 Overlays is renamed
  **🗺 Overview** (hold semantics unchanged, hotkey O). The burger button is
  gone — the workflow rail is always visible; `MenuOpen`/`SavedMenuOpen`/
  `ToggleMenu` and `ScanPinModel.isPlacing` were pruned with it.
- **Rail**: the mode headers show a status pill only on the (renamed)
  **Register** section (`WorkflowStep.title Correspondence = "Register"`);
  the pin×mesh matrix corner lost its "pin \ mesh" micro-label; the pins head
  lost its "Pins" title and its button reads **○ New pin**, left-aligned. The
  "Ready to align" diagnostic now appends the display numbers of the solvable
  meshes ("Ready to align 1,3") — patched in GuiRail from
  `Readiness.pairCounts` + MeshOrder, keeping the pure engine (and its tests)
  untouched.
- **Dock**: Overview mode = one hint line (the old focused-mesh card was
  redundant with the tiles). Register mode = pin chip + radius slider + a new
  per-mesh **correspondence coordinate editor** — X/Y/Z number inputs in
  metric world at the committed pose; edits route through
  `PickCorrespondenceAt` (same ROI clamp/toast + ref-vs-mover handling as a
  surface pick), disabled until that mesh has a point. Inspect mode lost the
  axis-note line and the in-canvas "fill = … · outline = …" caption.
- **Legend (Inspect 3D)**: ensemble title is now **Variance σ**; the
  difference title names the compared meshes ("Difference 2 vs 1 (M3C2)");
  the zero tick's label renders one line lower (y 28 → 37) so it can no
  longer collide with an edge label on an asymmetric range.
- **Defaults**: `data/default.txt` = **JOB**; the on-load camera now rests on
  the default reference mesh (its pano centre, framed to its own bounds ×0.6)
  instead of the whole padded scene — whole-scene framing remains the
  fallback when the reference has no bbox yet.
- **Edit point**: a committed pick now **disarms** the editor (one click =
  one edit) in both surfaces (3D tap + focus click, both route through
  `PickCorrespondenceAt`); an out-of-ROI click keeps it armed so the "Pick
  inside the pin ROI" toast doesn't force a re-arm.

## Inspect false-colour maps are per-pose (Before/After + Peek) (2026-07-08)
The Inspect scalar fields (per-mesh difference, ensemble variance) were fetched
at the committed pose only: toggling Before/After kept painting the old pose's
values (stale — the geometry moved, the colours didn't), and the Peek hold
flipped the geometry but not the paint. They now carry the same per-pose pair
cache as probes and slices.

- **Model**: `SurfaceDistanceOther` / `FocusDistOther` alongside the existing
  maps — main = committed displayed pose, Other = the opposite Before/After
  pose, fetched only once a solve exists.
- **Fetch** (`ensureVariance` / `ensureFocusDist`): one debounced batch fetches
  whatever is missing for both poses (committed first, so the visible paint
  lands sooner; variance aggregation refactored into a local `aggregate`).
  Transforms via `ModelTransforms.displayedWorldAt view`.
- **`SetRegView`** swaps both pairs wholesale (always correct — each cached
  array is pose-baked, the toggle just relabels which pose is committed) and
  cancels the in-flight CTSes + bumps both generations: a result landing after
  the swap would file under the wrong pose.
- **Peek** selects the Other cache at the two paint sites
  (`MeshView.inspectField`, `FocusScene.focusOverlay` mode 1) — flips together
  with `displayedMeshT` geometry in the 3D view and the focus tiles/single.
  Purely visual, no query. Peek is only reachable once solved (GuiTopBar gates
  the hold), so an empty Other never shows for an unsolved workspace; while an
  Other fetch is still in flight the mesh paints plain rather than stale.
- **Invalidation**: all existing triggers drop/keep the pairs together
  (`invalidateProbes`, `invalidateFocusDist`, `setMeshVisible`, workflow
  switch); `CoarseSolved` → `invalidateProbes` re-arms both poses after every
  re-solve. Displacement needs no pair — it is itself the Before→After delta.
- Docs: CLAUDE.md per-pose bullet. (GUI.md was retired in the same commit —
  the as-is GUI reference is no longer maintained.)

## Pin cross-section charts in the Overlays hold (2026-07-07)
While the Overlays hold is down, each pin's 2D name tag now carries a vertical
cross-section profile chart, and the centre cut is traced in 3D inside the pin.
Everything is precomputed + cached per pose so the hold stays a pure toggle.

- **Server** `/api/query/slice` (QueryHandlers.sliceHandler): all meshes × all
  parallel plane offsets in one request (per-mesh world transforms, probe
  convention), `Array.Parallel` over meshes. `MeshAnalysis.planeSlices` cuts
  the mesh with each plane (BVH sphere candidates fetched ONCE for all
  offsets), clips to the probe sphere in the chart frame (u²+v² ≤ r²−w², rim
  crossings interpolated, chains split), and returns flat (u,v) pairs.
  The marching/chaining core of contactRings was extracted into a shared
  `traceLevelSet (triBuf) (signedDist) (edgePoint)` + generic `decimate`;
  contactRings is now a thin wrapper (sphere field + exact sphere-root edge
  points, unchanged behaviour — live-verified |p−c| ≡ 2.0000 on a real mesh).
- **Slice frame** (ScanPin module): fixed world direction for every pin —
  cut plane spans world X (chart u) × world Z (chart v), parallel offsets
  along world Y at radius/4, three each side (7 planes). Helpers
  `sliceToWorld`/`sliceUV`/`sliceCentreIndex` keep chart↔world at one place.
- **Cache** mirrors the probes exactly: `ScanPin.Slice` (committed pose) /
  `SliceOther` (opposite pose, fetched once a solve exists); SliceState DU with
  the Running stale-guard; `ensureSlices` postlude clones the ensureProbe
  debounce (per-pin CTS, 250 ms, both poses fired off one token, 140 pts/plane
  cap); `invalidateProbes` drops slices with probes (same triggers: transforms,
  reference, radius via SetInnerRadius); `swapProbeViews` swaps ready pairs on
  SetRegView; the reg peek only SELECTS the other cache — no query reads it.
- **Label charts** (GuiOverlays.pinFlagLabels): the pill is now name row +
  SVG chart. Two attributes, one JS renderer keyed by pin id: `data-labels`
  (positions, every frame) only moves pills; `data-charts` (paths/dots/grid,
  changes only on slice arrival / pose display / visibility) rebuilds chart
  DOM. Paths are prebuilt in F# as SVG `d` strings in chart px (culture-safe
  sprintf — AppendFormat would emit comma decimals); x spans the pin diameter,
  y auto-fits BOTH poses' data (peek flips without rescale; deliberately
  exaggerated, ⌀/Δ footnotes state window + span); centre slice full-strength,
  parallel slices fade by |offset| (0.26/0.17/0.09), centre drawn last;
  correspondence anchors (ref + in-ROI moving, peek-aware displayed pose via
  the new `MeshView.displayedWorldPeekAt`) projected onto the centre plane as
  mesh-coloured dots; hairlines at u=0 and v=0.
- **3D centre cut** (ScanPinScene.pinSliceLines): the cached centre-plane
  polylines per shown mesh in its dataset colour, always-on-top (they lie
  exactly ON the whited-out surfaces — coplanar depth would stitch), gated on
  the hold, peek selects Slice/SliceOther. ScanPinScene's local dispWorldAt
  became `MeshView.displayedWorldPeekAt` (shared with the chart anchors).
- Docs: GUI.md §4.6 (chart + 3D cut paragraphs), CLAUDE.md state-rules bullet +
  endpoint list.

## Difference map: RdYlBu-style gradient + value isolines (2026-07-07)
The diverging difference map's grey centre vanished (against the white page and
the washed-out surface); replaced with an RdYlBu-style ramp and added
constant-value contour lines:

- **New gradient** (all consumers in sync — CPU colormap, 3D mesh shader enc 1,
  focus shader mode 1; legend/matrix/dots pick it up automatically): zero =
  light yellow #FFE78A, + → orange #F46D43 → dark red #A50026, − → steel blue
  #74ADD1 → dark blue #313695; per-sign normalization + t^0.6 boost kept.
  Grey (0.62,0.63,0.66) now exclusively means "no signal" (within-LoD matrix
  cells, no-data sentinel, dot fallback) — never "0".
- **Isolines**: dark contours at every k·step of the difference value, step =
  nice 1/2/5 ≈ (shared range span)/8 (so 0 is always a contour), computed
  client-side and passed as a uniform (3D `DiffIsoStep`, focus
  `FocusIsoStep`); shader draws them from the interpolated field with
  ddx/ddy-based antialiasing (~2.5 px), multiplicative darkening to 45 % at
  the core, suppressed where the colour clamps (t ≥ 1), and faded out where
  contours pack denser than ~2 px (steep/grazing fragments would smear into a
  dark blotch). NOTE: FShade has `ddx`/`ddy` but NOT `fwidth`.
- Docs: CLAUDE.md colour-map bullet rewritten + isoline rule; GUI.md §4.4.

## GUI.md — as-is GUI & interaction reference (2026-07-07)
New repo-root `GUI.md`: a complete, honest, code-free writeup of the GUI for
review — layout, interaction grammar, global state/holds, 3D view (picking,
ghosting, false-colour math, outlines, pins), the three workflow steps, rail /
focus panel / dock, probe/seed/solve data flow, end-to-end interaction chains,
input table, and an explicit limitations section (incl. the scale-bar
fov-axis defect spotted while writing: it treats the 90° fov as vertical but
the renderer applies it horizontally → lengths off by ~the aspect ratio).

## Fix: GPU picks landed ~2 units in front of the camera (2026-07-07)
Double-click recenter (and any GPU pixel pick in the main view) returned a
point near the image plane instead of the mesh. Cause: Aardvark.Dom picking
writes (id, gl_FragCoord.z) into a pick attachment from every **pickable**
node during the forward render — with blending forced OFF on that attachment,
so screen-alpha-0 fragments still stamp it. The image-space outline composite
(fullscreen quad, NDC z=0, DepthTest.None, no Sg.NoEvents) therefore
overwrote the whole pick buffer with depth 0.5 every frame; depth 0.5 passes
the `< 0.9999` background gate and unprojects to ~2 render units in front of
the camera (near=1). Broken since 47d0625 (2026-06-29) made outlines
always-on — the old `Sg.Active OutlineMode` (default off) had masked it.
Armed correspondence placement via 3D-surface click was equally affected
(false "hit" at depth 0.5 pre-empted the raycast fallback). Pin-dot taps
survived (pinScene draws after the composite), hover coords survived (server
raycast). Fix: `Sg.NoEvents` on the composite (OutlineView.fs) + a
load-bearing comment; CLAUDE.md picking rule added.

## Show-overlays hold: white-out + thick flags + 2D name tags (2026-07-07)
The hold-O / 🎨 Overlays modifier now spotlights the pins much harder:

- **Meshes paint plain white** while held (shading kept for relief): the
  `Greyscale` uniform/branch in `MeshShader.shade` became `Whiteout` and runs
  last, so every false-colour map (Inspect/heatmaps) is overridden too. Only
  the separately-rendered pin geometry carries colour.
- **Flag poles get much thicker** while held (line width 2.5 → 7.0, pole +
  tip ring, still pin-coloured; unchanged when not held). The pole-tip maths
  moved to shared helpers `ScanPin.flagMagnitude` / `ScanPin.flagTopRender`
  (ScanPinModel.fs) so geometry and labels agree.
- **2D name tags at the flag tips** (`GuiOverlays.pinFlagLabels`): DOM pills
  (glyph + ShortName, pin-coloured border/text on white) projected from
  `flagTopRender` through the same 90°-horizontal frustum as the main view,
  re-placed every frame via the `observedRender` JSON-attribute pattern;
  behind-camera / far-offscreen pins are dropped, overlap is accepted. The
  3D `Sg.Text` pin labels hide while held so names don't double up.
- CSS: `.pin-flag-labels` / `.pfl` in style.css (fixed, pointer-events none).

## 360° focus view replaces the pano unwrap (2026-07-06)
The focus panel's "Pano" projection is no longer a cylindrical full-unwrap; it
is now a standard rotate-able perspective view fixed at the mesh's panorama
centre (street-view style). Button label is now "360°".

- **Deleted** `FocusShaders.pano` (cylindrical vertex stage) + its
  `PanoEye/PanoCenter/PanoZoom/PanoAspect/PanoRadFar` uniforms; FocusShaders is
  now fragment-only (`focusColor`). Both projections render through the same
  `trafo → diffuseTexture → focusColor` pipeline — **no new shader**, so no
  in-browser shader-compile risk; the camera (view/proj) carries the difference.
- **Camera** (`FocusScene.panoCam`): eye = the existing per-mesh panorama
  centre (`pano-centers.txt` else mesh origin), Z-up `lookAt`,
  `Frustum.perspective` (same convention as the main view's 90° horizontal);
  fov = 90°/zoom clamped [4°, 120°]; near/far = fitExtent × 1e-3 / × 4.
- **Input**: drag = grab-the-world look-around (per-axis atan offsets so the
  point under the cursor tracks it; azimuth wraps, elevation clamps short of
  the poles); wheel = fov zoom anchored at the cursor. Plain left-drag now
  drags the camera in BOTH projections when no correspondence edit is armed
  (armed plain-left still places the point; middle / Shift+left always drag).
- **State**: the 360° view keeps its own per-mesh (azimuth, elevation, zoom)
  under a separate `camFor` key (`name + "†pano"`) — no more cross-talk with
  the Top pan/zoom semantics; `resetCam` clears both.
- **Pick**: `worldRayHit`'s pano branch is a standard perspective unproject
  (forward + right/up scaled by the half-fov tangents) — correspondence
  placement and the live aim ghost work in the 360° view as before.
- Unchanged/kept Top-only: the correspondence ring/glyph overlay, the gold
  reference outline, and the displacement arrows (all laid out in Top-view XY
  maths; the displacement channel still collapses 360° → Top).
- Docs: CLAUDE.md compile-order + panorama-centre lines updated.

## Dead-code prune (2026-07-05)
Repo-wide audit (4 parallel read-only sweeps + a mechanical whole-repo
identifier cross-reference, 1070 defs) then a full prune. User decisions:
prune everything including PWA, test-only math, and ProbeResult.Sources; keep
the camera files (OrbitController.fs / CameraModel.fs) untouched.

- **Study/PWA stragglers deleted**: `tools/study-integration.mjs` (targeted
  removed `/api/study/*`), `sw.js`, `manifest.json`, `icon-192/512.png`, the
  apple/PWA meta tags, the stale `/s/{token}` comment, and the inert
  `data-bs-theme`. index.html now unregisters any previously-installed service
  worker once; with no SW cache the `style.css?v=N` bump ritual is gone
  (the link is plain `style.css` again — server no-cache headers cover edits).
- **CSS**: 171 dead selectors removed (~590 lines): all `study-*`, panorama,
  fusion, retarget/cards, prov-hover, ruler, old left-panel/`lp-*` (except the
  live `.lp-sublabel`), old `.mesh-list`/`.pin-list` rows (`.mesh-swatch`/
  `.mesh-num` live on), CollapsibleSection `.cs*`, rail diagnostics/pin-list/
  flags, `.tb-dataset*`, correspondence-manager rows (`.ins-mgr` shell kept),
  `.pulse-outline`+`superPulse`, misc singletons. Verified by exact-token scan
  (scratchpad `csscheck.mjs`) honouring sprintf-built class prefixes — reruns
  report 0 dead classes.
- **Dead messages/handlers**: `ResetCamera`, `ReseedMesh` (+`reseedOneMesh` and
  the now-pointless `forceMeshes` param of `seedAnchorsCore`), `NavTo` +
  `NavAction` + `Diagnostic.Action` (rail diagnostics render severity+text
  only) + the `SuperPulse` JS bootstrap.
- **Write-only model state**: `RegistrationState.Running`,
  `Correspondence.RefDistance`/`Residuals` (so `CoarseSolved` and
  `AnchorsSeeded` payloads slimmed; `Query.lsqPairs` now returns only the
  transform — the server lsq response is unchanged and still tested),
  `ReadinessPin.Id`/`Label`/`Unresolved`, `ModelTransforms.solvedRender`,
  `WorkflowStep.all`/`mode`, `InspectChannel.label`, `AnchorSource.tag`/`ofTag`,
  two unused `refMeshA` locals.
- **Probe DTO slimmed end-to-end** (server compute → JSON → client decode →
  fields): dropped q1/q3/kde/bandwidth/footprint/intrinsics per distribution
  (incl. the whole KDE grid + `meshIntrinsics` server loops — real per-probe
  CPU savings) and planarity/length/autoLength/refOffset/xAuto/xFit/sources
  (incl. the three-source decomposition + `PerMeshSource`) on the result.
  Live surface is exactly: `normal` + per-mesh `name/count/median/std/
  samples/positions`.
- **Test-only math pruned with its tests**: `FlyToMath`/`FlyToTarget`,
  `RegConditioning.dominantAxis`/`isCollinear` (collinearity checks now assert
  on `lambdaRatio` directly), `RegMath.observabilityDeficiency`. Supertests
  43 → 29, all green.
- Verified: server + client (native off) builds, Supertests 29/29,
  `node tools/integration.mjs http://localhost:8003` 13/13 against a freshly
  built server (probe + lsq assertions unaffected), CSS token scan clean.
  Camera files untouched by request (their dead `AnimationKind` cases,
  `animationRunning`, 9 never-emitted `OrbitMessage` cases, `isOrtho`/`pick`/
  pan-shift branches remain — documented here for a future pass).

## Dock resize handle (2026-07-03)
- The bottom dock got a drag-to-resize handle on its top edge (`.dock-resize`
  in GuiInspector.dock) — the horizontal twin of the focus panel's
  `.focus-resize`, same pure-JS OnBoot pattern (pointer capture, no Elm state).
- One source of truth: the drag writes the `--dock-h` root CSS var
  (default `:root { --dock-h: 220px }`); consumers are `.pin-inspector`
  (height), `.render-control` (`calc(100% - var(--dock-h))` — so the 3D
  render control genuinely resizes, Aardvark tracks the canvas size), and the
  three bottom-anchored overlays (scale bar, orientation indicator, colour
  legend) at `calc(var(--dock-h) + 10px)`. No more hardcoded 220/230 pairs.
- Clamp: 120px … 60% of window height, evaluated at drag time. The chart
  re-renders during the drag via its ResizeObserver (added last round);
  style.css → ?v=4.

## M3C2 restricted to Z-overlap (2026-07-03)
- User report: in Inspect, the M3C2 difference map (moving mesh focused) and
  the disagreement/variance map (reference focused) extended into regions with
  no mesh overlap — unlike Δz, which only responds where the meshes overlap
  in Z. Fix is server-side only, in `region-distance` mode 0
  (QueryHandlers.fs): the closest-point M3C2 value is now gated by the exact
  same support test mode 1 uses — a vertical world ray from the vertex (down,
  then up, both transformed into the ref-local frame) must pierce the
  reference, else the 1e30 sentinel. The old approximation (reject closest
  points beyond 0.02 × ref-bbox diagonal, `regionMaxDistFrac`) is deleted —
  it both leaked fringe error (near-miss side regions within the cutoff) and
  could disagree with Δz's support in the other direction.
- The variance map needed no change: `ensureVariance` issues mode-0 queries
  (target = reference, ref = each moving mesh) and already skips sentinels per
  mesh per vertex (`abs v < 1e20`, cnt ≥ 2) — so a moving mesh now simply
  contributes nothing at reference vertices it doesn't Z-overlap.
- Verified live (scratchpad `check-overlap.mjs`, server on :8003 via
  `--no-launch-profile` — launchSettings pins `dotnet run` to :8002, which the
  user's own dev server holds): modes 0 and 1 sentinel exactly the same
  37831/40382 vertices on JOB_lowpoly2 0789 vs 0791; mode 0 costs 71 ms for
  40k verts (ray gate + closest point). Client untouched; shaders/legend
  already treat 1e30 as "no encoding".

## One-sided histogram + dock chrome trim (2026-07-03, after user review)
- Violin → one-sided stacked HISTOGRAM (user pick: the mirror halved the
  already-scarce height for zero information): crisp per-bin rects growing up
  from each lane's baseline, pin segments stacked in canonical order. With a
  solve, the inactive pose is a near-black step OUTLINE of its total (shape
  only, no colour/subdivision) over the filled emphasized pose; in-canvas
  caption "fill = after · outline = before" (flips with Peek). Same shared
  count scale across lanes and poses. Median ticks now sit on the baseline.
- Aggressive vertical reclaim, dock height UNCHANGED (220px): dock mode-label
  header row removed (all three modes gain ~22px; the rail names the mode);
  in-canvas title + hint lines removed (padT 26→14, padB 24→20); "Focus
  channel"/"Δ" labels dropped; pin legend chips moved into the single head row
  next to the channel toggles; axis meaning is a muted one-liner at the head
  row's right (`.ins-axis-note`). Chart canvas height ≈ doubled, per-lane data
  height ≈ 4× (no mirror + more canvas).
- Shift panel (`.ins-shift`, 188px) now mounts ONLY in the Displacement
  channel (the "shows in Displacement" stub note is gone) — the chart takes
  the full dock width in Difference. The canvas re-renders on size change via
  ResizeObserver (it only re-rendered on data mutations before — a resize
  would have left a stale-sized canvas).
- Brush-band value labels get a white halo (they now overlap bars). JSON:
  `state` field dropped (no in-canvas header consumes it). style.css → ?v=3
  (layout-affecting CSS change, per the stale-CSS rule).

## Violin distribution chart (2026-07-03)
- The dock's Inspect chart (`GuiInspector.brushChartJs`) is now a stacked
  violin per moving-mesh lane: 48 histogram bins over the shared mm x-range
  (1–99% quantiles over BOTH poses' full probe samples, span-0, 8% pad),
  binned in F# (`distData` ships per-pin `hb`/`ha` count arrays — small JSON,
  full samples never shipped); JS smooths the CUMULATIVE stack outlines
  (1-2-1) so layers never cross, stacks pin layers outward from the lane axis
  in the canonical pin order (CreatedAt, guid), strokes the total silhouette.
  One count→height scale across all lanes AND both halves (comparable areas).
- Before/After: no solve → mirrored halves (classic violin). Solved → fixed
  halves ▼ bottom = Before, ▲ top = After; the non-committed half renders
  muted (12% hue + 88% luminance grey, lifted 60% toward white); Peek flips
  ONLY the emphasis (visual, matching the 3D peek contract). Data: new
  `ScanPin.ProbeOther` = the same probe at the opposite pose (`ensureProbe`
  fires it only when solved, sharing the pin's CTS + debounce);
  `ModelTransforms.displayedRenderAt`/`displayedWorldAt` provide the pose.
  `SetRegView` now SWAPS a ready (Probe, ProbeOther) pair in place — the
  Before/After toggle no longer blanks the matrix/chart — and clears
  `BrushedSamples` (gids re-index); unpaired states invalidate both.
- Brushing unchanged by design: the conceptual samples (canonical
  `brushSamples` gid array, committed pose) still populate `el._dots`; the
  x-range drag, 50 ms-throttled bridge emit, click-clear and `data-brushed`
  echo work as before — the dots are simply not painted. New: brushed count
  next to the range label; a model-side clear drops the local band; without a
  local drag the band is reconstructed from the echoed gids.
- Verified headlessly: typecheck build green, Supertests 43/43, and a node
  harness (scratchpad `check-violin.js` + `smoke-violin.js`) that extracts the
  inline JS from the .fs source, parse-checks it, renders pending/reg=0/reg=1
  against a stubbed canvas, simulates a brush drag (partial gid emit
  verified), click-clear, and echo-band reconstruction.

## Legend placement report (2026-07-03) — stale-CSS root cause
- Headless-browser measurement (real click into Inspect): the legend renders
  exactly as specced — horizontally centred (translateX(-50%) applied), bottom
  edge at viewport−230px = the scale bar's bottom edge, 10px above the dock
  (which is fixed height 220px). The reported "in-flow bottom-left, behind the
  dock" is precisely how the element renders when `.color-legend` CSS is
  missing → the browser had a STALE CACHED style.css.
- Fix: Superserver now serves the hand-edited shell assets (css/html/js incl.
  the index fallback) with `Cache-Control: no-cache` (ETag revalidation each
  load, cheap 304s), so CSS edits always reach the browser.
- Second report (legend below the scale bar, behind the dock) = the same
  missing-rule rendering; the no-cache header can't evict an ALREADY-cached
  copy. index.html now links `style.css?v=2` — a changed URL bypasses both the
  disk cache and the service-worker cache with no user action. Bump `?v=` if a
  stale stylesheet would ever break layout again. Verified end-to-end
  (headless browser, Inspect mode): legend centred, bottom edge =
  viewport−230px = scale-bar bottom edge, 10px above the 220px dock.

## Inspect colours + samples (2026-07-03)
- **One error range** (`ScanPin.inspectRange` + adaptive `MeshView.inspectRange`):
  signed (lo, hi) metres over every ready pin's ROI probe samples on moving
  meshes, spanning 0, hard-capped ±0.5 m; no pins ⇒ ±0.5 m. Drives the 3D
  difference painter (new `DistLoNeg` uniform, asymmetric piecewise — neutral
  welded to 0, each sign normalized by its own end), the variance σ map
  (saturates at max(|lo|, hi)) and the focus tiles/single (`FocusLoNeg`).
  Per-mesh robust-percentile normalization (`robustHi`) and the gear
  "Difference heatmap range" slider (`DiffRangeScale` model field + message)
  are GONE — ranges must stay comparable.
- **Displacement range** (`MeshView.displacementRange`): global max
  |load→solved| over each solved mesh's world-bbox corners (rigid ⇒ exact at a
  corner), uncapped — displacement is not an error metric.
- **Legend** (`GuiOverlays.colorLegend`, bottom centre, Inspect only): active
  map's gradient (variance σ / difference M3C2 or Δz / displacement) as an SVG
  via observedRender — nice-step ticks + exact range ends; mm/cm/m formatting
  by span.
- **Sample dots**: brushed samples render as small solid icosphere dots
  (`Dots.render` in LineShader.fs — vertex-coloured triangle batches), coloured
  by the sample's value on the diverging gradient normalized to its own pin's
  range (`ScanPin.pinErrorRange`) — replaced the pin-coloured crosses.
- **Hover-reveal removed**: the 3D surface-hover → chart brush (View.fs) and
  the hovered-pin ROI sample cloud (`sampleBrush`) are gone with their
  throttles; chart X-range drag is the ONLY brushing path.
- Docs: CLAUDE.md "One Inspect error range" contract section; README workflow
  bullets updated (select vs double-click zoom, one range + legend, chart-only
  brushing).

## Select ≠ zoom separation (2026-07-03)
Single click/tap = selection + visibility only; double click/tap = camera zoom.
- **Reducer**: `SetFocusedMesh`, `SelectPin`, `FrameCorrespondence` no longer emit
  `FlyToPoint`. New `ZoomToMesh` / `ZoomToPin` messages own the 3D framing radius
  conventions (bbox ×0.6 / pin ROI ×4). Dead `FlyTo(target, aspect)` message +
  handler removed (no emitter existed).
- **Double-click sites** (each: emit select + Zoom* and call the 2D FocusScene
  helper): Overview roster row, matrix column head (mesh → `ZoomToMesh` +
  `resetCam` fit), matrix pin row-head (`ZoomToPin` + `zoomOnPin`), matrix cell
  (re-`FrameCorrespondence` + `FlyToPoint` on the anchor + `zoomOnWorldRadius`;
  no-marker cells fall back to mesh framing), focus tile (`ZoomToMesh` +
  `resetCam`), 3D pin dot (`Sg.OnDoubleTap` → select + `ZoomToPin`; 2D zoom
  unreachable there — compile order, matrix pin row is the linked path).
- **ClickGate** (Primitives.fs): defer-and-cancel discriminator used ONLY by the
  two toggle-on-single-click controls (matrix cell locate/back-out, 3D pin dot
  select/deselect) — the `tap`/`click` events fire on both leading clicks of a
  double, which would toggle twice. Double handlers are written to END in
  "selected + zoomed" regardless, so slow double-clicks stay correct.
- `FocusScene.onMeshFocused` (Inspect auto-tight-fit on select) removed — it was
  an automatic zoom on selection, exactly what this pass separates. Mesh
  double-click now resets that mesh's 2D camera to fit in every mode.
- 3D viewport: mesh tap = select only (dropped its 2D zoom side-effect);
  double-tap keeps the existing recenter-on-point behaviour.
- CLAUDE.md grammar line extended (double-click = zoom, ClickGate rule).
- Verified: typecheck build green, Supertests 43/43.

## Polish round 2 (2026-07-02)
- **Reference-marker parity**: the reference's correspondence point (RefAnchor) now
  draws exactly like a moving-mesh marker — same wire-sphere+cross glyph/size in the
  reference mesh's colour in the 3D constellation (hover-linked via its matrix cell),
  and shown in the focus Top overlay when the single shows the reference. Ref-column
  matrix cells now locate (FrameCorrespondence/selectCell read RefAnchor). Pick/place
  already worked (✎ edit point + PickCorrespondenceAt isRef branch) — verified.
- **Distribution chart**: X-axis ruler (nice-step ticks, labels, faint gridlines,
  zero line, axis baseline); brushing reworked from dot-lasso to an X-RANGE drag —
  selects every sample in the range, band edges show exact mm values, click clears.
- **Pin visuals**: centre jack now small + faint neutral (was bright yellow when
  selected / red otherwise); equator + contact rings + focus ROI circles use the
  pin's own colour (was host-mesh colour falling back to fixed blue / red-gold in
  the focus); verdict flag pole is neutral grey (red/green semantics dropped) and
  takes the pin colour while the 🎨 Overlays hold is down.
- **Focus head slimmed**: ⤺ back, ⇄ link, ⟲ reset buttons removed with their code —
  `Model.LinkViews` + `ToggleLinkViews` + both link-views camera paths +
  `FocusScene.recenterOnWorld` + dead CSS (incl. stale `.focus-peek`). Locate
  back-out remains via re-clicking the located matrix cell (BackOutLocate kept).

## Docs cleanup (2026-07-02)
- CLAUDE.md rewritten: kept only rules/pitfalls (state rules, render-pipeline
  contracts, coordinate discipline, adaptive-performance rules, query-perf rules,
  compile order, API reference, Aardvark.Dom gotchas, fsproj/CSS notes); dropped
  the feature-behaviour catalogue (ghosting §-tables, Inspect channels, camera
  controls, GUI placement, Model snapshot — all readable from code). ~350 → ~200 lines.
- README rewritten: fixed stale claims (readiness lives in the Correspondence rail,
  not the top bar; intrinsic heatmaps are Overview-roster per-mesh switches, not in
  the dock; focus-single pan is middle/Shift+left, not plain left; ghost floor is in
  the gear, not a 👻 toggle), documented pano-centers.txt in the dataset layout, and
  dropped the duplicated architecture/pipeline/perf sections (now CLAUDE.md-only).

## Adjustments after user testing (2026-07-02)
- Inspect mesh focus isolates **only the moving mesh** — the reference is no longer
  part of the solo shown-set (a co-located reference occluded the field);
  `MeshVisibility.shown` simplified (step/ref params dropped at all call sites).
- Inspect **matrix-cell locate additionally activates pin isolation**
  (`FrameCorrespondence` sets `AnchorGhostMode` in Inspect, like a pin click);
  Correspondence keeps its default.

## Done
- **Solo redesign** — `MeshSolo : string option`, a pure overlay over `MeshVisible`
  (no destructive mutation, no restore map). One shared rule `MeshVisibility.shown`
  (Model.fs) consumed by render `MeshActive`, 3D raycast candidate sets, Alt-wheel
  cycling, contact-ring + constellation gating. In Inspect the shown-set is
  {isolated mesh, reference}.
- **◐ isolate lifecycle** — re-click exits; `UpdateHelpers.exitSolo` resets every
  visibility toggle to ON; tile vis buttons disabled during isolation (+ reducer
  guard on `SetVisible`); `SetWorkflowStep` ends isolation, drops `LocateBackup`,
  clears `Selection.Hovered`.
- **Inspect policies (reducer-owned)** — `SetFocusedMesh (Some m≠ref)` → auto-solo
  {m, ref} + pin isolation off (focusing ref returns to the ensemble);
  `SelectPin Some` → exit mesh isolation + pin isolation on (`None`/delete → back
  to off). Reference renders as plain solid context during solo (variance encoding
  gated to no-solo); the old "empty outline for others" special case removed —
  others go to the regular ghost floor.
- **Linked cameras** — new FocusScene helpers: `onMeshFocused` (Inspect: tight fit
  of the focused mesh; called from tile / matrix column / 3D click / cell-no-anchor),
  `zoomOnPin` (rail pin-row click: focus panel keeps its mesh, zooms onto the pin,
  same metric half-extent as the 3D `FlyToPoint`), `zoomOnWorldRadius` (matrix cell
  locate: focus switches mesh + zooms onto the correspondence coordinate, replacing
  the fixed ×4 zoom).
- **Matrix cell toggle-off fix** — the un-locate branch now requires an active
  locate (`LocateBackup` present); a pin-row + mesh-column selection no longer
  swallows the first cell click.
- **Stale-cache fixes** — `setMeshVisible` invalidates the variance map, bumps the
  focus-dist generation (newly shown meshes fetch their missing difference fields —
  previously never fetched), and clears `BrushedSamples` (gids index a
  visibility-dependent array). `ensureVariance` skips during solo; `ensureFocusDist`
  fetches the shown set (isolated mesh even if its raw toggle is off).
- **Focused-mesh resolution unified** — `GuiFocus.visibleMeshes` no longer diverges
  from `FocusScene.single` (restore-set branch removed); focusing/locating a hidden
  mesh re-enables its toggle so the single always shows the focused mesh.
- **Dead state removed** — `Selection.SelectedPoint` + `SetSelectedPoint` message
  (write-only), `LocateState.Pin/Mesh` fields, `MeshSoloState` DU.
- **Docs synced** — CLAUDE.md (§A visibility model, §C per-mode table, Inspect
  visualizations, locate, selection-camera sync, model snapshot) + README
  (tiles/isolation lifecycle, Inspect behaviour).
- **Verified** — adaptify regenerated, client typecheck build green
  (`-p:WasmBuildNative=false`), Supertests 43/43 pass.

## Follow-ups / open questions
- Shader verification status: `MeshShader.shade` (+`DistLoNeg`) and the focus
  shaders compiled clean in a real headless-Chromium WebGL2 run (puppeteer,
  `?nocache=true`); the run can't reach `#loading-done` in THIS environment —
  an offscreen framebuffer fails identically on the unmodified HEAD build
  (environmental, headless GL). Still to eyeball live: the new `Dots` shader
  (first brushed selection), legend SVG (all three map kinds), dot
  size/readability, and variance-map contrast on the pin-derived scale (σ is
  usually smaller than the error envelope — if too pale, consider a separate
  σ range).
- `ShaderCache.fs` no longer covers the changed mesh/focus shaders (new
  hashes) — first load live-compiles them (few seconds). Re-run
  `./precompileShaders.sh` on a machine where headless WebGL works to re-bake.
- 3D pin-dot double-tap zooms 3D only — the 2D focus canvas is unreachable from
  ScanPinScene (compiles before FocusScene); the rail pin-row is the fully
  linked path per spec.
- In-browser verification of the select/zoom split pending: single-click cell
  select now lands after the 350 ms ClickGate window — check it doesn't feel
  laggy; check double-click on roster/tiles/cells zooms without visible
  toggle flicker.
- In-browser verification of the full Inspect flow still pending (shader paths
  unchanged, but ghost-floor appearance of "others" during Inspect solo is a
  visual change worth eyeballing).
