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
- Per-mesh FOOTPRINT contours (user report: "only a combined outline of all
  meshes"): root cause is architectural — the combined G-buffer is depth-
  tested, so edges only fire at depth breaks; co-located meshes yield one
  union outline and hidden boundaries aren't in the buffer at all. Fix = a
  second additive coverage pass (channel per mesh, 2×Rgba8 MRT, no depth,
  cap 8 meshes) + `OutlineCoverageEdge` composite painting each channel's
  covered↔uncovered transition in that mesh's colour, gated by the same
  OutlineMask flags. Build green, Supertests 29/29. Browser pass owed: two
  NEW shaders (`OutlineCoverage`, `OutlineCoverageEdge`) compile in-browser;
  each mesh shows its own closed contour in the pair view; additive blend +
  depth-less FBO work on the Aardworx WebGL backend; per-mesh contour also
  respects the ◌ toggle. ShaderCache stale → ./precompileShaders.sh.
- Per-mesh outline toggles (follow-up to B4/B5, user-requested): client build
  green, Supertests 29/29; BOTH outline shaders changed (G-buffer mesh id +
  fragment-depth bias, edge-pass mask) → in-browser compile check + visual
  pass owed (◌ tile toggle both modalities, Inspect pair view: context =
  silhouette only / no isolines, no speckle on the inspected pair where
  epochs are co-located — the 5e-5 depth push may need tuning). ShaderCache
  stale → ./precompileShaders.sh at leisure.

## Done (outline toggles)
- G-buffer (`OutlineGBuffer`): target0.y = MeshId ((index+1)/255, 8-bit exact
  in Rgba8), fragment depth = fc.Z + OutlineDepthBias (silhouette-only
  context meshes get 5e-5 so the co-located inspected pair wins depth ties —
  no per-pixel id/colour alternation).
- Edge pass (`OutlineEdge`): `OutlineMask : Arr<N<32>, V4f>` (Blobs-style),
  slot = centre-pixel id; .X = 1 → silhouette+isolines, 0.5 → silhouette
  only, 0 → nothing.
- Policy (`MeshView.outlineFlagAt`/`outlineBodyShownAt`/`outlineMask`):
  Inspect + MeshSolo ⇒ everything except {solo, reference} = 0.5 (the
  feature: pair view context keeps only its contour — ghosts already gone
  per B5). Toggle modalities resolve from body visibility: `OutlineVisible`
  off + body visible ⇒ flag 0, mesh stays in the G-buffer (occludes); off +
  no body ⇒ Sg.Active false, mesh leaves the buffer (stops occluding).
- Model `OutlineVisible : Map<string,bool>` (sparse false entries, reset on
  dataset load) + `SetOutlineVisible`; ◌ toggle on the focus tile strip.
- `OutlineView.buildFromNode` takes the mask (`maskAllOn` for the focus
  reference overlay; reference node binds dummy MeshId/bias).
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
