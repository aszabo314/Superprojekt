# ScanPin v7 overhaul — implementation log

Continuous log for the `ScanPin_v7_coding_spec.md` rewrite. Newest entries at the bottom of each phase. Build/verify commands and conventions live at the end.

---

## Phase 1 — Aggressive removal ✅ COMPLETE & verified green (2026-06-24)

All three projects build clean; `dotnet run --project src/Supertests` → **86/86 passed**. Independently verified (not just trusting subagent reports).

Removed everything not in the spec, in dependency order, building between steps:

| Feature | Notes |
|---|---|
| **Study mode** | client StudyModel/StudyApi/StudyTelemetry/StudyUpdate/GuiStudy; server StudyConfig/StudyStore/StudyHandlers; `/api/study/*`; `studies/` dir; study tests. *(user-confirmed; not in spec)* |
| **Panorama** | PanoramaView.fs, Pano shader, `▦ Pano`, model state, card. *(user-confirmed)* |
| **Fusion** | FusionView.fs, FusionShader, `◈` toggle, CPU-raycast pick |
| **Save/Load** | Persistence.fs, gear Save/Load, workspace JS |
| **Retarget** | types, card, messages, reducer |
| **Lasso** | card, draw layer, `◌`, shader `LassoPlanes` |
| **Registration history** | RegStep/RegLog history, rollback `↩`, reset `↺`, `★ Set as final`, median-offset strip → **single commit** |
| **Iso-plane/cutaway/ruler** | `⊟ slice`, `lock\|d`, cutaway, locked-plane gizmo. **Kept reference-peek.** |
| **Old D/A/C error model** | Provenance module, three-source bar, provenance+diff heatmaps (`HeatmapMode`→`HeatOff`), prov tooltip, dataset-error UI. **Kept SensorType/MeshSensorTypes.** |

### Decisions / assumptions
- Study + Panorama removed per explicit user confirmation (neither in spec nor §12 list).
- `ServerActions` (dataset bootstrap) relocated from deleted StudyUpdate.fs → `UpdateHelpers.fs`.
- `StudyGate` kept as a 2-line always-on shim so existing `showWhen` feature-gates compile (full app = all visible); prune in Phase 2/3.
- Single-commit registration: `CommitRegistration` applies pending delta to `MeshTransforms` + re-bases anchors, no log. Preview (`PendingReg`) intact.
- `RegTransformState` deleted (trivial after removals); `LastSolve` kept (independent diagnostics).

### Dead-but-harmless leftovers (Phase-3 prune targets)
- `StudyGate` shim (Primitives.fs)
- `GuiCards.registrationToggleButton`
- generic clip-plane shader plumbing (fed constant no-clip in View/MeshView/MeshShaders)
- `ProbeResult.Sources` + `CardCharts.probeBarJs` (dead three-source data; server MeshProbe untouched)

---

## Phase 2 — Implement the spec ⏳ IN PROGRESS

Target (spec §1–§11): slim top bar + dark debug menu; left workflow rail (stepper 1 Reference · 2 Coarse align · 3 Fine ICP · 4 Inspect · 5 Commit) + PINS list; right focus panel (2nd WebGL control + small-multiples); §3 data model (Mesh/Pin{roiRadius,isCorrespondence}/Solve/Preview); §5 translate-only ortho coarse align; §6 heatmaps (intrinsic incidence/shape/range + extrinsic z-diff/m3c2 + variance); §7 movement layer (glyph field + warped grid); §8 pin glyph semantic zoom + violin flyout; §9 ghost isolation modes; §10 image-space edge-detection outlines. Constraint: ≤2 live WebGL controls. LoD₉₅ = `1.96·(√(σ_ref²/n_ref+σ_M²/n_M)+reg)`.

Caveat: FShade shaders only validate in-browser, so the shader-heavy items (§6 heatmaps, §7 movement, §8 glyphs, §10 outlines) are build-checked here but need a browser pass.

