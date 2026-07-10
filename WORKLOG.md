# Worklog — ScanPin v10: selection unification + cleanup

Spec: `ScanPin_v10_selection_and_cleanup_spec.md`. Two specs, hard checkpoint
between them (Spec A report → wait for approval → Spec B).

## Objectives (Spec A — selection unification)
- A1 One `Selection` = {mesh | pin | cell(pin,mesh)} + hovered; matrix is the driver.
- A2 Every view follows the selection (3D emphasis, focus framing, tiles, graph, legend).
- A3 Detail graph = one selection-driven diagram; chart's own selection UI removed.
- A4 Brushing suppresses the false-colour map; only brushed dots show.
- A5 Legend tracks selection + brush + active map continuously.
- A6 End-to-end audit; build + tests green. → CHECKPOINT, wait for approval.

## Objectives (Spec B — cleanup, after approval)
- B1 Disjoint palettes; crosshair/circle/markers share pin colour.
- B2 Camera-adaptive tick-snapped isolines.
- B3 Overview intrinsics: Dst unified+legend, Shp threshold slider, Inc bug.
- B4 Independent outline pre-pass → outline-only representation.
- B5 Inspect de-clutter: false-colour as base, context meshes outline-only.
- B6 Tooltip bug.

## In progress
- CHECKPOINT reached (Spec A done): client typecheck green (0 errors), full
  server build (incl. wasm native) green, Supertests 29/29, integration 13/13
  against a fresh server on :8004, shell + API serve. Checkpoint report sent —
  WAITING for user approval before Spec B. In-browser passes still owed to the
  user (headless WebGL is broken in this environment): selection round-trip in
  all three modes, focus derived framing feel, one-diagram chart, brush = sole
  focus, legend follow. No shader changes in Spec A → no ShaderCache impact.

## Done (Spec A implementation notes)
- A1 `Selection = { Active : ActiveSelection; Hovered }`,
  `ActiveSelection = SelNone | SelMesh | SelPin | SelCell(pin,mesh)`;
  projections `Selection.pin`/`Selection.mesh`. One `SetSelection` message
  replaces `SetFocusedMesh` + `ScanPinMessage.SelectPin` + `FrameCorrespondence`
  (cell selection IS the locate: solo + backup + ProjTop + Inspect pin-ROI).
  ToggleCorrArm aligns Active to its cell. Deleted pin: SelCell→SelMesh,
  SelPin→SelNone. Drivers: matrix col/row/cell, roster, tiles, 3D pin dots,
  3D surface click (background miss → SelNone).
- A2 reducer keeps the per-mode policies (Inspect solo swap etc.); MeshView
  isActive adds selection emphasis outside Inspect (selected mesh solid, rest
  ghost floor). Focus cameras are now DERIVED followers: `selBaseFrame`
  (pin → pin region ×4, cell → that mesh's marker hard-zoom, mesh/none → fit)
  composes with per-(mesh, selection-target)-keyed user pan/zoom offsets;
  tiles frame the selection on their own mesh (small-multiples compare). All
  imperative FocusScene camera helpers (resetCam/focusOnWorld/zoomOnWorldRadius/
  zoomOnPin/currentSingleMesh) deleted with their click-site calls.
- A3 dock chart = ONE selection-driven diagram (mesh→by-pin, pin→by-mesh with
  mesh colours, cell→single pair with median tick, none/ref→ensemble by-pin);
  pin-chip legend UI removed (CSS pruned); metric toggles kept; brush arrays
  restricted to the shown subset (gids stay global canonical).
- A4 brush non-empty ⇒ 3D inspectField → enc 0, focus overlay modes → 0,
  displacement arrows off; brushed dots (shared `ScanPinScene.brushedDotGeometry`,
  now coloured on the SHARED inspect range) render in main 3D + focus single +
  tiles. Click clears (existing bridge) and restores maps.
- A5 legend follows brush ("Brushed samples", diverging over shared range) +
  selection (cell appends pin identity to the Difference title) + channel.
- CSS: `.mx-cell-sel` (selected cell); removed `.ins-dist-leg*`.
