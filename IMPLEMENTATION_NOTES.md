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

---

## Focus panel v3 — large single + co-oriented small multiples (`ScanPin_v3_focus_panel_spec.md`)

Built the deferred §1/§6 small-multiples item, unified with the large single into one persistent right-docked panel (`GuiFocus.fs`). Context follows the workflow step — **pick** (step 2) vs **compare** (step 4) — layout is invariant.

### Server
- New `MeshPreview.fs` + `POST /api/query/mesh-preview` (`meshPreviewHandler`). Projects a downsampled mesh (≤`maxTris`, default 6k, uniform stride, re-indexed like the patch sampler) into a shared 2D frame and tags every vertex with a per-channel display scalar. Projections `Pano|Top|Front|Side`; channels `Shade|M3C2|Zdiff|Incidence|Range|Shape`; origin Own/Reference. Pano drops ±π seam-crossing triangles. Returns `{ verts2d, tris, scalar, lo, hi }`. Verified end-to-end on Hessigheim across every channel/projection.
- **Sensor/eye = `transform·centroid`, not `transform·0`.** Positions are stored relative to the centroid, so the mesh's local origin maps to `transform·centroid`; using `0` put the sensor at the far UTM origin and collapsed Range/Incidence to a constant. This now matches the GPU single's render-space sensor (`fullTrafo·0`).
- Picking reuses the existing Embree `/api/query/ray` (no new endpoint).

### Client
- **Large single = the one secondary WebGL control** (`FocusView.fs`). Renders ONLY the focused mesh, reusing `MeshShader.shade` (so every channel — textured / extrinsic diverging / intrinsic incidence·range·shape / shaded — comes for free) with a new **`FocusProject.vertex`** shader that branches `FocusPano`: pano = cylindrical (azimuth→x, elevation→y, radial→depth) from a world eye; ortho = standard MVP. `FocusView.cam` is shared with `GuiFocus` so the surface-pick inverts the same projection.
- **Small multiples = pure 2D canvas** (`observedRender` island + `multiplesJs`), one cell per visible mesh, triangle-filled and coloured by the per-vertex server scalar (shaded-relief in pick, scalar ramp in compare with a **shared colour scale** + LoD-neutral band for extrinsic). Click a cell → `SetFocusMesh` → promotes to the single + sets the inspector mesh.
- `Model`: `FocusAxis`→**`FocusProjection`** (+`ProjPano`, default), **`FocusMesh`**, **`FocusMaps : Map<mesh, FocusPreview>`**. Removed `AlignMesh` (folded into `FocusMesh`). Messages: `SetFocusProjection`, `SetFocusMesh`, `FocusMapsComputed`, `PickCorrespondenceAt`; removed `SetFocusAxis`/`SetAlignMesh`.
- `ensureFocusMaps` postlude — one debounced per-generation fan-out (mirrors `ensureVariance`); invalidates on projection / channel / context / reference / transform / visibility. `Query.meshPreview` wrapper.
- **Step-2 surface pick** (`pickAt` → `PickCorrespondenceAt`): inverts the focus projection to a render ray → mesh-own frame → `/query/ray` → maps the hit back through `effectiveWorld`; the handler ROI-constrains it to the pin's probe cylinder and writes an `AnchorPick3D` marker. Translate-align drag kept (ortho only).

### Decisions / deviations (flagged)
- **GPU panorama is a cylindrical vertex projection, not the revived cubemap `PanoramaShader`.** Lighter (no FBO/readback — satisfies "no GPU readback in the default path"), and identical to the server's per-vertex projection so the single and its multiple cell agree. The spec's "reuse the removed shader" was guidance toward a GPU panorama; this meets the mandate more consistently.
- **"Solo in the main viewport" softened to the existing step-2 isolation.** A hard `MeshSolo` would hide the reference and break every reference-relative channel (probe / extrinsic map / variance). `SetFocusMesh` sets `FocusMesh`+`InspectorMesh`; the main view follows softly via the StepCoarse `wheelIsolation`. The multiples use a restore-aware visible set so a manual solo still shows every cell.
- **Incidence on the single diverges from the multiples in compare.** The single reuses `MeshShader` incidence (camera = pano eye = reference origin in compare), the multiples use sensor incidence. Range/Shape/Shade/extrinsic are consistent. Niche channel in a niche context.
- GPU pano has the classic ±π seam smear on the single (the canvas multiples drop seam triangles server-side).

### Needs browser verification
`FocusProject.vertex` (FShade compiles only in-browser — pano `atan2`, attribute pass-through into `MeshShader.shade`), the canvas multiples grid (layout/fit/colour/click-to-promote), the surface-pick ray math, and that the taller focus panel doesn't collide with the inspector dock / rail on hi-dpi.

---

## Per-step GUI + correspondence workflow (`ScanPin_v3_correspondence_workflow_spec.md`)

Implemented all sections A–I; client + server + 58/58 tests green; server data paths (closest-point, mesh-preview) smoke-tested.

