# Implementation Spec — Post-Study Iteration

Autonomous agent. Implement end-to-end; decisions are final. Builds on
the landed registration workflow + workflow panel. **WP0 first:** explore the repo, record
paths in `IMPLEMENTATION_NOTES.md` (mesh shaders/uniforms, `ScanPinScene` overlays:
`anchorGlyphs`/`cursorPlane`/`pinDots`, the chart→3D iso-plane handler, PCA util used for
probe normals, probe pipeline `MeshProbe`→DTO→card, violin renderer, three-source bar,
anchor review modal, ICP handler `MeshIcp.runIcp`/`/api/query/icp`, camera state). Preserve
conventions (world-space queries, 250 ms debounce+cancellation, Elm update loop, depth-gated
picking). After each WP: clean build, tests green, commit `PWP<n>: <summary>`.

Confirmed assumptions (frozen): violin uses **shared density scale** across meshes in a pin;
distance uses **per-mesh local normal**. The interpretation work below depends on both.

---

# PART A — 3D SECTIONING & MEASUREMENT (highest priority)

One clip-plane subsystem; four modes are parameterizations. Importance-driven, view-dependent
cutaway lineage (Diepstraten 2003; Viola 2004; Burns & Finkelstein 2008).

## A1. Clip-plane core (WP1)

Model:
```
type ClipMode = Hide | Ghost | SectionCap
type ClipPlane = { origin : V3d; normal : V3d; mode : ClipMode
                   cameraRelative : bool }      // recompute normal per-frame from view
ui.clipPlanes : ClipPlane list                  // 0..2 active
ui.referencePeekHeld : bool
```
- Mesh shader: add up to 2 clip planes as uniforms (`vec4` plane eqs) + per-plane mode flag.
  Fragment on the clipped side: `Hide` → discard; `Ghost` → force low alpha (reuse ghost
  alpha); `SectionCap` → discard above plane and, if a cap pass is cheap, render a flat
  section-cap colour, else discard only (cap optional, behind a flag).
- Planes apply to **mesh geometry only**; overlays (`anchorGlyphs`, `cursorPlane`, `pinDots`,
  rulers) never clipped.
- Half-space convention: clip the **camera-side** half (`dot(p−origin, normal) > 0` hidden/
  ghosted) so the cut reveals what's behind.

## A2. Mode A — Reference peek (WP2)  *(U3, step 3c)*

- Spring-loaded: a top-bar/mesh-panel-reference-row **hold** button + hotkey. While held,
  set all non-reference meshes to ghost (importance-down); reference stays solid. No plane.
  Release restores prior visibility exactly.
