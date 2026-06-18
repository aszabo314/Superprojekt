# Session-2 Implementation Notes

Living log for `ScanPin_session2_implementation_spec.md`. Per-item status at the bottom.
Status values: **done** / **reconciled** (built, but differs from the literal spec — reason given) /
**deferred** / **not-reproduced** (for bugs).

## WP0 — verified code map (names confirmed against current build `68cadf5`)

- **Hover probe path**: server `MeshProbe.sampleAlongAxis` + `run` (re-centres to ref median);
  client trigger is the Ctrl+click `HoverProbeAt` message (View.fs canvas handler) →
  `Query.probe` → `HoverProbeResult : ProbeState`; one global slot `Model.HoverProbe`; the
  tooltip is the `ridgelineJs` violin in `mini` mode (`CardsPin.probeStateJson`/`ridgelineJs`,
  `d.mini`). **No 3D referent today** (A1 target).
- **Pin / correspondence model**: `ScanPin` (plain record, *not* `[<ModelType>]`) in
  `ScanPinModel.fs` carries `Correspondence : Correspondence option`. `Correspondence`
  (`RegistrationModel.fs`) = `{ Enabled; RefAnchor; RefDistance; Anchors : Map<mesh,MeshAnchor>;
  Residuals }`; `MeshAnchor = { Point; Source; Accepted }`. Terms in surface text: "landmark",
  "anchor", "correspondence", "pin" — to be renamed (F11).
- **Registration panel**: `GuiWorkflow.workflowPanel`, 4 `collapsibleSection`s — Meshes
  (`meshRow`, ★=`SetReferenceMesh`), Correspondence pins (`corrRows`/`pinRow`, dots, ⟳ re-open
  review, ⊘ exclude, ▤ open card), Registration status (pending banner, `diagList` with ▶/→
  `NavTo`, **fine-mode `compactButtonBar`** [Traditional ICP / Region-restricted], history),
  Error stats (RMS table, `aggregateLine`, **median-offset strip** `medStripJs`/`medStripJson`,
  posts to `.wfp-medstrip-bus`).
- **Pin card**: `CardsPin.pinCardBody` → `probeStateJson`/`probeRidgeJson`/`ridgelineJs`
  (violin, ±LoD band via `lod = 1.96·√(refstd²+std²)`, in-band median already drawn muted +
  "n.s." but unlabelled band — B1), `probeBarJs` (three-source bar), NUM `pc-rms-table`,
  Correspondence section (`compactToggle "Use as registration landmark"`, per-moving-mesh rows
  `showWhen isMoving` → ref row hidden = F10, ⊕ `StartAnchorPick`, ▦ patch / ✂ cutaway / 📏
  ruler).
- **Auto-seed + review modal**: `Update.seedAnchors` (parallel `/query/closest`) →
  `AnchorsSeeded(refUpdates, candidates)`; candidates open `AnchorReviewing` modal
  (`GuiCards.anchorReviewCard`, `SetAnchorDecision`/`ApplyAnchorReview`/`CancelAnchorReview`);
  also an **auto-cutaway during review** in View.fs (lines 144-149).
- **Patch picker**: `PatchPickerState`, `/query/patch` triangles, canvas JS in `pinCardBody`
  (`hcol` magenta→blue ramp at ~line 849; footprint not clipped to circle = F16; default zoom
  fits full box = F18; texture load can yield black cell = F15).
- **Cutaway**: `ToggleCutaway`/`SetCutawayMode`/`ClipPlane`/`ClipMode`; View.fs builds the
  clip plane (`camCutaway`); the live-marker-protection (F12) is NOT present.
- **Solve wiring confirmed**: `RunCoarse→SolveCoarse→Query.lsqPairs` (Umeyama, NOT ICP — F23 is
  not a wiring bug); fine `RunFine→RunRegistration→Query.icp`. Readiness gates fine on
  `HasCommittedStep` already (good for C1).

## Reconciliation decisions (judgement calls vs the literal spec)

1. **`MeshAnchor.Accepted` is removed entirely** (cleanest "delete the accept/reject flow").
   A stored anchor *is* an applied correspondence marker. `ReadinessPin.Accepted` (the set) is
   kept but now means "meshes that have a marker." JSON `"acc"` is dropped on write and ignored
   on read (v2 back-compat).
2. **`Enabled` flag kept internally** as the registration-pin switch: a pin is a *registration
   pin* iff `Correspondence` is `Some` **and** `Enabled`. The toggle text becomes "Make this a
   registration pin"; demote = `Enabled=false` (markers retained for cheap re-promote). This is
   the low-churn way to satisfy "registration-pin ⟺ has-correspondence" without rewriting every
   `c.Enabled` guard; behaviour at the surface is identical.
3. **`NavAction.OpenAnchorReview` → `ReseedCorrespondence`** (re-projects markers, no modal).
4. **`AnchorsSeeded` payload** changes from `candidates : AnchorCandidate[]` to
   `seeded : (ScanPinId*string*V3d)[]` — markers apply immediately, no modal/decision pass.
5. Internal identifiers that never surface (e.g. `AnchorSource`, `StartAnchorPick`) are left
   as-is; only user-visible strings are renamed (matches the verification grep).

## Build / test commands
- Client build: `dotnet build src/Superprojekt/Superprojekt.fsproj`
- Adaptify (after Model.fs edits): `./adaptify.sh`
- Tests: `dotnet run --project src/Supertests`

