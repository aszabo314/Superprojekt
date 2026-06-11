# Ensemble Registration Workflow — implementation notes

Working notes for `ScanPin_registration_implementation_spec.md`. Updated per work package.

## WP0 — repo map (verified 2026-06-11)

| Concern | Location |
|---|---|
| Client Model root | `src/Superprojekt/Model.fs` (`[<ModelType>] Model`), adaptify output `Model.g.fs` |
| Pin types + payloads | `src/Superprojekt/ScanPinModel.fs` (`ScanPin`, `PayloadType`, `PointPayload`, `ContactRingState`, card types) |
| Probe DTOs | `src/Superprojekt/ProbeModel.fs` |
| Messages | `src/Superprojekt/Messages.fs` (`Message`, `ScanPinMessage`, `CardMessage`) |
| Reducer | `src/Superprojekt/Update.fs` (`Update.updateCore` + `ensureProbe`/`ensureRings` postludes in `ScanPinUpdate.fs`) |
| Registration card / retarget modal / panorama card | `src/Superprojekt/GuiCards.fs` (`registrationCard` is opened by a local `cval` toggle button, not a Message) |
| Mesh panel / placement flyout | `src/Superprojekt/GuiPanels.fs` |
| Pin card body + violin chart | `src/Superprojekt/CardsPin.fs` (`ridgelineJs`, `probeRidgeJson`); card chrome `Cards.fs` |
| Mesh shader (ghost rules, provenance heatmap, cursor band) | `src/Superprojekt/MeshShaders.fs` (`MeshShader.shade`); uniforms set in `MeshView.buildScene` |
| Ghost pass | not a separate pass — per-fragment α path in `MeshShader.shade` (`MeshActive=false` → uniform ghost) |
| Scene composition | `src/Superprojekt/SceneGraph.fs`; pin 3D visuals `ScanPinScene.fs`; pixel-constant lines `LineShader.fs` (`Lines.render`) |
| Workspace (de)serialization | `src/Superprojekt/Persistence.fs` (hand-rolled JSON, `"version":1`) |
| Server query handlers + routing | `src/Superserver/QueryHandlers.fs`, `Handlers.fs` |
| ICP | `src/Superserver/MeshIcp.fs` (`runIcp` returns the FULL transform incl. initial, not a delta) |
| Patch / isoline / contact rings | `src/Superserver/MeshAnalysis.fs` (`patch` fits plane from BVH-candidate triangle normals; geodesic Dijkstra footprint) |
| Probe | `src/Superserver/MeshProbe.fs` (`ProbeArgs.Transform` = **world-space** per-mesh transform; client passes `RigidTransform.renderToWorld(MeshTransforms[m]).Forward`) |
| Mesh/atlas fetch | client `MeshData.fs` (atlas URL `…/mesh/{name}/{i}/atlas`); server `MeshLoader.fs`/`MeshCache.fs` |

Key conventions confirmed:

- `Trafo3d` composition is **postfix**: `a * b` applies `a` first (checked against Aardvark.Base 5.3.23 XML docs + usage like `Trafo3d.Scale r * Trafo3d.Translation c`).
- `MeshTransforms : Map<string, Trafo3d>` stores **render-space** trafos; world ↔ render via `RigidTransform.worldToRender/renderToWorld` (conjugation by the centroid-translate + dataset-scale map). All server queries reconstruct world transforms through `renderToWorld`.
- **Found while reading (pre-existing bug):** `MeshView.meshTrafo` composes `meshTransform * base` (registration trafo applied to mesh-local coords *before* the centroid/scale base). For dataset scale 1 and small ICP rotations the error is invisible, but for scaled datasets the rendered translation shrinks by `scale` and large landmark rotations would render inconsistently with every query path. Fixed to `base * meshTransform` in WP6 (one place, used by mesh/fusion/panorama builders).
- No pre-existing test projects anywhere in the solution. WP13 adds `src/Supertests` (console runner, paket-managed, no new packages).
- Local datasets exist (`src/Superserver/data`: Hessigheim, SETSM_glacier, VictoriaCrater) → integration script is feasible. Port 5000 is shadowed by macOS AirPlay; run the server with `ASPNETCORE_URLS=http://localhost:8002`.

## Spec ↔ codebase reconciliations (running list)