- Implemented via a transient importance override, not by toggling the persistent eye state
  (must not mutate user's visibility settings). Generalize target = reference by default;
  config flag to instead target the hovered mesh (extends the undiscovered Option-hold).

## A3. Mode B — Anchor cutaway (WP3)  *(step 4c, the headline)*  ⭐

For the active pin's accepted anchors (need ≥2; ideal 3):
- **PCA** of the anchor points → `axisMax` (first principal axis = max-spread line). Reuse
  existing PCA util.
- **Plane origin** = anchor nearest the camera.
- **Plane orientation:** the plane **contains** `axisMax`; its normal = normalize(view
  direction − (view·axisMax)axisMax) — i.e. the component of the camera forward orthogonal to
  `axisMax`. This makes the cut run *through* all anchors and face the camera.
- `cameraRelative = true`: recompute normal each frame as the camera orbits, so anchors +
  the surface behind them stay visible throughout (adaptive cutaway). Mode = `Ghost` (default)
  or `Hide` (toggle).
- Entered from the pin card / workflow-panel pin row ("Cutaway") and auto-active during anchor
  review (Part B). Exits on deselect.

## A4. Mode C — Iso-plane section (WP4)  *(step 5)*

Extend the chart→3D iso-plane (`cursorPlane`):
- (i) **Clip above** the iso-plane (`SectionCap`) so the user sees into the meshes, toggled
  from the violin/iso-plane interaction.
- (ii) **Click to lock**: convert the transient hover plane into a persistent `ClipPlane`
  that survives orbit; click again / Esc to release. Locked plane shows a small on-plane gizmo.

## A5. Mode D — Focus box (WP5)  *(step 6a)*

Allow Mode B (front, camera-relative) + Mode C (top, locked) simultaneously = 2 planes → a
clipping box isolating the anchor neighbourhood. No new mechanism; assert the shader supports
2 planes (A1). A single "Focus anchors" affordance on the pin row enables both at once.

## A6. Measurement ruler (WP6)  *(U10, steps 4b/6a)*

- Render each accepted anchor↔reference connector as a **labelled ruler**: line + midpoint
  text = distance. Pre-solve = current pair gap; **post-solve = per-pair residual** (already
  computed) — ruler shrinks to residual length.
- Show both endpoints' values only on hover; default shows one midpoint number. Label billboards
  to camera, depth-tested against overlays not meshes (always legible).
- Toggle on the pin card + workflow-panel pin row.

## A7. Before/after pose swap (WP7)  *(U9, step 6a)*

Hold-to-swap control (pending-preview banner, Part C area): while held, render moving meshes at
**committed** pose; release → preview pose. Pure render-time transform selection; no model
mutation. Pairs with Mode D for anchor before/after inspection.

---

# PART B — ERROR VISUALIZATION & INTERPRETATION (high priority)

Makes the four geological hypotheses legible (H-A rigid offset / H-B real change / H-C
roughness / H-D below-detection). Anchors: Lague 2013 (LoD, local normal, spread), James et
al. 2017 (alignment error correlated across epoch).

## B1. LoD₉₅ band on the violin (WP8)  ⭐ highest-value interpretability gain  *(H-D)*

- Server probe DTO: ensure per-mesh `sigmaData`, `sigmaAlgo`, `sigmaCond` and
  `lod95 = 1.96·sqrt(sigmaRef² + sigmaMesh²)` are returned per pin per mesh (compute from the
  same σ feeding the three-source bar; if absent, add — see B4).
- Violin: render a **shaded band** `[−lod95, +lod95]` around 0 (the reference plane). Median
  ticks **inside** the band render in a muted "not significant" style + a small "n.s." marker;
  outside = significant style. This converts significance from eyeballing to an explicit
  verdict (also the confirm/refute affordance the study needs).

## B2. Small-sample handling (WP9)  *(KDE validity)*

- Probe DTO returns per-mesh `sampleCount`. Violin renders the count as a small badge.
- If `sampleCount < 20`: **do not draw the smooth KDE**; render a **jitter/strip** of the raw
  signed distances + median tick instead (KDE fabricates shape below ~15–20 samples). Threshold
  in one constant.

## B3. Median-across-pins strip (WP10)  *(H-A vs H-B)*

- New compact view (in the workflow panel error-stats section): one **row per moving mesh**,
  one mark per pin at its **signed median offset**, shared x-scale, LoD band shaded.
- Read: a **flat row** (all pins same offset) ⇒ H-A rigid/datum offset (correlated across epoch,
  James 2017); a **varying row** ⇒ H-B spatially-varying real change. Hover a mark ↔ highlights
  that pin in 3D and its violin (reuse chart↔3D linking).

## B4. Conditioning source — verify/wire (WP11)  *(D3, step 8c)*

- Trace `sigmaCond` end to end: probe computes local geometric observability (e.g. from the
  normal-equation conditioning / eigenvalue spread of the local neighbourhood used for the
  pin's normal+solve), through DTO, into the three-source bar.
- If currently unimplemented: implement it; if implemented but ~0: add a test asserting it is
  non-zero for a near-planar (weakly-conditioned) neighbourhood and ~low for a well-shaped one.
  A genuinely ~0 case must be visually distinct from "absent."

## B5. Channel labelling (WP12)  *(OQ-1, in-context)*

- Violin: legend/first-run tooltip — y = signed distance along the **local** normal, 0 =
  reference; width = precision/roughness (shared scale); median = bias; LoD band = significance;
  bimodal = two surfaces, not noise.
- Three-source bar: hover text per segment (dataset = sensor/reconstruction; algorithm =
  registration residual, correlated across the mesh; conditioning = local geometric
  observability).
- Anchor Δ (review/connector): label as "pre-alignment distance (work to do)", explicitly not
  the residual error — prevents the step-4d over-read.

---

# PART C — BLOCKING DEFECTS & SUPPORTING UX

## C1. Fine-ICP small-reference robustness (WP13)  *(D1, step 7/7a)* — blocks the study

Server `MeshIcp.runIcp`:
- Detect small-reference regime: `referenceBBoxDiag ≪ movingBBoxDiag` (ratio threshold).
- (a) **Restrict moving sample points to the reference's bounding region** (+ margin) before
  correspondence search; and/or (b) **sample from the smaller (reference) mesh** and match into
  the mover.
- (c) When pins exist, **auto-prefer region-restricted** (pin-weighted) mode in this regime;
  surface as a suggestion in the card.
- (d) **Per-iteration divergence guard:** if translation magnitude exceeds a multiple of the
  current overlap extent, **abort** and return an "insufficient overlap" error (not a flung
  mesh). Surface as a toast + inline message.
- Instrument: log per-iteration translation/rotation magnitude, surviving correspondence count,
  normal-equation condition number. Test: tiny-reference vs large-mover case converges or aborts
  cleanly, never diverges silently.

## C2. Host-aware pin tracking (WP14)  *(D2, step 8d)* — blocks the study

- Pin centre/marker follows its **host-mesh committed transform** (Option B). For
  **correspondence pins**: centre lives in the **reference frame** (static); per-mesh anchors
  follow their own meshes (`bakeAnchors` already does anchors — fix the **centre**).
- **Animate** the position change on commit (no snap). Pin card 3D anchor (`Cards.projectToScreen`),
  pin cross, rings all driven by the tracked transform. Snap-to-reference rejected as default.

## C3. ICP mode discoverability (WP15)  *(U8, step 7b)*

- Surface Traditional / Region-restricted as an explicit **labelled control** with one-line
  helptext; auto-suggest region-restricted under the C1 small-reference regime.

## C4. Candidate anchors in 3D during review (WP16)  *(U1, step 4a)*

- Render review **candidate** anchors in 3D in a distinct **pending** style (hollow glyph) with
  live connector lines + rulers (A6), during `AnchorReviewing` — not only after Apply. Mode B
  cutaway (A3) auto-active so they're visible despite occluders.

## C5. Isolation auto-suspend during placement (WP17)  *(U2, step 5b)*

- While in `AnchorPlacement`, auto-suspend the "Isolate pins" ghost; restore prior state on
  commit/cancel. The gear toggle reflects the temporary auto-hold.

## C6. Pin reposition during adjust (WP18)  *(U6, step 3b)*

- Drag-to-reposition handle on the pin while in `AdjustingPin`; position + radius both live until
  commit. Optional numeric position fields in the placement flyout.

## C7. Terminology + fresh-pin hint (WP19)  *(U5, U4, steps 2/3)*

- On first correspondence-enable: a one-time micro-glossary — **landmark** = one real spot;
  **anchor** = its mark on each mesh; **pin** = the act of marking (map analogy). Standardize UI
  copy on these three terms (retire "correspondence point" as a synonym in surface text).
- Fresh-pin one-line hint: "this pin marks one spot; enable correspondence to match it across
  meshes" — distinguishes the influence zone from a clickable picking area.

---

# Verification (WP20) — run yourself

1. **Clip core:** plane eqs hide/ghost correct half-space; 2 planes compose (focus box); overlays
   never clipped. **Mode B math:** plane contains `axisMax` and faces camera; normal recomputes
   on camera move; orientation preserved otherwise. **Ruler:** label = residual post-solve.
2. **Error-viz:** LoD band geometry = ±1.96·√(σ²+σ²); median in-band → n.s. style; sampleCount<20
   → strip not KDE; median-across-pins strip values match probe medians; conditioning non-zero on
   near-planar test neighbourhood.
3. **Defects:** ICP tiny-reference case converges or aborts (never silent fly-away); host-aware
   pin centre tracks host/reference transform and round-trips through workspace.
4. **Build clean**; existing tests pass; summarize in `IMPLEMENTATION_NOTES.md`.

# Fixed decisions
Shared violin density scale; per-mesh local normal. One clip subsystem, modes = params; clip
meshes only. Peek = transient importance override, never mutates eye state. Cutaway = PCA-contains-
axis, camera-relative. Ruler shows residual post-solve. LoD₉₅ at 1.96σ; n.s. styling inside band.
KDE→strip below 20 samples. Pin tracking = host-aware Option B, reference-frame centre for
correspondence pins, animated. ICP aborts rather than returns divergent pose.
