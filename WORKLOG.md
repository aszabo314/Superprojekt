# Worklog

> **Standing rules for this rework (2026-07-27):** documentation files
> (CLAUDE.md, README.md) are FROZEN until the entire rework is finished —
> do not edit them per-change. Log every change here as it lands; at the
> end, reconstruct the net documentation updates from this file.

## v14 documentation reconstruction (2026-07-27) — FREEZE LIFTED

README.md + CLAUDE.md rewritten from this log; every per-phase DOC DEBT item
resolved.

- **README**: "What you can do" rebuilt around the v14 shape — navigator
  hierarchy (Setup survey/root ★ + pair matrix ⇄ cell workspace), atomic pin
  transaction, pairwise solve into the registration tree, loop dialog,
  in-cell chart/brush/probe/error map, V/B peek keys, Space-hold clean view,
  near-plane Cut. Server blurb now names the pairwise error measure.
- **CLAUDE.md**: state rules rewritten (registration graph + invariants/
  hazards, navigation + ONE Esc chain, atomic pins, in-cell caches, peeks);
  render section updated (passTwo, near cut, in-cell error range, ONE chart,
  legend two-state, suitability trigger, OutlineWidthPx, OutlineMask now
  all-on); coordinate rules updated to composed poses + own-frame pin
  geometry; pano-centre consumers corrected; API list + compile orders
  regenerated from the fsprojs; NEW "F# pitfalls" section (deleted-DU-case
  catch-all patterns, Trafo3d struct equality, apply-a-first composition,
  Result shadowing). Durable sections (comment discipline, DepthMask ban,
  outline-pass rationale, adaptive-perf rules, Dom/FShade gotchas) kept.
- Removed with the rewrite: every v13 `(TODO: …)` marker, the three-mode
  rail/selection/focus-panel/dock/slice-cell/probe-star/Before-After
  descriptions, `/query/probe|slice` + MeshProbe/ProbeModel/FocusScene/
  FocusShaders/GuiFocus/GuiInspector references.
- Final state: client + server builds green, Supertests 102/102, integration
  29/29. Outstanding: the ONE whole-app browser pass (per-phase owed items
  above, incl. the OutlineGBuffer bias-removal shader edit).

## v14 dead-code elimination pass (2026-07-27)

Post-P9 cull of everything the rework orphaned. Client + server builds green,
Supertests 102/102 (−3 dip tests), integration 29/29 on a fresh :8002.

**Server:**

- `/api/query/slice` DELETED (route + `sliceHandler` + `SliceMeshDto`/
  `SliceRequest`) — the parked P3 decision resolved as prune: no client
  consumer since the slice-cell matrix died, integration coverage alone
  doesn't justify the endpoint.
- `MeshAnalysis.fs` → only `contactRings` (`dipAzimuth` + `planeSlices`
  deleted); `MeshAnalysisCore.fs` lost `dipFromMoments`/`dipOfPoints`
  (slice-azimuth fitting machinery — `traceLevelSet` + `decimate` stay,
  contact rings use them).
- `region-distance` mode 1 (vertical Δz) deleted: `RegionDistanceRequest.Mode`
  field + the Δz branch — the endpoint has exactly ONE metric again (M3C2
  closest-point with the Z-overlap support gate).
- `bboxes` no longer computes/returns `spacing` (mean edge length) — its only
  consumer was the slice-cell window; the edge-sampling loop went with it.
  Handler is now purely bbox + cache warmer.

**Client:**

- `Model.DebugLog` + the gear-popover log display (GuiTopBar), `.tb-gear-log`
  CSS. `ModelTransforms.edgeRender`, `ScanPin.pointOn`,
  `ScanPinUpdate.activeScale`, `Query.closestPoint` wrapper.
- `Query.regionDistance` no longer sends `"mode":0`; `fetchBboxes` +
  `SceneBoundsLoaded` payload narrowed to `(string * Box3d)[]`.
- `MeshView.buildReferenceOutlineNode` (reference gold outline — no reference
  concept in v14), `OutlineDepthBias` uniform + its shader term
  (`OutlineGBuffer.shade` writes plain `v.fc.Z`), `OutlineView.maskAllOn`.
- `LineShader.LineGlyphs`: `addDashedRing`/`addDashedRingXY`/`addArrow`/
  `addArrowXY`/`addCrossXY` deleted (kept: duplex, basisFromNormal, addRing/XY,
  addWireSphere, addCross, addBoxOutline).
- `Primitives`: `numberedFriendly`, `pinInkCss`, `showWhenNot`, mesh-level
  `shortName` (the `friendlyName` fallback now uses `meshLocal`).
- CSS orphan sweep: `.tb-gear-log`, `.placement-tooltip`,
  `@keyframes corr-flash-pop` removed; compound-selector orphans the
  class-token sweep can't see caught by hand (`.focus-set.btn-active`,
  `.tb-regview-btn/.tb-regview-peek`, `.rail-btn-primary`); `--dock-h`
  (permanent 0 since P5) dissolved into plain offsets; stale rail/roster
  comments + a §-spec-ref comment fixed.
