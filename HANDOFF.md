# ScanPin v7 overhaul — handoff

Implementation of `ScanPin_v7_coding_spec.md` ("registration workflow client" rewrite). Done in three phases: **(1) aggressive removal**, **(2) implement the spec**, **(3) final prune**. This document records exactly what was built, the per-spec implementation details, and the changes/decisions made. The running log is `IMPLEMENTATION_NOTES.md`; this is the consolidated reference.

**Status:** Phase 1 complete · Phase 2 complete (one optional view deferred, see §"Not done") · Phase 3 complete. **Client (WASM), server, and Supertests all build green; 86/86 tests pass.** GPU/shader pieces were build-verified here and smoke-tested in-browser by the maintainer as they landed.

---

## 1. How to build / verify

- **Client** (fast typecheck, native off): `dotnet build src/Superprojekt/Superprojekt.fsproj -p:WasmBuildNative=false` (~37 s)
- **Server**: `dotnet build src/Superserver/Superserver.fsproj`
- **Tests**: `dotnet run --project src/Supertests` → `86/86 passed`
- **Adaptify** (after editing any `[<ModelType>]` file — Model.fs, ScanPinModel.fs, RegistrationModel.fs, CameraModel.fs): `bash adaptify.sh`. **Never hand-edit `*.g.fs`.**
- FShade shaders are **float32-only** and only validate in-browser (run the server, load `http://localhost:5000`).

---

## 2. Phase 1 — Removals (everything not in the spec)

All removed with the build kept green between steps. Two big subsystems (Study, Panorama) were **confirmed for removal by the maintainer** since they're neither in the spec nor its §12 REMOVE list.

| Feature | What was removed |
|---|---|
| **Study mode** | client `StudyModel/StudyApi/StudyTelemetry/StudyUpdate/GuiStudy`; server `StudyConfig/StudyStore/StudyHandlers`; `/api/study/*` routes; `studies/` dir; study unit tests. |
| **Panorama** | `PanoramaView.fs`, PanoramaShader, `▦ Pano` toggle, model state, card, synthetic-pose generation. |
| **Fusion** | `FusionView.fs`, FusionShader, `◈` toggle, `FusionMode`, CPU-raycast fusion pick path, `buildFusionNode`. |
| **Save/Load** | `Persistence.fs`, gear Save/Load, the workspace download/upload JS. |
| **Retarget** | `RetargetState/Candidate/Decision`, retarget card, messages + reducer. |
| **Lasso** | `LassoDraft/Volume`, model fields, messages, reducer, lasso card, SVG draw layer, `◌` toggle, shader `LassoPlanes` uniforms. |
| **Registration history** | `RegStep`/`RegStepOutput`/`RegTransformState`, `RegistrationLog`, rollback `↩`, reset `↺`, `★ Set as final`, the cross-pin median-offset strip → **single commit**. |
| **Iso-plane / cutaway / ruler** | `ClipPlane` type, `ClipPlanes`/`CutawayActive`/`ClipAboveIso`/`RulerActive`, `⊟ slice`, `lock\|d`, locked-plane gizmo, `rulerOverlay`. **Reference-peek kept.** |
| **Old D/A/C error model** | `Provenance` module, three-source bar, provenance + diff heatmaps, prov tooltip, dataset-error UI, `MeshDatasetErrors`/`MeshAlgorithmResidual`/`ProvenanceThreshold`/`HeatmapPrev`. **`SensorType`/`MeshSensorTypes` kept** (feed the §6 range channel / step-1 sensor type). |

### Decisions in Phase 1
- **`ServerActions`** (dataset bootstrap `init`/`loadDataset`) was relocated out of the deleted `StudyUpdate.fs` into `UpdateHelpers.fs` (slimmed: no study-token branch).
- Single-commit registration: `CommitRegistration` now applies each pending delta to `MeshTransforms` and re-bases correspondence anchors (`bakeAnchors`), with **no history**. The pending **preview** (`PendingReg`) is intact — that's the spec's `Preview`.
- The coupled history + error-model removal (they share `AlgoResiduals`, the commit machinery, and the heatmap shader) was done as one pass.

---

## 3. Phase 2 — Implementation, per spec section

### §1 Layout / §11 Debug menu — `GuiTopBar.fs`
- Top bar reduced to **hamburger · ⟲ reset · 👁 Peek (hold) · ⚙ debug**. The hamburger toggles the rail (`MenuOpen`, default **true**).
- The **dark gear popover** is the debug menu: dataset switch + rendering mode (Textured/Shaded/Slope) **moved here from the bar/old panel**, alongside camera speed, ghost silhouette+opacity, isolate-pins, shading/slope params, **per-mesh outlines toggle** (§10), centroid/bounds info, debug log.

### §2 Workflow rail — `GuiRail.fs` (new)
- Vertical stepper **1 Reference · 2 Coarse align · 3 Fine ICP · 4 Inspect · 5 Commit**; one step expanded at a time; per-step **readiness pill** (ready/warn/block/info) computed from model state; **PINS list** underneath (place / select / ⚲ promote-to-correspondence / ✎ edit / ✕ delete).
- New model state: `WorkflowStep` (enum) + `Model.WorkflowStep` + `SetWorkflowStep`. A near-pure view: every control dispatches an existing message; it issues no server queries.
- Replaces the old `GuiPanels.leftPanel` + the floating registration panel (`GuiWorkflow.workflowPanel`), both deleted in Phase 3.

