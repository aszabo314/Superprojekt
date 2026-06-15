# Implementation Notes — Post-Study Iteration (ScanPin_iteration_implementation_spec.md)

Living document. WP0 = path map; each WP appends its outcome at the bottom.

## Baseline (before WP1)
- `dotnet run --project src/Supertests` → **138/138 passed**.
- `dotnet build src/Supertests/Supertests.fsproj` → clean.

## Path map (WP0)

### Mesh shaders / uniforms
- `src/Superprojekt/MeshShaders.fs`
  - `RenderPass` (passMinusOne/Zero/One/Two), `MeshShader` (`[<ReflectedDefinition>]`).
  - `UniformScope` members are the full uniform list (Lasso*, Blob*, Heatmap*, Diff*, Cursor*). **Add clip-plane uniforms here.**
  - `MeshShader.shade` — the forward mesh fragment shader. Ghost rules, α-gated depth (`depth = if alpha>=opaqueThreshold then v.fc.Z else 1.0`). **Clip test goes near the top of `shade`, before/with the ghost logic.**
  - `MaxLassoPlanes`/`MaxBlobs` = 32; `opaqueThreshold` = 0.99f.
  - **CRITICAL**: FShade must be float32-only (V3f/V2f, `3.14f`, `: float32` uniforms). fp64 only fails in-browser, not in `dotnet build`/fshadeaot.
  - `FusionShader.shade`, `PanoramaShader.shade` also live here.
- `src/Superprojekt/MeshView.fs`
  - `buildScene` builds the per-mesh `surface` sg + `committedGhost` sg. **Every uniform must be set on BOTH** (the committedGhost duplicates the whole uniform block). Clip uniforms must be added to both.
  - `pinBlobUniforms` (metric→render conversion for blobs), `cursorRender` (cursor plane uniforms metric→render). **Model for clip-plane metric→render conversion.**
  - `CursorHighlight` record (Origin/Normal/Clip/PinCentre/PinRadius/CylLength) defined here, built in View.fs.
  - `effectiveMeshT` / `committedMeshT` — pose selection (committed ∘ pending delta). **WP7 before/after swap toggles between these.**
  - `meshTrafo` — base (mesh-local→render) * registration trafo (postfix).
  - `buildFusionNode`, `buildPanoramaNode`.

### Scene graph composition
- `src/Superprojekt/SceneGraph.fs` — `build` threads view/proj/fullscreen/placementHover/patchHover/cursorHighlight/wheelIsolation into MeshView + ScanPinScene. `originIndicator`/`originLabels`/`referenceOutline` (always-on-top passOne). **New overlays (ruler, cutaway gizmo, candidate anchors) thread through here → ScanPinScene.**
- `src/Superprojekt/View.fs` — `View.view`: the renderControl, all pointer/key handlers, view-local cvals (`spaceHeld`, `altHeld`, `hoverCoord`, `patchHover`, `cursorScreen`, `placementHover`). `cursorHighlight` aval built here (chart cursor wins over 3D hover). `wheelIsolation`. **Reference-peek hotkey/hold + clip-plane aval composition land here.** `OnKeyDown`/`OnKeyUp` handle Alt/Space/Escape.

### ScanPinScene overlays
- `src/Superprojekt/ScanPinScene.fs` — `build` returns `ASet.unionMany [pinDots; pinRings; pinLines; pinPatchRings; ghostPreview; cursorPlane; anchorGlyphs; patchLink; studyFlags]`.
  - `pinDots` — clickable markers (SelectPin/FocusPin).
  - `pinRings` — equator ring + axis indicator + contact rings + falloff outline (AdjustingPin).
  - `anchorGlyphs` — accepted-anchor wireframe tetras + line to RefAnchor, follows preview deltas via `worldDeltaOf`. **WP6 ruler + WP16 candidate-anchor glyphs extend this pattern.**
  - `cursorPlane` — chart-hover elevation disk ⊥ probe axis (`ChartCursor`). **WP4 iso-plane lock reuses this.**
  - Line rendering via `Lines.render` of `(V3d*V3d*V4d*float)[]` (start,end,colour,width). Memoization pattern: `AVal.custom` + ref cache + reference-equality cut (see `patchLink.tickCache`, `studyFlags.flagCache`).
  - Disk geometry: `diskPos`/`diskIdx`. Sphere outline: `PinGeometry.buildSphereOutline`.

