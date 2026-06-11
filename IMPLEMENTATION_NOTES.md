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
- **WP13** ✅ `src/Supertests` console runner (Umeyama: recovery n=3/4/50, det+1 on planar, weight=duplication, collinearity flag, <3-pairs client-guard; reg-log commit/discard/rollback; correspondence/log JSON round-trip incl. missing-fields defaults) + `tools/integration.mjs` HTTP flow. Results recorded at the bottom.
