# Superprojekt — registration client handoff

Consolidated current-state reference for the ScanPin registration workflow client
(the "v7/v3" overhaul line). This is the single hand-off doc; detailed
chronology lives in git history + commit messages. `CLAUDE.md` holds the
durable architecture/style rules; `README.md` is the app overview.

**Status:** client (WASM), server, and Supertests all build green; **58/58 tests
pass**. All GPU shaders and DOM/canvas islands are build-verified only — FShade
compiles and the canvas/SVG islands run **only in-browser**, so anything touching
them needs a load of `http://localhost:5000` to confirm.

## Build / verify

- **Client** (fast typecheck, native off): `dotnet build src/Superprojekt/Superprojekt.fsproj -p:WasmBuildNative=false` (~35 s)
- **Server**: `dotnet build src/Superserver/Superserver.fsproj`
- **Tests**: `dotnet run --project src/Supertests` → `58/58 passed`
- **Adaptify** after editing any `[<ModelType>]` file (Model.fs, ScanPinModel.fs, RegistrationModel.fs, CameraModel.fs): `bash adaptify.sh`. **Never hand-edit `*.g.fs`.**
- **FShade rule (bit us):** shaders are float32-only **and** must contain no
  lambdas/local functions. A `let f x = …` *inside* a `fragment`/`vertex` body
  compiles in F# but FShade rejects it as an unsupported lambda — inline it.
  `OutlineView.build` is invoked unconditionally, so its shader compiles at
  startup regardless of the outline toggle.

## What the app is now

Single forward pass; **≤2 live WebGL controls** (main viewport + the focus
panel's large single). Four invariant UI containers — they never move/resize/
appear/disappear on step change; only their *content* and *emphasis* change.

- **Top bar** (`GuiTopBar.fs`): hamburger · reset · hold-Peek · ⚙ dark debug
  popover (dataset, rendering mode, ghost/shading params, outline toggle,
  centroids, debug log).
- **Left rail** (`GuiRail.fs`): six-step vertical stepper —
  **Reference · Manual move · Correspondences · Fine ICP · Inspect · Commit** —
  one expanded at a time, per-step readiness pill, + a pins list (place / select /
  ⚲ promote-to-correspondence / ✎ edit / ✕ delete). The Correspondences step also
  renders the revived **readiness diagnostics** (blocker/warning/ready with
  one-click `NavTo` actions).
- **Focus panel** (`GuiFocus.fs` / `FocusView.fs`): always present. A GPU **large
  single** (the second WebGL control) over a **2D-canvas small-multiples** strip,
  driven by one projection selector **Panorama | Top | Front | Side** (default
  Panorama). Context follows the step: *pick* (textured, own-origin) vs *compare*
  (Inspect → active §6 channel, reference-origin, shared colour scale). Server
  `POST /api/query/mesh-preview` (`MeshPreview.fs`) projects each mesh + a
  per-vertex channel scalar; `ensureFocusMaps` is the debounced postlude.
- **Bottom dock** (`GuiInspector.fs`): always present, **step-contextual**
  (mode-name header + six cross-faded modes). Manual move / Inspect = the
  raincloud error inspector; Correspondences = the **correspondence manager**;
  Reference / Fine ICP / Commit = light readouts.
- **3D scene** (`ScanPinScene.fs`): pin dots, pin influence rings + contact
  rings, far-view pin glyphs (verdict colour + magnitude), movement layer
  (arrows / warped grid, preview only), and the **correspondence constellation**
  (per-mesh sphere glyph + haloed reference glyph + lines, pickable, selected pin
  emphasized, out-of-ROI omitted).

## Registration / correspondence workflow

- **Pins**: one primitive (`ScanPin`); `InnerRadius` = ROI (log-slider); ⚲ promotes
  to a registration pin (`Correspondence.Enabled`).
- **Auto-seed** (`UpdateHelpers.seedAnchors`): ROI-clamped closest-point per moving
  mesh (reach = probe-cylinder bounding sphere). `Correspondence.InRoi` records
  membership → each moving mesh is **placed / placeable / out-of-ROI**; `k/n`
  counts in-ROI meshes only. `⟳` re-seeds one mesh (`ReseedMesh`).
- **Manual edit** (Correspondences step): click/press-drag-release the focus large
  single → server raycast → `PickCorrespondenceAt` (ROI-constrained); the handle +
  an opaque reference crosshair are drawn in the large single and every cell;
  hold **⇄ ref** to peek the reference through the focused mesh's own camera.
- **Brushing** (`SetWorkflowPinHover` / `SetCorrRowHover`): pin-row hover brightens
  the constellation; manager-row hover ghost-isolates that mesh + brightens its
  glyph + pulses its 2D handle; clicking a 3D glyph / 2D cell selects it.
- **Solve**: `Solve coarse` (lives in the manager) → `/api/query/lsq-pairs`;
  optional `Fine ICP` → `/api/query/icp`; everything lands in `PendingReg`
  (preview), then **single commit** (no history) — `Commit`/`Discard`.
- **Heatmaps** (§6, `MeshShaders.fs`): intrinsic incidence/range/shape; extrinsic
  M3C2 ↔ Δz (`region-distance` Mode 0/1); all-meshes variance on the reference.

## Decisions / deviations worth knowing

- **§3 data model not renamed** — existing types map to the spec (Mesh = the
  `Mesh*` maps + `Registration.ReferenceMesh`; Pin = `ScanPin`; Solve = `LastSolve`;
  Preview = `PendingReg`). Cosmetic-only rename skipped.
- **GPU panorama is a cylindrical vertex projection** (`FocusProject.vertex`), not
  the old cubemap shader — lighter, no readback, and pixel-consistent with the
  server's canvas projection.
- **Constellation glyphs are small spheres**, not literal billboards (view-
  independent + pickable without per-frame billboard math).
- **Handle "drag" = press-drag-release placement** + a live display handle (the
  HTML overlay can't get rc-relative coords mid-drag); functionally a re-place via
  the surface raycast. Cell handles are display-only (click a cell → promote).
- **Incidence on the focus single diverges from the multiples in compare only**
  (single = camera/eye incidence via the reused mesh shader; multiples = own-sensor
  incidence). Range/shape/shade/extrinsic are consistent.
- **Readiness/NavTo revived** (not deleted) — they're test-covered and now feed the
  Correspondences-step diagnostics.

## Needs an in-browser pass

The dock cross-fade + six modes, the constellation (sphere sizes / picking), the
focus-overlay handle+crosshair projection (pano + ortho), peek-reference, the
canvas multiples, and the brushing links — all GPU/DOM, unverifiable headless.
Server data paths (closest-point, mesh-preview, region-distance) are smoke-tested.

## Deferred / known gaps

- **Pin-glyph literal near-in-3D zoom** — the attentive view is the dock raincloud
  instead.
- **CLAUDE.md describes several removed subsystems** (Fusion / Panorama / Study /
  Lasso / floating cards / old error model) — left per the standing
  "don't churn CLAUDE.md per change" convention; treat the sections above as
  authoritative for the current build.