### PCA util
- `src/Superprojekt/RegistrationModel.fs` → `RegConditioning.spreadEigenvalues : (V3d*float)[] -> float[]` (weighted covariance + Jacobi eigen, descending). **Use this for WP3 anchor-PCA axisMax** (need the eigenvector for the largest eigenvalue — `spreadEigenvalues` returns values only; will add an eigenvector helper or compute axisMax directly).
- Server probe normal: `src/Superserver/MeshProbe.fs` `eigenvectorFor` + `estimateNormal` (PCA normal of ref vertices in sphere). Used by `probe`.

### Probe pipeline (MeshProbe → DTO → card)
- Server: `src/Superserver/MeshProbe.fs` — `ProbeMeshInput`, `estimateNormal`, `sampleAlongAxis`, `probe` (returns `Normal`, `Planarity`, distributions, `Sources`). `src/Superserver/QueryHandlers.fs` probe handler. **WP8/WP11 add sigma/sampleCount/lod95 to the DTO here.**
- Client DTO: `src/Superprojekt/ProbeModel.fs` — `ProbeDistribution` (Count/Median/Q1/Q3/Std/Kde/Bandwidth), `ProbeSources`/`ProbeSourcesPerMesh` (DatasetError/AlgorithmResid/LocalConditioning), `ProbeResult`. **Add per-mesh sigma/lod95/sampleCount fields.**
- Client wrapper: `src/Superprojekt/Query.fs` (probe request/parse).
- Lazy/debounced: `src/Superprojekt/ScanPinUpdate.fs` `ensureProbe` postlude (250 ms).

### Violin renderer + three-source bar
- `src/Superprojekt/CardsPin.fs` — `ridgelineJs` (the `observedRender "data-ridge"` JS), `pinCardBody`. Violin gated by `StudyGate.featureOn model "violinChart"`; three-source bar by `"threeSourceBar"`. Sources via `r.Sources`. NUM condition replaces violin with RMS table (`showWhenNot violinOn`). **WP8 LoD band + WP9 strip + WP12 labels edit `ridgelineJs` + the data-ridge JSON payload.**
- Hover-probe tooltip reuses `ridgelineJs` with `d.mini`.

### Anchor review modal
- `src/Superprojekt/GuiCards.fs` — `anchorReviewCard` (CSS-centered modal). State: `Model.AnchorReview : AnchorReviewState` (`AnchorReviewIdle|Seeding|AnchorReviewing of AnchorCandidate[]`), `AnchorReviewFilter`. **WP16 renders these candidates in 3D + auto-activates cutaway.**
- Auto-seed flow in `src/Superprojekt/ScanPinUpdate.fs` (closest-point projection).

### ICP handler
- Server solver: `src/Superserver/MeshIcp.fs` `runIcp` (Gauss-Newton recentred, 3× median gating, anchorWeights option, regionEps). **WP13 adds small-reference detection + region restriction + divergence guard here.**
- HTTP: `src/Superserver/QueryHandlers.fs` `/api/query/icp`. Client: `src/Superprojekt/Query.fs`. Reducer/preview: coarse+fine in `src/Superprojekt/Update.fs` / `ScanPinUpdate.fs`; `PendingReg` types in `RegistrationModel.fs`.
- ICP mode: `RegistrationMode` (`TraditionalIcp|RegionRestrictedIcp`) in `Model.fs`; `RegistrationState.Mode`. **WP15 surfaces this as a labelled control (registration card — `GuiCards.registrationCard`).**

### Camera state
- `src/Superprojekt/CameraModel.fs` / `.g.fs` (`OrbitState`), `OrbitController.fs` (`OrbitMessage`, `SetTargetCenter`). View/proj built in View.fs (`model.Camera.view |> CameraView.viewTrafo`; 90° hfov perspective). **WP3 cameraRelative cutaway needs the live view direction — derive from `view` aval (camera forward = -view.Backward.Z or from camera location→target).**

### Model + Messages + Update
- `src/Superprojekt/Model.fs` — top-level `Model` record + `Model.initial`. `[<ModelType>]` → regenerate `.g.fs` via `adaptify.sh` after edits. `ModelTransforms` (committed/effective render+world). **Add `ClipPlanes`/`ReferencePeekHeld` (+ ruler/cutaway toggles) here.**
- `src/Superprojekt/Messages.fs` — `Message` DU.
- `src/Superprojekt/Update.fs` — main reducer (~1250 lines). `SetChartCursor` at line ~1165. Study gating guard ~line 202.
- `src/Superprojekt/ScanPinUpdate.fs` — pin reducer + `ensureProbe`/`ensureRings` postludes + `bakeAnchors`.