1. **`pendingDelta : Map<MeshName, M44d>`** → implemented as `PendingReg : PendingRegistration option` whose `Results : Map<string, PendingMeshResult>` carry the render-space delta `Trafo3d` *plus* the per-mesh rms before/after, convergence, collinearity flag and per-pair residuals that §5/§8.5 need anyway. "pendingDelta non-empty" ⇔ `Results` non-empty.
2. **Delta storage space**: deltas stored render-space (same convention as `MeshTransforms`); effective preview pose = `committed * delta` (postfix). Conjugation preserves composition, so this is exactly the spec's `pendingDelta[m] * committed[m]` in world space.
3. **Anchors are world-space at current committed pose** (spec). Therefore commit/rollback re-bases anchors of moved meshes by the world delta (`bake`/`unbake` in Update). Anchor *picking* is disabled while a preview is pending so an anchor can never be captured at a preview pose and then double-transformed on commit.
4. **Correspondence record** also stores `RefAnchor`/`RefDistance` (projection of pin centre onto the reference) — the spec derives it ad hoc, but solve/visuals/patch-picker all need it.
5. **Types live in `RegistrationModel.fs`** (new file before `ScanPinModel.fs`), with `ScanPinId` moved there, so the pure registration state machine is compilable outside the WASM project for unit tests.
6. **Anchor decisions** use a dedicated `AnchorDecision` DU rather than reusing `RetargetDecision` (defined later in compile order).
7. WP5 "each pin row gets an exclude toggle" — implemented in both plausible places: registration-card coarse readiness list (⊘ per enabled pin) and the pin card correspondence section header toggle.
8. **`ProvenanceHeatmap : bool` → `HeatmapMode` DU** (`HeatOff | HeatProvenance | HeatDiff`) with `HeatmapPrev` for the auto-revert on commit/discard. Old workspaces (`provHeatmap` bool) still load.
9. **Patch picker "textured by default"**: patch points get per-vertex atlas UVs (server addition, backwards-compatible); the card JS samples the mesh atlas through an offscreen canvas. "Shaded" toggle = height colormap (the pre-existing patch rendering), since true hillshade needs a mesh, not a point set.
10. **Anchor glyphs**: rendered as wireframe tetrahedra through the existing pixel-constant `Lines.render` (one adaptive node for all glyphs+links) instead of filled meshes — keeps GPU resource churn at zero when anchors change, per the CLAUDE.md adaptive-performance rules.
11. **Reference outline**: subtle render-space bbox outline (12 edges, accent colour) instead of a screen-space silhouette — the WebGL stack here has no stencil/post pass and CLAUDE.md forbids the fragile paths.
12. **Fine solve rms**: `rmsBefore` = first ICP convergence entry, `rmsAfter` = RMS of final residuals (same numbers the old flow produced).
13. **Reset button** now means "roll back all steps" (spec §8.4); identity transforms restored step by step so anchor un-baking stays exact. The old hard `ResetMeshTransforms` semantics remain reachable only through workspace load.
14. **WP10 violin axial pick**: gated on correspondence enabled; uses the *effective* probe (preview probe while pending, else committed).

## Per-WP status

- **WP0** ✅ this file.
- **WP2** ✅ `RegistrationModel.fs` (new), `ScanPinModel.fs` (PointPayload.Correspondence, ScanPin.ProbePreview), `Model.fs` (PendingReg, RegistrationLog, HeatmapMode/Prev, AnchorReview, AnchorPick, PatchPicker), adaptify regenerated, `Persistence.fs` version 2 (pins.correspondence + registrationLog; old files load with defaults).
- **WP3** ✅ `RegMath.fs` (weighted Umeyama, Jacobi eigen, conditioning), `/api/query/lsq-pairs` (400 on <3 pairs), patch `frameNormal`/`frameRefDir` override + per-point `uv`; client `Query.lsqPairs`, `Query.patchInFrame`.
- **WP1** ✅ ★ per mesh row (two-way with registration card), tooltip, reference bbox outline, reference-change cascade (probes + pending cleared + re-seed).
- **WP4** ✅ ToggleCorrespondence → parallel closest-point seeding → review modal (per pin×mesh rows, Δ flags, accept/reject, Apply); re-seed on reference change & retarget commit (Auto/unaccepted only).
- **WP5** ✅ registration card restructure (reference / coarse stage with readiness + conditioning badge + solve / fine stage with warn-once / pending table with sparkline + commit/discard / history), coarse + fine solves land in PendingReg.
- **WP6** ✅ meshTrafo order fix, preview pose, committed-ghost tint render, banner, guards (placement/retarget/fusion/dataset switch), probes+rings under effective transforms, anchor glyphs + links.
- **WP7** ✅ ProbePreview plumbing + split half-violins with Δ-arrow in `ridgelineJs`.
- **WP9** ✅ patch picker (shared frame, transform-aware, atlas-textured small multiples, crosshair, click→ray→Patch2D anchor, toast on miss).
- **WP10** ✅ one-shot 3D pick (shader-level solo, depth-gated, Esc, auto-advance), Shift+click violin axial pick.
- **WP8** ✅ heatmap Diff mode (signed Δ combined error, LoD mask → ghost, diverging blue/red, hover Δ+LoD tooltip, auto-revert).
- **WP11/12** ✅ rollback newest / reset-all with anchor un-baking, collinearity badges, guard tooltips.
- **WP13** ✅ `src/Supertests` console runner (Umeyama: recovery n=3/4/50, det+1 on planar, weight=duplication, collinearity flag, <3-pairs client-guard; reg-log commit/discard/rollback; correspondence/log JSON round-trip incl. missing-fields defaults) + `tools/integration.mjs` HTTP flow. Results below.