### §1/§5 Focus panel = **secondary WebGL control** — `GuiFocus.fs` (new)
- The **second (and only second) live WebGL control**, right-docked, rendering the scene **orthographically in render-space** (reuses `MeshView.buildScene`). **Top / Front / Side** button group, one view at a time.
- Mounted only while open (`FocusOpen`) so the **≤2-WebGL-controls** rule always holds; a reopen tab shows when closed.
- **§5 translate-only coarse align**: pointer drag → in-plane render-space delta → `TranslateAlignMesh` → the selected moving mesh's committed `MeshTransforms` (translate only, no rotation). Moving-mesh selector = visible non-reference meshes.
- New model state: `FocusOpen` / `FocusAxis` (`AxisTop|AxisFront|AxisSide`) / `AlignMesh` + messages `SetFocusAxis`/`ToggleFocusPanel`/`SetAlignMesh`/`TranslateAlignMesh`.

### §3 Data model — mapped, **not renamed** (decision)
The existing model already realises the spec §3 shape under different names, so a record rename was **deliberately skipped** (high churn, no functional gain):
- Mesh = `MeshNames`/`MeshVisible`/`MeshOrder`/`MeshSensorTypes`/`MeshTransforms` + `Registration.ReferenceMesh`
- Pin = `ScanPin` (`Correspondence.Enabled` = isCorrespondence; `InnerRadius` = roiRadius)
- Solve = `LastSolve` · Preview = `PendingReg`

### §4 Pins / ROI — already spec-compliant
`ScanPin` is the single primitive; `InnerRadius` is the ROI with an **inline log-slider** in the placement flyout; tap on the reference sets the centre; auto-seed is closest-point; the probe evaluates inside the ROI cylinder; ⚲ in the rail promotes/demotes the correspondence.

### §6 Heatmaps — **complete** (`MeshShaders.fs`, `MeshView.fs`, `GuiRail.fs`, server)
`HeatmapMode = HeatOff | HeatIncidence | HeatRange | HeatShape`; rail-Inspect group Off/Incidence/Range/Shape + an Extrinsic toggle with an **M3C2 ↔ Δz** switch + a **Variance** toggle.
- **Intrinsic incidence** — shader: `|n·toCam|` → grazing red → head-on green.
- **Intrinsic range** — per-mesh `SensorOrigin` uniform = full mesh trafo · (0,0,0); `RangeMax` = scale × max|local vertex| (`LoadedMesh.localMaxR`, computed at load); shader paints `|wp−SensorOrigin|/RangeMax`. **Sensor = each mesh's own origin** (maintainer calibration), no coefficient. Rigid-invariant.
- **Intrinsic shape** — per-vertex triangle quality `4√3·A/Σl²` (1 equilateral → 0 sliver), incident-face mean, computed at load into a new `ShapeQ` per-vertex attribute (mirrors `SurfaceDist`); shader red (poor) → green (good).
- **Extrinsic M3C2 / Δz** — the kept `DistanceEncoding` diverging map (`SurfaceDistOn`, paints the soloed moving mesh). Server `region-distance` gained a `Mode` field (0 = signed closest-point M3C2; **1 = vertical Δz** via Embree raycast onto the reference; signed: moving-above-reference → positive). Toggled by `ExtrinsicZDiff`.
- **Variance / disagreement (all-meshes)** — `VarianceOn`. Default selection = **all visible moving meshes (≥2)** (no multi-select UI for now — maintainer-approved default). `Update.ensureVariance` postlude does N reference-centric `region-distance` fetches (target = reference, ref = each moving), computes per-reference-vertex **std**, stores under `SurfaceDistance[refMesh]`, painted on the **reference** via a new **sequential** `DistanceEncoding = 2` ramp. Mutually exclusive with the single-mesh extrinsic map.

### §7 Movement layer — `ScanPinScene.fs`
Preview-only. `MovementMode = MovementOff | MovementGlyphs | MovementGrid` (rail-Inspect button group). **Arrows**: 5×5 grid over each committed pin's ROI plane, before→after displacement arrow (chevron head) where after = world preview-delta · before (`RigidTransform.worldDeltaOf`). **Grid**: original faint lattice + warped accent lattice.