### GUI surfaces
- `GuiTopBar.fs` (top bar buttons + gear popover), `GuiPanels.fs` (left panel + placementFlyout), `GuiCards.fs` (registrationCard, anchorReviewCard, retargetCard, panoramaCard, lassoCard), `GuiWorkflow.fs` (workflow panel — error stats section for WP10), `GuiOverlays.fs` (previewBanner for WP7, toast).
- `wwwroot/style.css` — all styling.

### Adaptify
- After editing `Model.fs`/`ScanPinModel.fs`/`CameraModel.fs`: `./adaptify.sh` (wraps `dotnet adaptify --local --force ./src/Superprojekt/Superprojekt.fsproj`). Never hand-edit `.g.fs`.

### Tests
- `src/Supertests` compiles RegistrationModel/StudyModel/RegMath/StudyConfig/StudyStore. **WP11/WP13 add tests here (the conditioning + ICP math must be reachable from a WASM-free module).** ICP solver currently lives in `MeshIcp.fs` which is NOT in Supertests — to unit-test the divergence guard I either move the pure math to a Supertests-compiled module or test via the integration harness. Decision recorded per-WP.

## Per-WP outcomes

- **WP1** (clip core): `ClipMode`/`ClipPlane` + `ClipPlanes`/`ReferencePeekHeld` in Model.fs (adaptify regen'd). Shader: `ClipPlaneCount/ClipPlane0/1/ClipMode0/1` uniforms; camera-side half (`dot(n,wp)+w>0`) discarded (Hide/SectionCap) or forced to ghost alpha (Ghost), clip-ghost fragments read as uniform silhouette colour and never occlude/pick. `clipUniforms` aval resolved in View.fs (camera-relative normal per frame, metric origin→render), threaded SceneGraph→MeshView onto both surface + committed-ghost. **Deviation:** SectionCap currently discards (flat cap not rendered) — spec explicitly allowed this ("cap optional, behind a flag"). **ClipPlane half-space:** Normal points at the half to REMOVE; for camera-relative the resolver makes it the toward-camera component orthogonal to `Axis` (so the cut reveals what's behind, per A1).
- **WP2** (reference peek): `peekTarget` aval in MeshView weaves into per-mesh `isActive`+`GhostOpacity` (precedence: AnchorPick > peek > wheelIso > chartHighlight > silhouette). Spring-loaded top-bar "👁 Peek" button (`pointerCapture`) + hold-R hotkey (`SetReferencePeek`); both no-op without a reference / in study mode. No eye-state mutation. Hovered-mesh target (config flag) deferred — reference-only for now.