### Progress
- **§2 Left workflow rail** ✅ (build-verified) — new `GuiRail.fs` (`GuiRail.rail`): vertical stepper (1 Reference · 2 Coarse · 3 Fine · 4 Inspect · 5 Commit), one step expanded at a time, per-step readiness pill (ready/warn/block/info), + a PINS list (place / select / ⚲ promote-to-correspondence / ✎ edit / ✕ delete). Reuses existing messages (SetReferenceMesh, SetVisible, SetMeshSensorType, SolveCoarse, RunRegistration, SetRegistrationMode, Commit/Discard, ScanPinMsg…). New model state: `WorkflowStep` enum + `Model.WorkflowStep` + `SetWorkflowStep` msg. Wired into `View.fs` replacing `GuiPanels.leftPanel` + `GuiWorkflow.workflowPanel` (both now dead → Phase-3 prune). Hamburger now toggles the rail (`MenuOpen` default→true, rail gated on it). CSS added to `style.css` (`.workflow-rail`/`.rail-*`).
- **§1/§11 Slim top bar + dark debug menu** ✅ (build-verified) — top bar reduced to hamburger · ⟲ reset · 👁 Peek (hold) · ⚙ debug. Removed from bar: dataset selector, ○ Pin (→ rail), ⚲ Registration (→ rail IS the registration surface), coord readout. Dataset switch + rendering-mode (Textured/Shaded/Slope) moved into the ⚙ gear popover (debug menu) alongside camera speed / ghost / shading / slope / centroids / log.
- **§3 data model** — NOT renamed. The existing model already realizes §3's shape under different names (Mesh = MeshNames/MeshVisible/MeshOrder/MeshSensorTypes/MeshTransforms/Registration.ReferenceMesh; Pin = `ScanPin` with `Correspondence`/isCorrespondence=`Correspondence.Enabled`/`InnerRadius`=roiRadius; Solve = `LastSolve`; Preview = `PendingReg`). A cosmetic record rename was deferred to avoid high-churn rework — revisit only if a strict §3 shape is required.
- **§1/§5 Focus panel = secondary ortho WebGL control** ✅ (build-verified; needs browser check) — new `GuiFocus.fs` (`GuiFocus.panel`): right-docked panel with the **second** (and only second) live WebGL control, rendering the scene orthographically in render-space (Top/Front/Side button group, one at a time). Mounted only while open (`FocusOpen`), so ≤2 controls holds. **Pointer drag translates** the selected moving mesh in the two in-plane axes (`TranslateAlignMesh`, render-space, translate-only) → updates that mesh's committed `MeshTransforms`. Moving-mesh selector = visible non-reference meshes. Reopen tab when closed. New model state: `FocusOpen`/`FocusAxis`/`AlignMesh` + messages. CSS `.focus-*`.
- **§9 Ghost isolation modes** ✅ (build-verified; needs browser check) — *Align-auto*: in step 2 (StepCoarse) the moving mesh renders solid, all others ghosted, in BOTH the main viewport (via `wheelIsolation`) and the focus control. *Pin-focus*: `PinFocusMode` (rail toggle in Inspect) restricts the mesh-shader blob uniforms to the focused pin's ROI and forces isolation on → ghost everything outside that one pin. *Movement-auto*: deferred (needs §7 movement layer).
- **§4 Pins / ROI** ✅ already spec-compliant — `ScanPin` is the single primitive; `InnerRadius` = roiRadius with an **inline log-slider** in the placement flyout; `Correspondence.Enabled` = isCorrespondence (⚲ in the rail); auto-seed = closest-point; probe evaluates inside the ROI cylinder. No change needed beyond the rail's promote toggle.
- **§8 Pin glyph — far/preattentive view** ✅ (build-verified; needs browser check) — `ScanPinScene.pinGlyphs`: per committed pin a pole + head-ring (`Lines`), head colour = verdict (green `#16a34a` if every moving mesh's |median| ≤ LoD₉₅, red `#dc2626` if any significant, grey when no probe yet), pole height grows with magnitude (max |median offset| across moving meshes). LoD uses the spec form `1.96·√(σ_ref²/n_ref + σ_M²/n_M)` (reg term ≈ 0 for now). The **near/attentive split-violin = the existing pin card / flyout** (opens on select) — not re-rendered as a 3D billboard. Coexists with the clickable `pinDots`.
- **§7 Movement layer** ✅ (build-verified; needs browser check) — `ScanPinScene.movementLayer`, preview-only. `MovementMode` (Off / Arrows / Grid), rail-Inspect button group. *Arrows*: 5×5 grid over each committed pin's ROI plane, before→after displacement arrow (with chevron head) where after = world preview-delta · before. *Grid*: original faint lattice + warped accent lattice. World delta via `RigidTransform.worldDeltaOf`. **Movement-auto ghost** (§9): when the layer is on under a preview, `wheelIsolation` isolates the first pending-delta mesh (moved mesh solid + glyphs, rest ghosted).
- **§6 Heatmaps — intrinsic triad + extrinsic M3C2** ✅ (build-verified; needs browser check). `HeatmapMode = HeatOff | HeatIncidence | HeatRange | HeatShape`, rail-Inspect Off/Incidence/Range/Shape group + an Extrinsic-M3C2 toggle.
  - **Intrinsic incidence** — mesh-shader block: |n·toCam| → red (grazing) → green (head-on).
  - **Intrinsic range** (user spec: sensor = each mesh's own origin, no coefficient) — `SensorOrigin` per-mesh uniform = full mesh trafo · (0,0,0); `RangeMax` = scale × max|local vertex| (computed at load, `LoadedMesh.localMaxR`); shader paints `|wp−SensorOrigin|/RangeMax`, blue (near) → red (far). Rigid-invariant.
  - **Intrinsic shape** (user spec: any reasonable default with signal on thin/degenerate tris) — per-vertex triangle quality `4√3·A/Σl²` (1 = equilateral, →0 = sliver), incident-face mean, computed at load into a `ShapeQ` per-vertex attribute (mirrors `SurfaceDist`); shader red (poor) → green (good).
  - **Extrinsic M3C2 / z-diff** — the kept `DistanceEncoding` surface map, surfaced as a rail toggle (`SurfaceDistOn`; paints the soloed moving mesh) with an **M3C2 ↔ Δz** mode switch (`ExtrinsicZDiff`). Server `region-distance` gained `Mode` (0 = signed closest-point M3C2, 1 = vertical Δz via Embree raycast onto the reference, signed: moving-above-reference → positive). Both render through the same diverging map.
  - **Variance / disagreement (all-meshes)** ✅ — `VarianceOn` (rail toggle). Default selection = **all visible moving meshes (≥2)** (no multi-select UI for now). `Update.ensureVariance` postlude fetches per-reference-vertex distance to each moving mesh (region-distance target=reference, N parallel fetches), computes per-vertex **std** (ignoring sentinels), emits `VarianceComputed(refMesh, std[])` → stored in `SurfaceDistance[refMesh]`. Painted on the **reference** mesh via a new `DistanceEncoding = 2` **sequential** ramp (light grey → red, normalised by DistScale). Mutually exclusive with the single-mesh extrinsic map. **§6 heatmaps are now complete** (intrinsic incidence/range/shape + extrinsic M3C2/Δz + variance).
- **Still TODO**: §1 focus small-multiples (per-mesh extrinsic miniatures — canvas + a multi-mesh distance fetch; redundant with the now-complete spatial heatmaps), glyph **near-in-3D** semantic zoom (currently the flyout), §3 optional record rename, **Phase 3 final prune** (clean build = §13 acceptance). Everything else in the spec is implemented + green. (dead: `GuiWorkflow.workflowPanel`, `GuiPanels.leftPanel`, `StudyGate` shim, `GuiCards.registrationToggleButton`, clip-plane shader plumbing, `ProbeResult.Sources`/`CardCharts.probeBarJs`). All three projects build green; 86/86 tests pass. **Needs browser verification:** all GPU pieces, especially §10 outlines (new pipeline) + §6 shader ramps.

### §10 image-space outlines — IMPLEMENTED (first cut; build-verified; **needs browser verification + threshold tuning**)
`OutlineView.fs` (new) + `OutlineGBuffer`/`OutlineEdge` shaders (MeshShaders.fs) + `MeshView.buildOutlineNode` + SceneGraph wiring + `OutlineMode` toggle (debug menu, **default off**). Offscreen MRT pass: target0 = world-normal(rgb)+window-depth(a), target1 = palette-colour(rgb)+mask(a); custom attachment `Sym.ofString "Outline1"`. Edge-detect fullscreen composite: edge = depth jump (>0.0015) OR normal-angle jump (>0.30) OR mask boundary; paints per-pixel palette colour, alpha-blended overlay (DepthTest.None). Mask boundary covers silhouette + near-plane cut (no inverted hull, per spec). Lazy: offscreen task only runs when `OutlineMode` on, so default-off cannot regress the forward pass. **Thresholds (0.0015 / 0.30) and the world-normal encoding are guesses — tune in-browser.** Risk: MRT signature + custom-attachment + FShade MRT output compiled, but the GLSL/render is unverified here.

### §10 image-space outlines — original plan (kept for reference)
Offscreen-pass API recovered from `git show HEAD:src/Superprojekt/FusionView.fs` (signature/texture/attachment/framebuffer + `runtime.CompileRender` + `RenderTask.renderToWithClear` + `GetOutputTexture` + a fullscreen-quad composite reading the texture). Plan:
1. **G-buffer offscreen pass** (`MeshView.buildOutlineNode`, mirrors the removed `buildFusionNode`): render all meshes to an **MRT** — target0 Rgba8 = `world-normal*0.5+0.5` (rgb) + window depth `fc.Z` (a); target1 Rgba8 = per-mesh **palette colour** (rgb) + coverage mask (a=1). New float32-only gbuffer shader.
2. **Edge-detect fullscreen pass**: sample target0/target1 at centre ±1 texel (texel = 1/viewport); edge = depth jump **OR** normal-angle jump **OR** mask boundary (the mask boundary is the near-plane cut + silhouette → satisfies the "incl. near-plane clip, not inverted-hull" requirement). Output target1.rgb where edge, else transparent.
3. **Composite** over the main framebuffer (alpha-blended overlay), gated behind a new `OutlineMode` toggle (**default off** so it can never regress the working viewport).
Risks: FShade MRT output-record attributes; edge thresholds + normal encoding need in-browser tuning; keep float32-only.

### Session checkpoint (2026-06-24)
Landed this session on top of Phase 1: §2 rail, §1/§11 top bar + debug menu, §1/§5 focus panel (2nd ortho control + translate-drag), §9 ghost isolation (align-auto + pin-focus + movement-auto), §4 ROI (already compliant), §8 pin-glyph far view, §7 movement layer, §6 FULL (intrinsic incidence/range/shape + extrinsic M3C2/Δz + variance), §10 outlines (gated). New files: `GuiRail.fs`, `GuiFocus.fs`. New model state: `WorkflowStep`, `FocusOpen`/`FocusAxis`/`AlignMesh`, `PinFocusMode`, `MovementMode`/`MovementLayer`, `HeatmapMode` (+Incidence/Range/Shape), `MenuOpen` default→true. New mesh-shader uniforms: `HeatmapMode`/`SensorOrigin`/`RangeMax` + `ShapeQ` per-vertex attribute (+ `LoadedMesh.localMaxR`). All build-verified green; GPU/scene pieces need a browser smoke-test. Remaining: §6 z-diff/variance, §10 outlines (plan above), §1 small-multiples, §3 optional rename, Phase-3 prune.

---

## Phase 3 — Final prune ✅ (2026-06-25)
Deleted now-dead code (all-green after): `GuiWorkflow.fs` (workflowPanel → replaced by the rail) and `GuiCards.fs` (lasso/retarget/panorama cards + spark + registrationToggleButton, all unreferenced) removed from disk + fsproj. `GuiPanels.fs` reduced to just `placementFlyout` (mesh/pin/registration sections → the rail). `StudyGate` shim removed from `Primitives.fs` (its last users — leftPanel + CardsPin gates — neutralised to always-on). `CardCharts.probeBarJs` (dead three-source bar JS) removed. **Left intentionally** (low value / shader risk): the generic clip-plane shader plumbing (fed constant no-clip), `ProbeResult.Sources` (server-computed, client-unused), and the vestigial `WorkflowPanelOpen`/`ToggleWorkflowPanel` model state (harmless; `WorkflowPinHover` is still used by the pin hover highlight). See `HANDOFF.md` for the full record.

## Build / verify / conventions
- Client (fast, native off): `dotnet build src/Superprojekt/Superprojekt.fsproj -p:WasmBuildNative=false -clp:ErrorsOnly`  (~37s)
- Server: `dotnet build src/Superserver/Superserver.fsproj`
- Tests: `dotnet build src/Supertests/Supertests.fsproj` then `dotnet run --project src/Supertests`
- After any `[<ModelType>]` edit (Model.fs, ScanPinModel.fs, RegistrationModel.fs, CameraModel.fs): `bash adaptify.sh`. **Never hand-edit `.g.fs`.**
- FShade shaders are **float32-only** (V3f/V2f/float32/`0.0f`); only verify in-browser.
- Light theme, accent `#1a56db`; data accent `#0891b2`; reference amber `#b45309`; significance `#16a34a`/`#dc2626`. Mesh palette = 9 categorical colours.

---

## Addendum — Pin Inspector rebuild (`ScanPin_v7_pin_inspector_spec.md`) ✅ (2026-06-16)

Replaced the per-pin detail surface. All three projects green; client (native off) + server + Supertests (58/58, was 86 — 28 DetailViewMath tests removed). GPU/JS pieces still need a browser pass.

### A. Removed (entirely, with backing code)
- **`CardsPin.fs`, `Cards.fs`, `CardUpdate.fs`, `CardCharts.fs`, `DetailViewMath.fs`** — deleted from disk + fsproj (+ Supertests fsproj).
- **Card system**: `CardSystemModel`/`Card`/`CardId`/`CardContent`/`CardAnchor`/`CardAttachment` types, `CardMsg`/`CardMessage`, `model.CardSystem`, the select→`CreateCardsForPin` postlude, `reanchorCards`.
- **Violin / split-violin** (`CardCharts.ridgelineJs`) + the **hover-probe tooltip** that rendered it (`GuiOverlays.hoverProbeTooltip`, `HoverProbe` state/messages/`HoverProbeState`, `hoverProbeBody`, Ctrl-click path).
- **Object-centred detail view**: `buildDetailJson`/`detailJs`/`detailSection`, `DetailViewMath`, `Model.DetailGrids`/`DetailGridPin`, `DetailGridsComputed`, `ensureDetailGrids`, server `/api/query/region-grid` + `RegionGridRequest` + `MeshAnalysis.regionGrid` + `Query.regionGrid`, `ElevGrid*` types.
- **Linked-view chart thread**: `ChartCursor`/`ChartHoverMesh`/`ChartStickyMesh`/`SurfaceDistBrush` model + `SetChartCursor`/`SetChartHoverMesh`/`ChartColumnClick`/`ClearChartSticky`/`SetSurfaceDistBrush`/`SetCorrMarkerHover`/`CorrMarkerHover`, `cursorPlane` scene node, the A3 range-brush shader path (`DistBrushOn/Lo/Hi`). Kept the pin-hover→glyph highlight (`WorkflowPinHover`).
- **Manual correspondence pickers** (decision below): 3D anchor pick (`AnchorPick`/`StartAnchorPick`/`CancelAnchorPick`/`AnchorPickHit`/`SetAnchor`/`pickGuide`) and the **patch small-multiples picker** (`PatchPicker*`/`PatchHover`/`OpenPatchPicker`…/`patchLink`), since their only entry points lived in the deleted card and the new inspector has no picker. Auto-seed (closest-point) remains the marker source.

### B. Built — `GuiInspector.fs` (always-mounted 2D bottom dock)
- Full-width, fixed 220 px, docked below the viewport (`.render-control` height = `calc(100% - 220px)`, rail/focus/scale/orient nudged to clear it → never overlaps the 3D scene). Reads the effective selected pin; neutral `◌` placeholder when none. No prose — numbers/marks/glyphs only.
- **B1 identity**: inline name (`RenamePin`), ROI radius log-slider (reuses `SetInnerRadius`, now generalised to the effective pin, not placement-only), `k/n`, `⚲` promote/demote, `✕` delete.
- **B2 raincloud** (SVG via `observedRender`): one row per visible moving mesh on a shared zero-centred axis (robust 1–99 pct of pooled before+after). Per row: ±LoD band (`1.96·√(σ_ref²/n_ref+σ_M²/n_M)`), after = rain (always, ≤300 server-subsampled) + median/IQR box + half-violin only at N≥20, before = single committed-median tick; greyed when no samples. Row click → `SetInspectorMesh`.
- **B3 readout**: per moving mesh — swatch · `✓/✗` placed · residual mm (`Correspondence.Residuals`). Click selects the active row.
- **B4 intrinsic bars**: I/R/S for the active moving mesh from the new probe `Intrinsics`, red→green ramp.

### C. Model/message
- New: `Model.InspectorMesh : string option` (active row; also the extrinsic-map target — replaced `ChartStickyMesh`), `SetInspectorMesh`, `RenamePin`. `ToggleSurfaceDistance`/`SurfaceDistanceComputed`/`ensureSurfaceDistance`/`MeshView` rewired to `InspectorMesh`. `shortName`/`numbered`/`c4bToHex` moved to `Primitives` (their `Cards`/`CardsPin` home was deleted).

### Server
- `MeshProbe`: `ProbeDistribution.Samples` now always returned (≤300 subsampled — full set still drives stats); new `ProbeDistribution.Intrinsics = [incidence; range; shape]` computed over the ROI cylinder (`meshIntrinsics`). Surfaced as `intr` in the probe handler; parsed into `ProbeModel.ProbeDistribution.Intrinsics`.

### Decisions / deviations
- **Manual pickers removed, not re-homed.** The spec's new inspector defines no picker and mandates removing the card + "no unreferenced symbols left"; auto-seed covers marker placement. (Patch picker was a kept feature in the *first* v7 spec, but this addendum deleted its only host.)
- **HoverProbe (Ctrl-click quick probe) removed** — its only renderer was the violin.
- **B4 "incidence" is view-independent** (mean |surface-normal · probe-axis| over the ROI), unlike the §6 camera-incidence heatmap, because a dock bar can't depend on the live camera and the server has no camera. Range = proximity-to-mesh-origin sensor; shape = mean triangle quality. All in the same ROI the probe already samples (no new round-trip).
- **`AnchorSource` DU kept intact** (only `AnchorAuto` now constructed in-app, but `label`/`tag`/`ofTag` + RegJson round-trip tests still reference all cases).
- Contact-line highlight (`cursorHighlight`/`CursorHighlight`) kept on **3D hover only** (its chart-driven branch was removed) — not part of the violin/detail thread the spec targeted.

### Needs browser verification
Dock raincloud SVG sizing/interaction, B4 bars, the `DistBrush`-removed mesh shader, and that the dock never visually collides with rail/focus/overlays on hi-dpi.