### Model / messages (§B,§H)
- `WorkflowStep`: split `StepCoarse` → **`StepManualMove` + `StepCorrespondences`** (six steps). `AlignMesh` was already folded into `FocusMesh`.
- `Correspondence.InRoi : Map<string,bool>` (round-tripped in `RegJson` + test).
- New: `SetCorrRowHover`, `ReseedMesh`, `SetFocusPeekReference`; fields `CorrRowHover`, `FocusPeekReference`. Removed `FocusOpen`/`ToggleFocusPanel` (panel is always present — no open/close) and the focus-reopen button.
- Pruned vestigial dead code: `WorkflowPanelOpen`, `ToggleWorkflowPanel`, `requirementsSurfaced` (set-but-never-read), the dead `.workflow-panel`/`.wfp-*` CSS (removed GuiWorkflow), and the `AnchorSource` variants `AnchorPatch2D`/`AnchorViolinAxial` (+ `label`/`tag`/`ofTag`/RegJson/test usage).

### ROI membership (§C)
`seedAnchors` is now **ROI-clamped**: a mesh is in-ROI iff its closest point is within the probe cylinder's reach (`roiReach = √(InnerRadius² + (fixedProbeLength/2)²)`); out-of-ROI meshes aren't seeded and stale auto markers are dropped. `Correspondence.InRoi` records membership; `k/n` counts in-ROI meshes only. Added `reseedOneMesh` (forces re-seed of one mesh, the ⟳).

### 3D constellation (§D — `ScanPinScene.fs`)
Replaced the non-pickable line-tetra `anchorGlyphs` with a constellation: per in-ROI moving mesh a small filled **sphere glyph** (palette colour) at the marker, a larger **amber haloed** reference glyph, and thin lines to the reference. Selected pin bright / others dim; depth-test off (renders on top). Glyphs are a stable (pin×mesh) ASet with adaptive trafo/colour (no churn on hover/preview) and are pickable: hover → `SetCorrRowHover`, tap → `SetFocusMesh`.

### Focus 2D editor (§E — `GuiFocus.fs` / `FocusView.fs`)
- **Handles + reference crosshair** drawn in the large single (HTML overlay, projected via `projectToFocus` = the same pano/ortho as `FocusProject.vertex`) **and** in every canvas cell (projected in the cell's own server frame). Crosshair opaque; handle in mesh colour; the manager-hovered mesh's handle pulses (accent).
- **Editing** is press/click-drag-release on the large-single surface → server raycast → `PickCorrespondenceAt` (ROI-constrained) → marker + a "Correspondence placed" toast. Click a cell → promote to the large single.
- **Peek-reference**: hold the `⇄ ref` button → `FocusView.buildSingle` renders the **reference geometry through the focused mesh's own camera** (new `camMeshA` decoupling geom from cam) — juxtaposition, no transparency.

### Correspondence manager + per-step dock (§A,§F)
- `GuiInspector.dock` is now **step-contextual**: a mode header (`WorkflowStep.mode`) + six cross-faded modes (CSS opacity, absolutely-positioned — containers never move). Manual move / Inspect = the raincloud error inspector (B1–B4); Correspondences = the **manager**; Reference / Fine ICP / Commit = light readouts.
- **Manager**: reference row + per-moving-mesh rows (swatch · state `✓`/`○`/`⊘` · residual-or-pre-solve-spread mm · `⟳` re-seed · `✎` edit), out-of-ROI greyed, `k/n` + **Solve coarse** (moved off the rail). No source column.

### Linked highlighting (§G)
- Pin-row hover (rail) → `SetWorkflowPinHover` → brightens the pin's constellation (the previously-dead reader now has an emitter).
- Manager-row hover → `SetCorrRowHover` → ghost-isolates that mesh in 3D (`View.wheelIsolation`, StepCorrespondences) + brightens its 3D glyph + pulses its 2D handle; cleared on row-leave and on terrain pointer-move.
- Bidirectional: tapping a 3D glyph / clicking a 2D cell → `SetFocusMesh`/`SetInspectorMesh` (manager row goes active).

### Readiness engine revived (§H,§I)
Rather than delete the test-covered `Readiness.compute`/`NavAction`/`NavTo`, **revived** them: the Correspondences rail step renders the coarse diagnostics (blocker/warning/ready) with one-click `NavTo` actions; stale `NavTo` pulse selectors (`.pc-corr`, `.left-panel`) repointed to `.pin-inspector`/`.rail-mesh-list`.

### Decisions / deviations (flagged)
- **Constellation glyphs are small spheres, not literal billboards** — view-independent and pickable without per-frame billboard math; reads as point glyphs from any angle. Sizes (`InnerRadius·0.3`, floor 0.08 render) need a browser pass.
- **"Draggable handle" = press-drag-release placement** (marker jumps to the release point) + a live display handle, not a live-follow HTML drag — the overlay can't get rc-relative coords mid-drag cleanly. Functionally equivalent (click/drag to re-place); the existing surface click-pick does the raycast.
- **Cell handles are display-only**; editing happens on the large single (click a cell → promote, then place). Matches "click a multiple cell → promote for editing".

### Needs browser verification
The dock cross-fade + 6 modes, the manager interactions, the constellation sphere sizes/picking, the focus-overlay handle/crosshair projection accuracy (pano + ortho), peek-reference, and all the brushing links — all GPU/DOM, verifiable only in-browser.