- **Latent bug found + fixed**: the gear popover still had an "Isolate pins"
  copy whose gate matched the P6-deleted `AnchorPlacement` case — F# silently
  reparsed it as a catch-all VARIABLE pattern (warning FS0049 only), so
  `placing` was constantly true and the toggle permanently inert. Deleted (the
  cell workspace hosts the real toggle). Full-rebuild warning tally now clean
  of FS0049/FS0025/FS0026 — NEW PITFALL for CLAUDE.md: deleting a DU case
  turns its remaining patterns into catch-alls, and only warnings betray it.

**Tests:** Supertests dip-fit trio deleted (dipOfPoints gone);
`tools/integration.mjs` slice §5 + Δz lift test deleted, sections renumbered,
the `lift` matrix moved to the pair-error-at section (its other consumer).

Browser verification still owed (with the per-phase items): the
`OutlineGBuffer` shader edit renders correctly (bias removal).

## v14 P9 — loops + forced path resolution (2026-07-27) — FINAL PHASE

Spec: `ScanPin_v14_P9_loops_path_resolution.md`.

- **T1 invariant change**: `RegGraph.tryAddEdge` now returns `EdgeAddResult` —
  `EdgeAdded` / `EdgeClosesLoop (cycleEdges, residual)` / `EdgeRejected`.
  Both-endpoints-connected is TRANSIENTLY accepted (exactly one fundamental
  cycle), never committed; disconnect-adds stay impossible. New pure
  machinery: `treePath` (directed a→b steps via LCA, up = Transform, down =
  Inverse), `loopResidual` (tNew ∘ path(ref→mov) — identity iff the paths
  agree), `residualRotationDeg` + `residualAt` (displacement at a probe
  point — rigid conjugation preserves translation length, so an injected 5 cm
  reads 5 cm exactly, test-pinned). `SolvePair` no longer refuses redundant
  pairs (orientation from `pairRefMov`); `PairSolved` distinguishes a
  same-pair re-solve (parent check — `Map.containsKey` alone would misroute
  a redundant child that keys an edge elsewhere) from a loop-closing add.
- **T2 the blocking modal** (`GuiOverlays.loopModal`, `.modal-scrim` z-300):
  "Two paths now connect these meshes" + "These paths disagree by X.X° and
  Y cm" (displacement read at the MOV mesh's centroid); rows = the NEW edge
  + every cycle edge, each with its single-scalar quality, WEAKEST
  pre-selected; pick exactly one to remove; Confirm → `ConfirmLoopResolution`
  (remove + recompose + invalidate cell caches + `bumpPairSolve`), Cancel/Esc
  → `CancelLoopResolution` (redundant edge discarded, prior tree stands).
  Esc chain: the modal now WINS (modal > transaction > point-edit > probe >
  ascend); peek keys refuse while it is open; dataset switch clears it. State
  = `Model.LoopPending` (`LoopPending` record in RegistrationModel; the
  committed graph never holds the loop). No 3D choreography, no standing
  overlay.
- **T3 tree restored**: `RegGraph.resolveLoop (mov, ref, tNew, q, removeChild)`
  — removes ONE cycle edge; the detached subtree (containing exactly one
  endpoint — the cycle crossed the removed edge once) re-hangs through the
  new edge, its internal path reversing like a reroot; the new edge INVERTS
  when the REF side detaches. Result proven in tests: spanning tree over the
  same members, every kept edge constraint pose(c) = T∘pose(p) holds (unique
  poses), an exactly-agreeing loop resolves with poses unchanged, MOV-edge
  and REF-side removals both correct.
- Supertests 105/105 (+12 loop/resolve; all old cycle-rejection tests updated
  to the transient semantics). Client + server builds green. Browser pass
  owed: modal look/flow on a real redundant solve.

**v14 phases complete (P0–P9).** Outstanding before doc reconstruction:
ONE whole-app browser pass (owed items listed per phase above) + the parked
decision on the server `/query/slice` endpoint (client-consumer-less since
P3; kept for integration coverage — prune or re-consume).

## v14 P8 — peek system, blink comparator (2026-07-27)

Spec: `ScanPin_v14_P8_peek_system.md`.

- **T1**: already clean — the ensemble Peek button + hotkey I fell in P2
  (grep-verified; only stale comments remained, fixed).