## Verification results (2026-06-11)

- **Builds**: `dotnet build Superprojekt.sln` — 0 errors (56 warnings, all pre-existing: FusionView `GetOutputView` deprecations + FShade AOT infos).
- **Unit tests** (`dotnet run --project src/Supertests`): **38/38 passed**. Umeyama recovers random rigid transforms at n=3/4/50 to ≤5e-15 residual (UTM-offset variant ≤3e-9), reflection-prone mirrored sets yield det(R)=+1 to 1e-16, weight-2 ≡ duplicated pair to 9e-16, collinear sets flag the warning, <3 pairs → None; RegLog commit/rollback restores transforms and residuals exactly; RegJson round-trips correspondence + a two-step log (coarse and fine inputs) and defaults cleanly on missing fields.
- **Integration** (`node tools/integration.mjs`, server on :8002, Hessigheim): **19/19 passed**. lsq-pairs recovers T⁻¹ to 1.9e-9 m (action-on-points metric — raw matrix entries amplify the ~5e5 m UTM lever arm), residuals ≤1e-9, <3 pairs → HTTP 400, collinear pairs flag the warning; ICP from the corrected transform decreases RMS 4.293 → 3.403 over 12 iterations; the probe's moving-mesh median error vs the unperturbed baseline shrinks 0.580 → 0.000 m after the lsq correction (absolute medians are confounded by genuine inter-epoch change in Hessigheim, hence baseline-relative assertions); patch returns UV-carrying points and echoes the requested frame exactly.
- **In-browser** (puppeteer against the served WASM client, since `dotnet build` cannot catch ESSL3 shader errors): meshes render with the modified `MeshShader` (HeatmapMode + diff branch — 337k mesh pixels, no shader compile errors), registration card shows Stage 1 / Stage 2 / History with "Solve coarse" correctly disabled, mesh-panel ★ toggles and mirrors into the card ("★ Reference: Hess-201803"), heatmap radio shows Off/Sources/Diff.

## Observed pre-existing issues (not in spec scope, untouched)

- `View.resolveFusionPick` raycasts meshes in their **untransformed** frames — fusion picking is slightly wrong for registered meshes (it predates this work; fusion is blocked during previews, so the new workflow never hits it).
- `Update.RegistrationFailed` used to flip `Running = false` on the **first** per-mesh failure even with other solves in flight; the new Expected-countdown replaces that.

---

# User Study Mode — implementation notes

Working notes for `ScanPin_study_mode_implementation_spec.md`. Updated per work package (`SWP<n>` commits).

## SWP0 — repo map (verified 2026-06-11)