### §8 Pin glyph (semantic zoom) — `ScanPinScene.fs`
**Far/preattentive view** (`pinGlyphs`): per committed pin a pole + head-ring (`Lines`); head colour = **verdict** (green if every moving mesh's |median| ≤ LoD₉₅, red if any significant, grey if no probe); pole height ∝ magnitude (max |median offset|). **Near/attentive view = the existing pin card / violin flyout** (opens on select) — not re-rendered as a 3D billboard (decision; the flyout already serves the attentive content per spec).

### §9 Ghost isolation — `View.fs`, `MeshView.fs`, `GuiRail.fs`
Opacity-ghost base kept. **Align-auto**: in step 2 the moving mesh renders solid, all others ghosted (main viewport via `wheelIsolation` + the focus control). **Pin-focus** (`PinFocusMode`, rail toggle): mesh-shader blob uniforms restricted to the focused pin's ROI + isolation forced on → ghost everything outside that one pin. **Movement-auto**: with the movement layer on under a preview, isolate the moved mesh + glyphs.

### §10 Per-mesh outlines (image-space) — `OutlineView.fs` (new), `MeshShaders.fs`, `MeshView.fs`, `SceneGraph.fs`
- **Offscreen MRT G-buffer** (`MeshView.buildOutlineNode` + `OutlineGBuffer` shader): target0 = world normal + window depth, target1 = palette colour + coverage mask (custom attachment `Sym.ofString "Outline1"`). Offscreen-pass API recovered from the removed FusionView.
- **Edge-detect composite** (`OutlineEdge` shader, fullscreen quad): edge = depth jump **OR** normal-angle jump **OR** coverage-mask boundary → paints the per-pixel palette colour, alpha-blended overlay. The **mask boundary** covers silhouette **and the near-plane cut** — satisfying the spec's "image-space, not inverted-hull" requirement.
- **Gated** by `OutlineMode` (debug-menu toggle, **default off**); the offscreen task is lazy, so default-off cannot regress the main forward pass. Thresholds (`0.0015` depth / `0.30` normal) and the normal encoding are tunable in-shader.

### §0 LoD₉₅
The pin-glyph verdict uses the spec form `1.96·√(σ_ref²/n_ref + σ_M²/n_M)` (the `+ reg` term ≈ 0 — no registration-uncertainty input wired). The pin-card violin's existing LoD band was left as-is.

---

## 4. Phase 3 — Final prune
Deleted, build kept green: `GuiWorkflow.fs` and `GuiCards.fs` (entirely dead — no callers); `GuiPanels.fs` reduced to just `placementFlyout`; the `StudyGate` always-on shim removed from `Primitives.fs` (its last users neutralised to always-visible); `CardCharts.probeBarJs` removed.

**Left intentionally:** the generic clip-plane shader plumbing (fed a constant no-clip — removing it means shader edits with in-browser-only verification); `ProbeResult.Sources` (computed server-side, now client-unused — harmless dead data); vestigial `WorkflowPanelOpen`/`ToggleWorkflowPanel` model state (harmless; `WorkflowPinHover` is still used by the pin hover highlight).

---

## 5. New files & key model/message additions

**New files:** `GuiRail.fs` (workflow rail), `GuiFocus.fs` (secondary ortho control), `OutlineView.fs` (outline offscreen pass).

**New model state** (`Model.fs`): `WorkflowStep`, `FocusOpen`/`FocusAxis`/`AlignMesh`, `PinFocusMode`, `MovementLayer` (`MovementMode`), `OutlineMode`, `ExtrinsicZDiff`, `VarianceOn`; `HeatmapMode` extended with `HeatIncidence`/`HeatRange`/`HeatShape`; `MenuOpen` default → true. (`MeshView.LoadedMesh` gained `localMaxR`.)

**New messages** (`Messages.fs`): `SetWorkflowStep`, `SetFocusAxis`/`ToggleFocusPanel`/`SetAlignMesh`/`TranslateAlignMesh`, `TogglePinFocus`, `SetMovementLayer`, `ToggleOutlines`, `ToggleExtrinsicZDiff`, `ToggleVariance`, `VarianceComputed`.

**New shaders** (`MeshShaders.fs`): `OutlineGBuffer` + `OutlineEdge`; `MeshShader` gained `HeatmapMode`/`SensorOrigin`/`RangeMax` uniforms, a `ShapeQ` vertex attribute, and the incidence/range/shape/variance fragment branches.

**Server** (`QueryHandlers.fs`): `RegionDistanceRequest.Mode` + the vertical-Δz raycast branch in `regionDistanceHandler`. Client wrapper `Query.regionDistance` gained a `mode` parameter.

---

## 6. Not done / deferred (with reasons)
- **§1 focus small-multiples** (per-mesh extrinsic miniatures) — the one unbuilt feature. Needs a new canvas renderer (no 3rd WebGL control allowed) + a multi-mesh distance fetch, and is **largely redundant** now that the spatial heatmaps (single-mesh map + variance) are complete. Deferred by agreement.
- **Pin-glyph literal near-in-3D zoom** — served by the flyout instead.
- **§3 record rename** — skipped (cosmetic; model already maps to §3).

## 7. Caveats for the next person
- All GPU pieces were build-verified here; **§10 outlines** is the newest pipeline — if outlines look wrong/absent when toggled on, it's threshold/normal-encoding tuning (in `OutlineEdge`), not structure.
- §6 **range/variance** depend on the server (`region-distance` Mode + the variance fetch) — run the server to exercise them.
- The **§3 "data model not renamed"** decision means spec names (Mesh/Pin/Solve/Preview) map to existing types — see §3 above for the mapping.