- **T2 two spring-loaded keys**, cell scope only, zero config, REF/MOV from
  the tree (`MatrixNav.pairRefMov`, nearer-root = REF), "before" = as-loaded
  always: **V** (visibility) — the MOV blinks OFF outright while held, the
  REF alone answers "same rock?" (design call: hidden means Sg.Active off —
  never the ghost floor, a blink needs a clean swap; pin annotations stay);
  **B** (pose) — the MOV displays AS-LOADED instead of composed while held,
  REF static ("did registration help?"). `Model.PeekVis`/`PeekPose` +
  `SetPeekVis`/`SetPeekPose` from view key down/up (reducer idempotence
  absorbs key repeat); presses REFUSED unless in a cell AND both pair meshes
  are resident (`MeshesLoaded`) — releases always land; peeks clear on
  descend/ascend/dataset.
- **T3 perceptual constraints**: instant swap — vis peek = Sg.Active flip on
  the main surface + outline G-buffer + footprint coverage + suitability
  nodes (`MeshView.peekVisHiddenAt` in all four actives — no silhouette or
  footprint remnant); pose peek = a trafo change through the ONE
  `displayedMeshT`/`displayedWorldAt` pair (now `peekMovAt`-aware, so
  surface-riding pin markers follow the blink; the offscreen outline pass
  shares displayedMeshT and follows automatically). Zero refetch: the error
  map rides MOV's surface during the pose peek (registered-pose values,
  purely visual — documented). Both states GPU-resident by construction
  (same geometry; residency gated at the press). No camera writes, no UI
  reads the peek state (zero reflow), whole meshes swap, wheel stays orbit
  zoom, no indicator control (eyes-free), no auto-blink/cycling/config.
  Reducer-side queries keep the COMMITTED pose (`ModelTransforms` untouched
  by peeks).
- Supertests 93/93; client + server builds green. Browser pass owed: blink
  feel (V), pose jump (B), residency refusal on a cold pair.

## v14 P7 — in-cell error inspection (2026-07-27)

Spec: `ScanPin_v14_P7_incell_error_inspection.md`. Consumes P0's
`/query/pair-error` + `/query/pair-error-at` + the kept `/query/region-distance`.

- **T1**: already-clean verification — the two-diagram design, slice/stretch/
  ordinate/dot-glyph machinery and the M3C2|Δz toggle all fell in P3/P5
  (grep-verified zero remnants; the client hardcodes region-distance mode 0,
  no metric UI exists — the measure stays opaque).
