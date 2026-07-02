# Worklog — visibility / linking cleanup pass

## Objectives
1. Clean up automatic visibility state changes (solo/isolation, per-mode defaults, stale caches).
2. Small-multiples ◐ isolate: toggle on/off on same button; exit resets all visibility toggles to ON; vis toggles locked while isolation active; workflow switch ends isolation.
3. Inspect mode: mesh focus (≠ ref) → false-colour on that mesh + isolate {reference, mesh}; pin focus → mesh isolation off, pin isolation on; both zoom 2D + 3D cameras tightly.
4. Audit linked interactions: mesh click ⇒ shared focus + 3D fly; pin click (rail) ⇒ 3D tight fly + focus panel zooms onto pin on current mesh; matrix cell ⇒ 3D very close + focus switches mesh & zooms onto the correspondence.
5. Fix stale state / code smells along the way.

## In progress
- (nothing — pass complete; user verified in browser, adjustments below applied)

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
- 3D pin-dot tap selects the pin (reducer policies apply) but cannot zoom the 2D
  focus canvas (ScanPinScene compiles before FocusScene); the rail pin-row is the
  linked path per spec. Revisit if 3D pin taps should also drive the 2D zoom.
- In-browser verification of the full Inspect flow still pending (shader paths
  unchanged, but ghost-floor appearance of "others" during Inspect solo is a
  visual change worth eyeballing).
