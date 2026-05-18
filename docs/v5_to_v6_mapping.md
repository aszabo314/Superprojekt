# V5 → V6 Mapping

Working reference for the V5→V6 migration. Lists where each V5 feature lives
in the current codebase. Updated as Phase 1 (deletion) progresses.

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