## Per-item status
(updated as WPs land)

| Item | Status | Note |
|------|--------|------|
| WP0 reconnaissance | done | this file |
| §0 cull: accept/reject flow | done | `MeshAnchor.Accepted` removed; a stored marker is applied |
| §0 cull: anchor-review modal | done | `AnchorReview*` types/state/messages/card all removed; seeds apply immediately |
| §0 rename (F11) | done | surface text: "landmark"/"anchor" → "registration pin"/"correspondence marker"; coarse mode label "landmarks"→"correspondence" |
| §0 registration-pin ⟺ correspondence | reconciled | kept internal `Enabled` flag as the switch (decision #2); demote via ⊘ |
| C1 legible staging | done | "Stage 1 · Correspondence alignment" / "Stage 2 · Fine ICP (optional)" labels; coarse Ready text + history relabelled; fine-mode toggle hidden until a Stage-1 commit (`hasCommitted`) + one-line note |
| C2 default-apply, no gate | done | landed in WP1 (no modal) |
| C2 F10 editable reference marker | done | ⊕ on the reference row → `StartAnchorPick` on the reference mesh; `AnchorPickHit` moves `RefAnchor` when the picked mesh is the reference |
| C3 requirements early | done | first registration pin of the session auto-opens the panel (`requirementsSurfaced` flag) + inline hint in the pin card |
| B1 LoD = verdict | done | labelled band legend ("±LoD₉₅ detection limit") + per-mesh plain-language verdict line (significant / within noise n.s.) below the chart |
| B2 strip readable | done | x-axis label "signed median offset (m)" + legend line; dot hover now also pulses the pin's rings in 3D (SetWorkflowPinHover); dot click already selects the pin |
| B3 residual vs significance | done | caption: "Band = change significance. Alignment quality is the RMS residual in the Registration panel." |
| A1 hover probe 3D body | done (needs browser check) | `HoverProbeState.Radius` added; `ScanPinScene.hoverProbeBody` draws an equator ring (probe radius) + short axis line (local normal) at the hit point in the accent colour; cleared by the existing cascade. Line geometry only — no shader change, but render not visually verified here |
| D-F8 ⊕ pick toggle | done | both ⊕ buttons (per-mesh + reference) toggle the live pick + reflect active state (btn-active); re-click emits CancelAnchorPick |
| D-F12 cutaway clips markers | reconciled (needs browser check) | cut plane is pushed camera-ward of the nearest marker by the pin radius along the shader's camera-facing normal, so markers + their immediate surface stay on the revealed side. Plane-placement fix (not a per-fragment cylinder protect) — simpler, no shader change. Removed the now-dead `camCutaway` helper |
| D-F15 black patch | done | atlas `Image` gets `onerror` + a 0-dimension guard → falls back to shaded-height, never a black cell |
| D-F16 partial footprint | done | the surface is clipped to the pin circle and the uncovered area is hatched (`#f1f5f9` + diagonals) so partial overlap reads as "no coverage here", with the circle outline on top |
| D-F5 non-local samples | done | violin flags a distribution whose surface sits >0.6·half-length down the axis from the pin centre (`RefOffset + median`) as `far` — amber "far · n=…" badge |
| E-F17 perceptual colormap | done | patch `hcol` is now viridis (5-stop) instead of magenta→blue |
| E-F18 patch zoom-to-fit | done | first view zooms so the farthest sampled vertex reaches the circle edge (per-cell `views[id]` seed) |
| E-F14 patch picker prominence | done | inline hint + accent-bordered "▦ Pick in patches" button when a pin is a registration pin |
| E-F2 hover mini chart | done | numeric median ± half-IQR per moving mesh under the mini violin; y-scale already auto (XAuto). Largely subsumed by A1 |
| A4 pick guide | done (needs browser check) | during a 3D marker pick, the reference marker's normal is drawn as a guide line (+ small cross) — the predicted landing is where it meets the target mesh |
| A4 live landing marker / ridge emphasis | deferred | needs a per-cursor-move raycast (landing point) and local curvature (ridge emphasis); the guide line already shows the intersection visually |
| A2 signed-distance surface color map | done (NEEDS BROWSER CHECK) | user chose the canonical shader paint. New server endpoint `POST /api/query/region-distance` returns per-vertex signed M3C2 distance (cloud-to-mesh, signed by ref normal) in the served vertex order → aligns with the client buffer by construction. Client: `Model.SurfaceDistOn` + `SurfaceDistance` map; lazy debounced fetch (`ensureSurfaceDistance`, generation-guarded); `SurfaceDist` vertex attribute + `DistanceEncoding`/`DistLoD`/`DistScale` uniforms; diverging blue↔red colormap centred at 0 with ±LoD→neutral in `MeshShader.shade` (float32-clean). Per-selected-mesh only (soloed = chart-sticky column). Toggle = "⬢ 3D map" in the pin-card chart head. **Shader render + endpoint must be verified in a browser.** |
| A3 violin pick → 3D ruler + range brush | NOT STARTED — build on verified A2 | ruler (F4) extends the existing elevation cursor; range brush (F7) can reuse A2's `SurfaceDist` attribute (shader emphasis of the brushed interval). Deferred so A2's shader is verified before stacking more unverifiable shader work |
</content>
</invoke>