- **T2 the ONE diagram** (cell workspace, `GuiRail.chartJs` + `chartData`):
  the MOV mesh's error across the pair's pins, pin-source-STACKED (48-bin
  histogram, achromatic pin ramp, per-pin median ticks, mean-LoD band, mm
  axis), titled "Mesh N error vs M — across pins"; per-edge before/after diff
  = fill (current poses) + near-black step outline (edge-BEFORE poses via
  P2's `composeEdge`) with a "fill now · line before" key — registered pairs
  only. Placeholder furniture when pinless. Data: `Model.CellError`/
  `CellErrorBefore` per-pin `Query.pairError` batches at displayed poses
  (before-batch pin ROIs follow the anchor mesh's edge-before pose), samples
  stored MOV-relative-to-REF (sign flipped at landing when MOV = meshA);
  REF/MOV from new `MatrixNav.pairRefMov` (edge parent, else hop depth,
  unconnected = MOV, tie → key order). Lazy single-flight postludes
  (`ensureCellError`/`ensureCellDist`), ONE `cellErrorGen` guard bumped by
  `invalidateCellError` on descend/ascend/pin edits/solve/edge-drop/reroot/
  dataset.
- **T3**: diagram x-drag brush → `SetBrushedSamples` gid set (≤200; gid =
  index into the canonical CellError sample concatenation) → white ink-under-
  stroked 3D glyphs (`brushedSampleNode`); bidirectional: 3D hover (screen-
  space nearest ≤12 px, 80 ms throttle) → `SetHoverSample` → amber diagram
  cross-highlight + amber 3D glyph + exact value via pair-error-at into the
  workspace readout. Armed probe: ⊕ toggle by the map toggle; while armed any
  3D pick → pair-error-at → "probe ±N mm" readout — fully transient
  (`ProbeReadout` wiped on disarm/ascend; no persistence, no diagram link).
  False-colour map: `CellDist` = region-distance MOV vs REF at displayed
  poses, painted on MOV ONLY (never the reference; `InspectPlain` swaps only
  MOV's base to near-white), toggleable ("Error map"), suppressed by a
  non-empty brush (brush = sole focus, established grammar); range = new
  `ErrorRange.ofSamples` (pin-ROI samples, span-0, cap ±0.5 m) shared by map
  uniforms + diagram + legend; legend gains the diverging branch back
  ("Difference N vs M"). NO isolation in inspect: both pair meshes render
  as-is (the P5 nav rule is scope, not isolation). Esc chain: transaction >
  point-edit > probe disarm > ascend.
- Supertests 93/93 (+4 pairRefMov). Client + server builds green. Browser
  pass owed: diagram rendering/brush feel, glyph highlight, readouts, map on
  MOV with legend, before/after outline after a solve.

## v14 P6 — atomic pin placement, in-cell (2026-07-27)

Spec: `ScanPin_v14_P6_atomic_pin_placement.md`.

- **T1 DELETED**: `Correspondence`/`MeshAnchor`/`AnchorSource` types + the
  `ScanPin.Correspondence` field; auto-seeding wholesale (`seedAnchorsCore`/
  `seedAnchors`, `AnchorsSeeded`/`AnchorSeedFailed`, `updateCorr`/`allPinIds`,
  SetRegRoot's re-seed); ROI machinery (`roiReach`+`fixedProbeLength`, InRoi);
  the whole star readiness/solve-filtering stack (`Readiness`, `ReadinessPin`/
  `ReadinessInput`/`Diagnostic`/`Severity`, `RegConditioning` — server RegMath
  keeps its own conditioning) + their Supertests sections; the old
  `PlacementState.AnchorPlacement` click-place flow with its ≥2-overlap
  hard-prohibit (`countOverlap`, `placementValid`, ghost fade,
  `placementTooltip`) — atomic placement picks points explicitly, so the
  prohibition is meaningless.
- **T2 atomic pin + transaction**: `ScanPin` = { Pair (PairCell.key order),
  AnchorMesh + CentreLocal (the pin RIDES its placement mesh), InnerRadius,
  PointA + PointB (own-frame, NON-optional — no partial pin exists),
  ShortName, CreatedAt, ContactRings }. `PlacementState.PlacementActive of
  DraftTool × PinDraft` — modal, FREE ORDER: ◯ Area / ✚ Points sub-tools
  re-armable, clicks route by tool (area = raycast whatever pair mesh is
  under the cursor → anchor there; point = the HIT MESH attributes the slot,
  re-pick replaces), "area ✓ · N of 2 points" cue lives only in the draft
  bar, ✓ Commit enabled only complete (the one birth path), ✕/Esc abort =
  full rollback (the draft never touches the pin map). Points are
  UNCONSTRAINED (outside-ROI legal; ROI scopes analysis only). Draft renders
  all-white (uncommitted layer): area wire sphere + point wire-sphere/cross;
  flashlight ghost only while ToolArea aims.
- **T3 anchoring/solve/edit**: pin world centre = displayedWorld(AnchorMesh)
  ∘ CentreLocal everywhere (scene nodes token-read poses; blobs/rings/zoom/
  flags updated; rings now fetch the pair's two meshes only). `SolvePair`
  (cell toolkit ⌖ button, enabled ≥3 pair pins): orientation = existing edge
  (re-solve) else un-treed mesh MOV → treed REF; both-in-tree ⇒ cycle refusal
  toast, neither ⇒ connect-to-root-first toast. Pairs feed `/query/lsq-pairs`
  at the AS-LOADED baselines (edge transform = child-baseline onto
  parent-baseline, P1 convention — ancestor registration composes on top);
  `Query.lsqPairs` re-added returning transform + residuals; landing
  (`PairSolved`, `pairSolveGen`-guarded) → `tryAddEdge`/`withEdge` with
  quality = `RegGraph.solveQuality` (1/(1+rms/0.05), halves at 5 cm rms) +
  recompose. ANY pin edit (radius/point re-pick/delete) with a registered
  pair → `RegGraph.removeEdgeCascading` (the edge + its whole subtree — a
  stranded component would break the invariant) + recompose + toast + gen
  bump (in-flight solves land dead). Edit mode = `PinEditState.EditPoint`
  (pin row ·N buttons arm; single-mesh raycast replaces the point atomically;
  never partial). Committed markers = MESH-COLOURED FILL + WHITE OUTLINE
  (mini icosphere + white wire sphere) on both pair meshes. ONE Esc chain:
  transaction abort > point-edit disarm > ascend.
- Cell workspace: New pin / ⌖ Solve (n/3) / Isolate hide during a
  transaction; pin rows = name · r log-slider · point re-pick per mesh · ✕.
- Supertests 89/89 (+8: leaf/mid-tree cascade + reference-equal branch,
  withEdge payload, quality bounds/monotonicity/calibration). Client+server
  builds green. Browser pass owed: full transaction flow, draft visuals,
  marker attribution colours, solve round-trip, edit invalidation toast.
- DOC DEBT (frozen): CLAUDE.md correspondence/seeding/readiness bullets,
  placement suitability hard-prohibit, pin-anchor conventions — superseded.

## v14 P5 — descend/ascend hierarchy; rail + global selection deleted (2026-07-27)

Spec: `ScanPin_v14_P5_hierarchy_descend_ascend.md`. The navigation rework —
the largest P-phase deletion. The app is now: 3D view + top bar + the left
navigator (matrix-home ⇄ cell-workspace) + overlays. The remaining pair tools
land P6–P8.

- **T1 DELETED wholesale** (with every consumer branch):
  - `WorkflowStep` (type/module/field/message; all per-mode branching:
    Inspect gates in MeshView/legend, Correspondence gates for isolation +
    constellation, per-mode isolation defaults in SetWorkflowStep).
  - The GLOBAL SELECTION model: `ActiveSelection`/`Selection`/`HoverTarget`,
    `SetSelection`/`SetHovered`, every panel's selection consequence, hover
    peeks (`wheelIsolation`), 3D pin-dot select taps.
  - `MeshSolo` + `enterSolo`/`exitSolo` + `LocateState`/`LocateBackup`/
    `BackOutLocate` (the locate machinery).
  - Whole FILES: `GuiFocus.fs`, `FocusScene.fs`, `FocusShaders.fs` (the
    selection-framed focus panel), `GuiInspector.fs` (the dock: charts,
    manager, XYZ editor, shift readout, brush bridge), `ProbeModel.fs`.
  - With their only drivers gone: sample BRUSHING (`BrushedSamples`,
    `SetBrushedSamples`, `ScanPinScene.brushSamples` + glyph builders,
    `BrushDotPx` gear), the exact-point probe (`PointProbe`, `probeValueAt`),
    correspondence ARMING/PICKING (`CorrArm`/`CorrPreview`/`ToggleCorrArm`/
    `CorrPreviewComputed`/`PickCorrespondenceAt`/`CorrFlash` + the 3D aim
    ghost/arrow + flash overlay + `raycastMesh`), the client PIN-PROBE layer
    (`ScanPin.Probe`, `ProbeState`/`ProbeResult` DTOs, `ensureProbe`,
    `Query.probe` — dead against the P0-deleted endpoint anyway; `ScanPin.axis`
    → `axisWith` now globalUp-or-world-up), the Inspect difference stack
    client-side (`FocusDist`, `ensureFocusDist`, `FocusDistComputed`,
    `Query.regionDistance`... KEPT `Query.regionDistance`? NO — wait, see
    below), `MeshView.inspectRange`/`inspectField` + per-mesh outline gating
    (`outlineFlagAt` → mask all-on, depth bias 0), pin messages without hosts
    (`SetInnerRadius`, `DeletePin`, `ProbeComputed/Failed`), the orphaned
    query wrappers `Query.lsqPairs` (dead since P1; P6 re-adds) and
    `Query.regionDistance` (its ensureFocusDist caller went with the Inspect
    stack; P8 re-adds), `ClickGate` + `ReadinessView` adapter (Readiness
    ENGINE kept — tested, P6-bound), the selection circle + constellation +
    hover pulses in ScanPinScene, `SceneGraph.focusedOutline`, the top-bar
    focused-mesh coordinate branch, `FocusScene.dpr` publisher.
  - Legend reworked to Range-heatmap-only; ~106 orphaned CSS blocks pruned
    (.ins-*, .focus-*, .rail-step*, .mx remnants, tb-regview, dock resize...);
    `--dock-h` → 0 (dock gone; bottom overlays reach the bottom edge).
- **T2 hierarchy**: `NavLevel = NavHome | NavCell of a*b` (`Model.Nav`,
  dataset-reset), `DescendPair` (Possible/Registered cell click — impossible
  cells are inert holes) and `NavAscend` (Esc + the workspace ‹ button; at
  home a no-op). `MeshVisibility.shown` REDEFINED on Nav: home = all, cell =
  the pair only — consumed by render MeshActive, raycast candidates and the
  placement overlap count. Cell-workspace panel: persistent A↔B header
  (swatch·num·name chips + ↔), live pair-state line (registered quality /
  not yet / insufficient), back control. Esc order: armed placement cancels
  first, then ascend (the single backward primitive).
- **T3 per-pair toolkit**: "○ New pin" + "Isolate pins" moved INTO the cell
  workspace (no global hosts remain); placement is pair-scoped through the
  ONE visibility rule (countOverlap + raycasts filter by Nav, so the ≥2-mesh
  rule = "both meshes of THIS pair"). No global mode state exists.
- Green: client + server builds, Supertests 98/98 (pure layers untouched).
  Browser pass owed: whole-app navigation flow, workspace header, ghosting of
  non-pair meshes in a cell, placement inside a cell.
- DOC DEBT (frozen): CLAUDE.md sections on the three-mode rail, ONE-selection
  model, focus panel, dock, brushing, exact-point probe, solo/visibility,
  Esc grammar — all superseded by the hierarchy.

## v14 P4 — reference-root designation (2026-07-27)

Spec: `ScanPin_v14_P4_root_designation.md`.

- **T1 old star reference picker DELETED**: the roster ★/☆ toggle button
  (ClickGate single/double, toggle-off-to-None) is gone; the roster keeps a
  display-only `rail-root-mark` ★ on the current root.
- **T2 overview root designation** — a STATE of matrix-home, not a rail mode:
  `MatrixHome = HomeOverview | HomePairs` (+ `SetMatrixHome`, default
  Overview, reset per dataset), tabs "Setup | Pairs" atop the navigator;
  Setup = mesh survey rows (swatch·num·name·★, gold row accent) where click =
  `SetRegRoot name` (message narrowed from string option — None only ever
  meant pre-load).
- **True re-rooting** (`RegGraph.reroot`): designating a TREE MEMBER now
  keeps the registration — every edge on the path new-root→old-root reverses
  (Child/Parent swap = the REF/MOV flip, Transform inverted, Quality kept;
  path children removed before reversed re-adds — interleaving would drop
  edges), off-path edges untouched (subtrees follow their path node). All
  poses recompose relative to the new root: pose'(m) = pose(m)∘pose(newRoot)⁻¹.
  Reducer: member+edges → reroot + recompose + toast "registration kept";
  non-member with edges → clear + toast (a registered tree cannot hang off an
  outside mesh); no edges → plain designation. All paths re-seed anchors +
  invalidate probes/rings.
- Supertests 98/98 (+10: REF/MOV flip exactly on the path, off-path
  reference-equality, quality carry, pose'(m)=pose(m)∘pose(B)⁻¹ over all
  members, invariant survives (adds ok, cycles rejected), degenerate no-ops).
  Client + server builds green. Browser pass owed: tabs, gold root rows,
  roster ★ mark.
- DOC DEBT (frozen): CLAUDE.md roster-★/reference-picker wording, "reference
  change wipes solve" rule (now: member re-root PRESERVES registration).

## v14 P3 — mesh×mesh matrix navigator, read-only (2026-07-27)

Spec: `ScanPin_v14_P3_matrix_navigator.md`. Old pins×meshes matrix deleted
wholesale; new upper-triangular mesh×mesh navigator (read-only — descend/root
actions arrive P4/P5).

- **T1 pins×meshes matrix DELETED** — and with it the entire client slice-cell
  ecosystem (its only consumer): GuiRail's `SliceDiagram` module + boot JS,
  `refProfile`, `CellInfo`, matrixHead/matrixRow/matrixRows/matrixView, the
  window/vert-extent global scales, all `.mx-*` CSS (27 rules);
  `ScanPin.Slice` + `SliceState`/`PinSlice`/`SliceMesh` DTOs +
  `SliceComputed`/`SliceFailed` + `ensureSlices` + `invalidateSlices` +
  `Query.slice` + the slice helpers (`sliceWindow`/`sliceOffsets`/
  `sliceClipRadius`/`sliceCentreIndex`/`sliceNormalOf`) + `Model.MeshSpacing`
  + the four gear slice tunables (fields, messages, sliders). SERVER
  `/query/slice` + MeshAnalysis.planeSlices/dipAzimuth KEPT (integration
  tests cover them; the P7/P8 per-edge cell diagrams are their likely
  consumer — prune at rework end if none materializes).
- **T2 mesh×mesh navigator** (`GuiRail.pairMatrixView`, `.pmx-*` CSS): rows/
  cols = meshes, upper triangle only, no diagonal; cell (A,B) IS the pair
  edge. Emphasis ramp: impossible = background hole (`.pmx-imp`) < possible =
  outlined vessel (`.pmx-pos`) < registered = filled (`.pmx-reg`), fill
  strength = the edge's ONE quality scalar (achromatic ink alpha 0.30+0.65·q
  — colour families stay free). `RegEdge.Quality : float` added ([0,1], 1 =
  best; `tryAddEdge` takes it; P5's solve writes it). Pure state fn
  `PairCell.state` (RegistrationModel): registered (either orientation, via
  new `RegGraph.pairEdge`) beats overlap verdict; unfetched overlap reads
  impossible. Overlap data: `Model.PairOverlaps` (unordered `PairCell.key`),
  lazy `ensurePairOverlaps` postlude — all missing pairs fetched in parallel
  at the as-loaded baselines via new `Query.pairOverlap` (P0 endpoint),
  single-flight per generation (dataset switch bumps), ONE
  `PairOverlapComputed` batch. Mounted in Register + Inspect rail bodies.
- **T3 reordering**: `MatrixOrder` (OrderSensor | OrderCoverage |
  OrderConnected) + `MatrixNav.hopDepth`/`orderMeshes` (canonical tiebreak;
  coverage = bbox XY footprint; connected = root, hop distance, unconnected
  last) + `Model.MatrixOrder`/`SetMatrixOrder` + the "Order
  Sensor·Cover·Root" compactButtonBar above the matrix. Contents proven
  order-invariant in tests.
- Supertests 88/88 (+12: pair-cell states, quality carry, hop depth, the
  three orders, permutation + content-invariance); client + server builds
  green. Browser pass owed: navigator layout/ramp legibility, overlap sweep
  landing (Hessigheim pairs should all read possible), order toggle.
- DOC DEBT (frozen): CLAUDE.md matrix bullet (pin×mesh, slice cells, gold
  column, dim cross), slice-cache state rules, slice gear tunables, matrix
  selection-driver wording — all superseded.

## v14 P2 — per-edge before/after (2026-07-27)

Spec: `ScanPin_v14_P2_per_edge_before_after.md`. Pose semantics only; the
per-edge peek/diagram UI consumes it in P5/P7/P8.

- **T1 ensemble before/after DELETED wholesale.** Gone: `RegView` type+module
  and both model fields (`RegView`, `RegPeekHeld`), messages
  `SetRegView`/`SetRegPeek`, the top-bar Before/After/Peek cluster, hotkey I,
  `applyRegView` + `swapProbeViews` (the swap-in-place machinery), the whole
  Other-pose cache layer — `ScanPin.ProbeOther`/`SliceOther` fields +
  `ProbeOther*`/`SliceOther*` messages + their fetch halves in
  `ensureProbe`/`ensureSlices` (slice requests now send transformOther=None),
  `Model.FocusDistOther` + `FocusDistOtherComputed` + the other-pose branch of
  `ensureFocusDist`, every peek-selected cache read (3D inspectField, focus
  overlay/scalar, matrix cell slice choice), the dock charts' two-pose
  machinery (ha/hb halves, fill-vs-outline, "fill Before · line After" key —
  series now carry ONE `h` histogram), the After-refusal gates + toasts
  (placement/resize/arm/pick/XYZ-editor read-only) — with no global After
  state, editing is always legal. Displayed pose = composed graph pose else
  as-loaded baseline (`ModelTransforms.displayedRender`/`displayedWorld`, no
  view param); new `loadWorld` = the baseline in metric world — seeding + the
  pin-resize anchor kill evaluate there (was `displayedWorldAt RegBefore`).
  `MeshView.displayedWorldPeekAt`/`displayedWorldCommittedAt` merged into ONE
  `displayedWorldAt`; `effectiveRegView` deleted.
- **T2 per-edge before/after** (`RegistrationModel.fs`): `EdgeSide =
  EdgeBefore | EdgeAfter` + `RegGraph.composeEdge child side g` — Before =
  the committed graph with THIS edge's transform replaced by identity (pair
  unregistered against each other; every ancestor edge still applied through
  composition), After = committed. Model glue for later phases:
  `ModelTransforms.edgeWorld`/`edgeRender` (baseline fallback outside the
  tree). Supertests (+8, 76/76): on a 2-hop chain R←A←B, edge-B Before keeps
  ancestor A's pose value-identical and parks B at its parent's pose; edge-A
  Before moves the whole A-subtree while B keeps its own edge applied; purity.
- DOC DEBT (frozen): CLAUDE.md Before/After machinery (RegView bullet, probe/
  slice/FocusDist pose-pairing, Peek, Before-only correspondence guardrails,
  charts' pose key) all superseded; correspondence flows now evaluate at the
  as-loaded baseline.

## v14 P1 — graph state model + composed world poses (2026-07-27)

Spec: `ScanPin_v14_P1_graph_state_composed_poses.md`. Client pose/state core;
no new UI. In-app nothing creates edges yet (pair-solve flows = P2), so the app
runs pose-flat like an unsolved v13 session; the Before/After+Peek machinery
re-arms automatically once ComposedPoses is non-empty.

- **T1 star pose model DELETED**: `Model.SolvedTransforms`, `SolveInputs`
  (type + field), messages `SolveCoarse`/`CoarseSolved` + the whole ensemble
  lsq fan-out in the reducer, `ensureSolveValidity` postlude, `solveGen`
  guard, GuiRail's Solve button + `canSolve`. `LoadTransforms` (as-loaded
  baseline, identity per mesh) kept.
- **T2 registration graph** (`RegistrationModel.fs`, WASM-free → Supertests):
  `RegEdge` = { Child (MOV), Parent (REF = nearer root), Transform } — metric
  world, child-onto-parent at baseline (lsq convention); `RegGraph` =
  { Root; Edges : Map<child, RegEdge> } (a parent map ⇔ rooted tree).
  `RegGraph.tryAddEdge` enforces the committed invariant: accepted iff ref in
  tree ∧ mov not — both-in ⇒ cycle rejected, ref-out ⇒ isolated rejected.
  `withEdgeTransform` (re-solve), `children`, `inTree`, `hasEdges`.
  `Model.ReferenceMesh` REPLACED by `Model.RegGraph` (plain record → ONE
  aval); every reference-mesh read is now the graph root ((g).Root — ~30
  sites); `SetReferenceMesh` → `SetRegRoot` (root change clears the graph;
  same seed/invalidate/toast semantics). ReadinessInput keeps its own
  `ReferenceMesh` DTO field (engine input, fed the root).
- **T3 composed worldPose**: `RegGraph.composeAll` (BFS from root; pose(m) =
  edge.Transform * parentPose, apply-left-first; root = identity, absent from
  the map; a root-child's pose IS its edge transform ⇒ star graphs reproduce
  the old star poses exactly) + `RegGraph.composeSubtree` (memoized: only the
  changed child's subtree recomposes, prev entries carried through).
  `Model.ComposedPoses : Map<string, Trafo3d>` = the render-space projection
  (via `ModelTransforms.recomposePoses` — worldToRender per mesh), the
  drop-in successor of SolvedTransforms: `displayedRenderAt`/`displayedMeshT`
  /`displayedWorldPeekAt`/`displayedWorldCommittedAt`/FocusScene.cellZoom/
  SceneGraph bbox outline all read it at RegAfter; every solved-gate
  (SetRegView/SetRegPeek/applyRegView/ensureProbe/ensureSlices/
  ensureFocusDist/top-bar/charts/shift readout) = `ComposedPoses` non-empty;
  GuiRail corrStatus "aligned" = `hasEdges`.
- Supertests: +24 (invariant: no-root/self/cycle/isolated rejections,
  tree growth, children, transform replace; compose: star exactness, chain
  child-first order, sentinel-proven subtree-only recompute, incremental edge
  add). Trafo3d is a struct ⇒ memoization asserted via sentinel preservation,
  not ReferenceEquals. 69/69 green; client + server builds green (adaptify
  re-run).
- DOC DEBT (frozen): CLAUDE.md registration-state bullet (LoadTransforms/
  SolvedTransforms/SolveInputs wording), ReferenceMesh mentions, solve-flow
  description — all superseded by RegGraph/ComposedPoses.

## v14 P0 — server pairwise reference-free error (2026-07-27)

Spec: `ScanPin_v14_P0_server_pairwise_error.md`. Server only; client untouched
(its `/query/probe` calls now 404 at runtime — expected, later phases rework
the client onto the pair endpoints).

- **NEW `PairError.fs`** (replaces `MeshProbe.fs`, deleted with its fsproj
  entry): the established M3C2-style measure symmetrized. Per pin: shared axis
  n = PCA normal of the POOLED A∪B vertex set in the pin sphere (oriented
  n.Z ≥ 0); both surfaces lattice-sampled in the cylinder; sample value =
  signed axial offset of B relative to A (B-sample: t_B − median(t_A);
  A-sample: median(t_B) − t_A) pooled into ONE distribution per pin — swap
  A↔B negates every value. Median over the pooled set;
  lodHalfWidth = 1.96·√(σ_A²+σ_B²). Full derivation + sign convention in the
  module doc-comment. Poses always explicit in the request (server stateless
  w.r.t. the registration tree).
- **Endpoints** (QueryHandlers + Handlers routes; `/api/query/probe` +
  probeHandler + ProbeRequest/ProbeMeshDto DELETED):
  - `POST /api/query/pair-error` `{meshA{name,transform}, meshB{…}, pins:[{id,
    centre,radius}], length, maxPointsPerMesh}` → `{pins:[{id, ok, reason,
    normal, count, median, lodHalfWidth, samples, positions}]}` — per-pin
    ok=false on no overlap, batch never fails on it; samples ≤300/pin with
    aligned flat xyz positions (probe payload convention kept).
  - `POST /api/query/pair-error-at` `{meshA, meshB, point, radius, maxDist}` →
    exact signed value at one picked point: each mesh's surface crossing of
    the line (point + t·n) nearest the point, value = t_B − t_A; 1 mm ray
    origin back-off so exactly-on-surface picks still register.
  - `POST /api/query/pair-overlap` `{meshA, meshB, maxDist, minFraction,
    maxSamples}` → `{sufficient, fracAB, fracBA, maxDist, samplesA, samplesB}`
    — stride-sampled closest-point coverage both directions, sufficient ⇔
    max(frac) ≥ minFraction (default 0.05); maxDist default 1 % of mean posed
    bbox diagonal clamped [0.5, 20] m; posed-bbox pre-reject.
- Kept (comment-only touchups, "probe convention" → Forward wording):
  `region-distance` (already pairwise pose-explicit), `slice` (referenceName
  is azimuth-source only), `lsq-pairs`, `contact-rings`, `ray`, `closest`.
- `tools/integration.mjs` §4 rewritten probe→pair-error (swap symmetry,
  determinism, perturb→lsq-correct shrink, per-pin overlap gate in one batch)
  + new §7 pair-error-at (antisymmetry, +5 m lift tracking, off-surface
  reject) + §8 pair-overlap (co-located sufficient / disjoint insufficient).
- Green: server build, integration 36/36 on :8002 (stale leftover Superserver
  on the port killed first), Supertests 45/45.
- DOC DEBT (frozen): CLAUDE.md API list still shows `/query/probe`, compile
  order still lists `MeshProbe.fs`; probe-pair language ("ScanPin.Probe",
  reference-star wording) all over the state rules — rewrite at rework end.