| Spec concern | Location |
|---|---|
| App root view / boot | `src/Superprojekt/View.fs` (`View.view` builds the whole DOM in one `body { }`; `App.app` record) + `Program.fs` (`Boot.run gl App.app`). **No router exists** — the server serves `index.html` for every non-API path via `MapFallbackToFile`, so `/s/{token}` already reaches the client; the path is readable via `Window.Location.Path` (verified against Aardworx.WebAssembly 1.2.8: `Location` has `Protocol/Host/HostName/Port/Path/Href/Search` + `GetQuery()`). |
| Top bar | `GuiTopBar.topBar` (hamburger, dataset dropdown, Lasso/Pin/Fusion/Pano buttons, camera reset, coord readout, gear) |
| Gear popover | end of `GuiTopBar.fs` (inside `topBar`, `.tb-gear-popover`, gated by `Model.GearPopoverOpen`) |
| Mesh panel | `GuiPanels.leftPanel` (mesh rows incl. ★ reference toggle, pin list, error metadata, provenance/heatmap radio Off/Sources/Diff) |
| Registration card | `GuiCards.registrationCard` — opened by a **local `cval`** (`registrationOpen` in `View.view`), not a Message; "Set as final" button goes here |
| Pin card + violin chart | `CardsPin.pinCardBody` + `ridgelineJs`/`probeRidgeJson` (violin), three-source stacked bar in the same file; card chrome `Cards.fs` (`cardDragHandle/cardPos/cardStyle`), 3D-anchored card system `Cards.renderCards` |
| Provenance heatmap mode state | `Model.HeatmapMode` (`HeatOff\|HeatProvenance\|HeatDiff` in `RegistrationModel.fs`) + `HeatmapPrev`; radio UI in `GuiPanels.fs`; shader uniform in `MeshView.buildScene` |
| Split-violin preview | `ScanPin.ProbePreview` + `preview` param of `CardsPin.probeRidgeJson` (kde2/median2/q12/q32 rows) |
| Message/update loop | `Messages.fs` (`Message`/`ScanPinMessage`/`CardMessage`) → `Update.updateCore` + postludes (`ensureProbe`/`ensureProbePreview`/`ensureRings`); module-level `CancellationTokenSource` refs for debounce |
| Workspace (de)serializer | `Persistence.fs` (hand-rolled JSON v2, `serialize : Model -> string`, `apply : string -> Model -> Result<Model,string>`) |
| Server routing | `Handlers.webApp` (Giraffe `choose`), JSON binding via `ctx.BindJsonAsync<CLIMutable DTO>` (case-insensitive), responses via `json {\| … \|}`; startup `Program.fs` (CORS, Blazor static files, Giraffe, `MapFallbackToFile "index.html"`) |
| Server data root | `MeshLoader.findDataRoot` walks up from `AppContext.BaseDirectory` looking for `data/` → **`studies/` is resolved the same way as a sibling of `data/`** |
| Datasets on disk | `Hessigheim` (3 epochs), `SETSM_glacier` (9 epochs, scale 0.01), `VictoriaCrater` → glacier-v1 uses tutorial=`Hessigheim`, main=`SETSM_glacier` |
| Tests | `src/Supertests` console runner compiles WASM-free client/server files directly (`RegistrationModel.fs`, `RegMath.fs`); integration `tools/integration.mjs` against :8002 |

New-file plan (compile-order positions):

- Client `StudyModel.fs` (after `RegistrationModel.fs`, before `ScanPinModel.fs`): WASM-free — feature ids, `StudyCondition`, config DTOs + `configPublic` JSON parser, `Predicate` + incremental evaluation state, `StudyState`/runtime types, answer drafts. Compiled into Supertests.
- Client `StudyApi.fs` (after `Query.fs`): HTTP wrappers for `/api/study/*`.
- Client `StudyTelemetry.fs` (after `Messages.fs`): mutable event queue + batcher (5 s / 50 events / immediate flush triggers), throttle state — module-level mutables like the existing CTS refs.
- Client `GuiStudy.fs` (after `GuiCards.fs`): study bar, instruction overlay / anchored tooltip, task pane, question widgets.
- Server `StudyConfig.fs` (before `QueryHandlers.fs`): config + secret parsing, startup validation — WASM/Giraffe-free, compiled into Supertests.
- Server `StudyStore.fs` (after `StudyConfig.fs`): JSONL stores with per-sid locks, balanced assignment, TRE scoring, HMAC completion code — Giraffe-free, compiled into Supertests (store root parameterised for temp-dir tests).
- Server `StudyHandlers.fs` (after `QueryHandlers.fs`): HTTP handlers, wired into `Handlers.webApp`.

## Spec ↔ codebase reconciliations (study mode, running list)

1. **`AppMode = Full | Study of StudySession`** → implemented as `Model.Study : StudyState option` (`None` ⇔ Full). Equivalent semantics; keeps the adaptify model flat and every existing view/update path untouched in Full mode.
2. **Condition DU** named `CondFull | CondNum` (JSON tags `"FULL"`/`"NUM"`) to avoid clashing with the ubiquitous `Full` identifier.
3. **"Phase F"** (§8, transforms post trigger) read as the **exit phase** (P6): question ids letter phases A=P1 … E=P5, F=P6, consistent with §10's "auto-upload workspace at final (P5→P6 transition)" and §3's completion requirement. `final` transforms + workspace upload happen on entering the exit phase; the P4 "Set as final" button posts the same label.
4. **Study bar hosts a tool strip** for gated action buttons (Pin etc.) — real sessions have no top bar, but `pinPlace` is an allowed feature in several phases and the existing button lives in the top bar; the study bar exposes exactly the allowed subset, reusing the existing emit paths.