- **WP3** (anchor cutaway): `RegConditioning.dominantAxis` (power-iteration PCA, WASM-free + Supertest). `View.fs cutawayPlane` derives a live camera-relative section through the selected pin's accepted anchors (axisMax in-plane, origin = nearest anchor to camera, normal recomputed per frame). Pin-card "✂ Cutaway" + ghost/hide. `CutawayActive`/`CutawayMode` model fields.
- **WP4** (iso-plane): `ClipAboveIso` toggle ("⊟ slice", probe head) clips above the live chart iso-plane while hovering; Alt-click the violin → `lock|d` → `LockIsoPlane` appends a static SectionCap plane to `ClipPlanes` (alt-click same spot releases, Esc clears). Slate ring gizmo per locked plane (`clipGizmos`). `clipUniforms` composes cutaway + locked/live iso (cap 2).
- **WP5** (focus box): `FocusAnchors` toggles cutaway + a locked top iso-plane at the pin centre = a corner. Pin-card "⊡ Focus". No new shader mechanism.
- **WP6** (ruler): `RulerActive` + `GuiOverlays.rulerOverlay` HTML labels at each accepted anchor↔reference midpoint (live gap = residual once a solve shrinks it; title = endpoints + gap/residual). **Perf note:** the label AList rebuilds per frame during camera motion (few labels only) — acceptable for the inspection use; could split structure/placement if it grows.
- **WP7** (before/after): view-local `previewSwap` cval threaded SceneGraph→MeshView; held → committed pose + slate ghost suppressed. Hold button in the preview banner. No model mutation.
- **WP8** (LoD band): per-mesh `lod95 = 1.96·√(σref²+σmesh²)` from existing per-distribution `Std` (refstd + per-row std added to chart JSON). Band shaded around 0; in-band medians → muted dashed + "n.s.". **Note:** split-preview violins draw the band but n.s. styling is applied to the committed-only median (preview half keeps its Δ arrow).
- **WP9** (small-N strip): server probe returns per-mesh raw `Samples` (only < 40 pts); client DTO + parse; violin draws a jittered strip < 20 samples (`SMALL_N`).
- **WP10** (median strip): workflow error-stats sub-view (`medStripJs` + `medStripJson`): row per moving mesh, mark per probed pin at its signed median, shared x-scale, per-mesh ±LoD band; hover→`SetChartHoverMesh`, click→`SelectPin` via `.wfp-medstrip-bus`.
- **WP11** (conditioning): probe `LocalConditioning` = `radius · (1 − λmin/λmax)` of the reference neighbourhood covariance (geometric observability). `RegMath.observabilityDeficiency` shares the formula, Supertested (planar high / isotropic ~0 / empty default).
- **WP12** (labelling): violin legend caption + title; three-source bar segment labels spell out the sources; anchor-review Δ labelled "pre-alignment distance, not residual".
- **WP13** (fine-ICP robustness): `MeshIcp.runIcp : Result`. Small-reference regime (`refDiag/movDiag < 0.4`) restricts moving samples to the reference world region (+margin). Divergence guard aborts when the **centroid displacement** (`|t|`, the recentered translation) exceeds `max 100 (3·(refDiag+movDiag))` — **fix during WP20:** the first guard tested `|tWorld|`, which is inflated by `(I−R)c` for any rotation about a far-from-origin centroid and wrongly aborted the legitimate Hessigheim solve; switched to `|t|` (actual centroid motion). Handler returns `{ok:false,reason}`; client surfaces it as `FineFailed`. **Verified live:** legitimate solve converges (integration 19/19), 5000 m no-overlap offset aborts with `ok:false`.
- **WP14** (host-aware pins): `bakeAnchors` re-bases a plain pin's centre by its host mesh's world delta (correspondence pins stay static in the reference frame); `reanchorCards` re-derives pin-card 3D anchors. Fixes floating pins; round-trips via workspace (centre persisted). **Deviation:** the change is instant (synced with the mesh's own instant commit) — the spec's "animate" is deferred (no mesh-pose animation exists to sync to).
- **WP15** (ICP discoverability): registration card mode helptext + small-reference amber suggestion (client bbox ratio matching the server threshold) with one-click switch.
- **WP16** (review candidates in 3D): `reviewCandidates` overlay draws hollow tetra glyphs (amber/green/red by decision) + connectors during `AnchorReviewing`; cutaway auto-activates over all candidate points.
- **WP17** (isolation auto-suspend): `anchorGhost` uniform forced off during `AnchorPlacement` (no mutation); gear toggle reflects the hold + is inert during it.
- **WP18** (pin reposition): `ScanPinMessage.RepositionPin` + X/Y/Z metre fields in the placement flyout (commit on change). **Deviation:** numeric fields chosen over a 3D drag handle (deterministic, no drag plumbing / cursor-jank).
- **WP19** (terminology): standardized landmark/anchor/pin glossary caption + fresh-pin hint. **Deviation:** always-available caption rather than a one-time dismissable block (simpler, no extra model state).

## Verification (WP20)
- `dotnet build` server+client: **0 errors** (55 pre-existing FShade warnings, unrelated).
- `dotnet run --project src/Supertests`: **143/143** (added: dominantAxis ×2, observabilityDeficiency ×3).
- `node tools/integration.mjs` (live server): **19/19** — lsq-pairs, ICP converges (12 iters, RMS 4.29→3.40), probe with new samples/conditioning DTO, patch picker.
- ICP abort path: 5000 m no-overlap offset → `{ok:false,"insufficient overlap…"}` (no flung pose).
- **Gap:** the in-browser shader compile check (the only way fp64 in FShade surfaces) could **not** be run — the local `/tmp/superrepro` puppeteer install is incomplete (empty `lib/`). Mitigation: the WP1 clip-shader additions are float32-only by construction (V4f / int uniforms, `0.0f`/`0.10f`/`opaqueThreshold` literals, `v.wp.XYZ` is V3f — no `float`/`V3d`/`Constant.Pi`), and `dotnet build` + fshadeaot pass. Recommend a quick manual load (meshes must render; toggle Cutaway/Focus to exercise `ClipPlaneCount>0`).

## Conflicts / deviations from spec
(recorded as encountered)
- WP1: SectionCap renders as plain discard (no flat cap pass) — permitted by spec.
- WP2: reference-peek targets the reference only (hovered-mesh config flag deferred).
- WP13: divergence metric is centroid displacement |t|, not |tWorld| (see WP13 note); gate widened to 3·(refDiag+movDiag) so only absurd motions abort.
- WP14: instant re-base, not animated.
- WP18: numeric fields, not a 3D drag handle.
- WP19: always-available glossary, not one-time.
