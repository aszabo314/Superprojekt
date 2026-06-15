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

## Conflicts / deviations from spec
(recorded as encountered)
