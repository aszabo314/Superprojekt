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
- Spec B implemented (B1–B6), all builds green after each task, Supertests
  29/29, integration 13/13 on :8004. Final report sent. Awaiting the user's
  browser pass — BOTH mesh shaders changed (MeshShaders: incidence geometric
  normal + shape discard + InspectPlain base; FocusShaders: shape discard) →
  needs the in-browser compile check; ShaderCache entries stale → runtime
  compile fallback, rerun ./precompileShaders.sh at leisure. Eyeball list:
  new palettes everywhere (swatches, rings, chart layers, constellation now
  PIN-coloured, aim ghost pin-coloured), isoline density steps while zooming,
  Dst legend + one range scale across meshes, Shp slider transparency, Inc
  red on artifact triangles, Inspect = plain base + outline-only context,
  Alt-wheel label only while Alt held.

## Done (Spec B implementation notes)
- B1 palettes: meshes = cool/earth 9 (teal/ochre/slate/cyan/brown…), pins =
  vivid warm/purple 10 (orange/fuchsia/violet/pink…, glyph-paired) — both
  clear of the gradient hues (red/blue/green/yellow), no-data grey, gold ★.
  Constellation markers, focus anchor cross+ring and the aim ghost (2D + 3D)
  now use the PIN colour (mesh identity there was the old crosshair/circle
  disagreement); slice-profile lines/dots stay mesh-coloured deliberately.
- B2 ContourSpacing camera-adaptive: ~24 contours per view from orbit radius,
  snapped to nice 1/2/5 world-metre steps; gear IsolineBands = densest cap,
  ≥4 contours at the far end. Difference isolines rely on their existing
  in-shader density fade (no camera term needed).
- B3 Dst: one all-mesh scale (MeshView.rangeMaxWorld) in 3D + focus, legend
  shows outside Inspect while any Dst is on. Shp: ShapeThreshold model field
  (+ SetShapeThreshold, adaptify) → discard below cutoff in both shaders,
  slider in the Overview rail (visible only while a Shp heatmap is on).
  Inc bug: was abs(dot(vertex-normal, toSensor)) — away-facing surfaces and
  smoothed sliver normals read head-on/good; now geometric (screen-derivative)
  face normal, sign-oriented by the stored normal, clamped at 0. Focus CPU
  variant drops abs → clamp 0 (per-vertex; can't do derivatives there).
- B4 outline-only: the outline pass already was an independent offscreen
  pre-pass over ALL loaded meshes (OutlineView G-buffer → composite); added
  the per-mesh body-suppression lever (outlineOnly ⇒ ghost floor 0 ⇒ every
  non-emphasized fragment discards, outline survives).
- B5 Inspect de-clutter: outlineOnly = (WorkflowStep = Inspect) — all ghost
  fills gone, context meshes outline-only; new InspectPlain shader base =
  plain near-white under the false-colour painters (no photo texture /
  palette / slope in Inspect; shading kept).
- B6 tooltip bug: meshWheelLabel showed the persistent ActivePickingLayer
  name at the cursor forever after one Alt-wheel (the layer is never cleared
  — it steers pick priority); now gated on Alt actually held. All other
  tooltips are static per-element titles (audited).
- Docs: CLAUDE.md (palette families, Inspect de-clutter in the ghosting
  contract, adaptive isolines, intrinsic rules), README Inspect bullet.

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
