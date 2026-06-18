# Implementation Spec — Session-2 Iteration

Autonomous agent. The app has moved since the notes (build `3d6a69e`); **treat every path,
type, and message name below as a hypothesis to verify against the current code, not as
ground truth.** Reconcile the intent of each item with what you find. Decisions in §0 are
final. Verify per-item. After each WP: clean build, tests green, commit `S2WP<n>: <summary>`.

## WP0 — Read the code first
Locate and record in `IMPLEMENTATION_NOTES.md` (names are from the last known state; confirm
or correct): the hover-probe path (`MeshProbe.sampleAlongAxis`, the Ctrl+click handler, the
hover-probe overlay/tooltip); the pin model + correspondence model (terms "landmark"/
"anchor"/"correspondence"); the Registration panel (`GuiWorkflow.fs workflowPanel`) sections
incl. the median-offset strip and the diagnostics/Fine-mode toggle; the pin card
(`CardsPin.fs pinCardBody`) violin + three-source bar + correspondence section; the anchor
auto-seed + review modal; the patch picker (texture load, footprint clip, zoom default,
colormap `hcol`); the cutaway clip-plane subsystem; the coarse/fine solve wiring
(`RunCoarse→SolveCoarse→Query.lsqPairs`, fine ICP `RunRegistration`). Note anything already
changed so the spec can be reconciled.

## §0 — Culling decisions (apply before building)
The session's throughline: **the 2D analytics are good; the gaps are (a) no 3D encoding of
distance, (b) opaque purpose-built abstractions, (c) illegible registration staging, (d)
terminology + workflow friction.** Cull aggressively toward that.

- **REMOVE the per-anchor accept/reject mechanism** (F19) and **the blocking anchor-review
  modal** (F13). A registration pin simply *has* a correspondence; demoting it to a scanpin
  is the only "reject." Seeded points apply by default; correction = 3D pick. This deletes a
  modal, a per-mesh accept/reject UI, and the `accepted` bool flow — net simplification.
- **DEMOTE Fine/ICP to an explicit, skippable Stage 2** (F22/F24). Do **not** remove it
  (the small-reference regime D1 still wants region-restricted ICP), but the default,
  legible path is **correspondence-only**. The fine-ICP mode toggle must not appear as a
  co-equal control before a coarse solve exists.
- **RENAME** to the participant's member-checked vocabulary (F11): *landmark → registration
  pin*, *anchor → correspondence marker point*, "use as registration landmark" → make a pin
  a *registration pin*. Drop "landmark" and "anchor" from all surface text. Keep two pin
  kinds: **scanpin** (measure only) and **registration pin** (has one correspondence).
- **DO NOT BUILD** anything not traceable to a finding. No new panels beyond what's below.

Citation spine (reuse from prior lit work; verify DOIs at write-up, not here): M3C2 signed
distance / local normal / LoDetection / roughness = Lague, Brodu & Leroux 2013; error
correlated across an epoch / precision maps = James, Robson & Smith 2017; thresholding /
DoD = Wheaton et al. 2010; **brushing-and-linking = Becker & Cleveland 1987**; **smooth
brushing in 3D focus+context = Doleisch & Hauser 2002**; cutaway/importance lineage =
Diepstraten 2003 / Viola 2004 / Burns & Finkelstein 2008; perceptual colormaps = Crameri,
Shephard & Heron 2020 (or viridis/Smith & van der Walt).

---

# PART A — 3D ENCODING OF DISTANCE (the headline: F1/F4/F7/F9) ⭐ most detail

The participant asked twice "what is the plotted value" and synthesized the whole session as
*"more 3D-linked renderings of the good 2D diagrams."* The established answer is two
complementary idioms: **(1)** color-map the signed M3C2 distance onto the surface (the
standard CloudCompare/Lague depiction), and **(2)** brushing-and-linking from the violin into
the 3D scene [Becker & Cleveland 1987; Doleisch & Hauser 2002]. Build both on the existing
probe data; the hard part the participant named himself — N overlapping meshes — is handled
by *per-selected-mesh* rendering, never all-at-once.

## A1 — Hover probe gets a 3D body (F1, Major)
Today Ctrl+click yields only a tooltip with no 3D referent. Give it the **same 3D vocabulary
as a placed pin**, transiently:
- On Ctrl+click, render at the hit point: the **probe cylinder** (reuse the pin region/ring
  geometry, sized to the current probe radius) and the **probe axis (local normal)** as a
  short line. This alone answers "what kind of sample is this."
- Cleared by the existing cascade (Esc / click elsewhere / timeout).
- No persistence, no card — it stays a lightweight spot-check, just legible in 3D.

## A2 — Signed-distance surface encoding, per selected mesh (F1, enhancement)
An opt-in encoding that copes with overlap by showing **one mesh at a time**:
- In the pin card violin (and on a probe), selecting/soloing a mesh's column **color-maps
  that mesh's signed M3C2 distance** (within the probe footprint, or within a lasso/region if
  one exists) onto its surface, 0 = reference, diverging map centered at 0. This is the
  canonical M3C2 depiction [Lague 2013].
- Diverging, perceptually-uniform colormap [Crameri 2020] with the **±LoD95 band mapped to
  the neutral mid-band** so "not significant" reads as "near-neutral colour" — directly
  reinforcing the H-D verdict in 3D.
- Strictly per-selected-mesh; never stack all meshes (the participant's own constraint).

## A3 — Pick a value/range on the violin → 3D (F4 ruler + F7 brushing) ⭐
This is brushing-and-linking [Becker & Cleveland 1987; Doleisch & Hauser 2002], the
principled form of the participant's two requests:
- **Single value (F4):** the existing chart→3D elevation cursor (the "isoline" he liked)
  becomes an explicit **measured ruler** — for the *selected* mesh, draw a ruler from the
  reference surface to the picked signed distance along the probe axis, labelled with the
  metric value. Extends what exists; one mesh at a time avoids N-mesh clutter.
- **Range brush (F7):** brushing a y-interval on the violin **highlights the contributing 3D
  samples** of the selected mesh (the vertices whose signed distance falls in the interval),
  e.g. by emphasis colour, with the rest of that mesh de-emphasised (smooth brushing / focus
  +context, Doleisch & Hauser 2002). Brush ⇄ 3D is bidirectional if cheap; one-way (chart→3D)
  is acceptable for v1.
- Both reuse A2's color/selection machinery; ship A2 first.

## A4 — 3D picking aids (F9, Medium)
During a correspondence-marker 3D pick (`StartAnchorPick`/successor):
- Render the **reference point's normal** and a **landing marker** where that normal meets the
  target mesh (predicted correspondence) — live as the cursor moves.
- Optional **ridge/corner emphasis**: highlight high-curvature vertices in the local region
  (curvature already implicit in normals; compute locally) to aid precise placement. Snapping
  to them is a stretch goal, behind a flag — emphasis alone satisfies the request.

---

# PART B — MAKE THE PURPOSE-BUILT ABSTRACTIONS LEGIBLE (F3, F21, F6)

The concepts were graspable from raw distributions; the *custom* encodings failed. Fix the
encodings, don't add more.

## B1 — LoD band reads as a verdict, not a data interval (F3, Major)
The band was read as "a 95% CI of the distribution" with in-band medians taken as
*confirming* — the reverse of intended. It is a **detection limit** (≈1.96·√(σ_ref²+σ_mesh²)),
and a median inside ⇒ **not significant** [Lague 2013; Wheaton 2010].
- **Label it** on the chart: "detection limit (LoD₉₅)", not an unlabeled grey band.
- **Strengthen the per-median verdict:** in-band medians render unmistakably as **n.s.**
  (muted + explicit "n.s." tag, already partially present — make it unmissable); out-of-band
  = significant style.
- Add a **one-line plain-language verdict** near the chart: e.g. "mesh X offset +0.4 m —
  significant" / "within noise (n.s.)". This is the confirm/refute signal the protocol relies
  on; it must not be left to interpretation.

## B2 — Median-offset strip made readable, or folded in (F21, High)
The H-A/H-B centrepiece was "not understood at all," even though the user had *already* found
the varying offset by hand — the element failed to communicate its own purpose.
- **Label rows** (mesh name/swatch) and **axis** (signed median offset, m), shared scale,
  with the ±LoD band shaded and labelled.
- Add a **one-line legend**: "flat row across pins = uniform offset (alignment); spread =
  varying change."
- **Tie each dot to its pin:** hover/click already links (verify) — make the dot carry the
  pin label and, on hover, pulse the pin in 3D and its violin, so the user connects the strip
  to the probing he already did.
- If after labelling it still reads as redundant with per-pin violins, **consider removing it
  in favour of a small multiples row of the per-pin violins** — but try the labelling fix
  first (cheaper, preserves the H-A/H-B contribution).

## B3 — Near-registered regime: don't let LoD be the only channel (F6, Medium)
Valid critique: once near-registered, medians are ~always n.s., so the binary verdict is
uninformative. The band is *correct* for change detection; the fix is to let the fine signal
come from elsewhere when near zero:
- Keep the band, but ensure the **numeric median / IQR and the RMS-trend** are always present
  and carry the sub-noise signal (registration quality), framed as **"registration residual"**
  distinct from **"change significance."** A short caption distinguishing the two readings
  suffices; no new viz.

---

# PART C — REGISTRATION STAGING & WORKFLOW (F22, F19, F20, F13, F24)

## C1 — Explicit, legible staging (F22, High)
Make the two-stage model self-evident; correspondence-only is the default path.
- Present registration as **Stage 1 — Correspondence alignment** (the coarse landmark/LSQ
  solve, the primary action) and **Stage 2 — Fine ICP (optional)**. Label the coarse action
  "Correspondence alignment," never an unlabeled "coarse."
- The **fine-ICP mode toggle does not appear until a Stage-1 result is committed** (remove the
  co-equal-control confusion). When it appears, one line: "optional refinement; weights toward
  your correspondences (region-restricted) or all overlap (traditional)."
- This + the rename (F11) is the answer to F24: ICP stays, but the workflow *reads* as being
  about correspondences, with ICP clearly secondary/skippable.

## C2 — Default-apply correspondences; no review gate (F13/F19, see §0)
- Enabling a pin as a **registration pin** auto-seeds correspondence marker points and
  **applies them immediately** (no modal, no accept/reject). Correction = 3D pick (A4) or
  patch picker.
- Optional non-blocking affordance: "N points auto-placed — review?" that just opens the pin
  card's correspondence section; never a blocking modal.
- The reference correspondence point becomes **editable** (F10): give the reference mesh a
  pickable marker too, defaulting to the projected pin centre.

## C3 — Surface requirements early (F20, Medium)
- When the **first registration pin** is created, surface the readiness/requirements readout
  immediately — auto-open the Registration panel that first time (once per session), and/or
  show a compact inline "needs ≥3 registration pins per mesh" hint on the pin.

---

# PART D — TARGETED BUG FIXES (verify each repro in current code first)

- **D-F8** ⊕ 3D-pick can't cancel by re-click: make it a **toggle** — if a pick targeting this
  pin+mesh is active, emit cancel; reflect active state on the button. (Esc already works.)
- **D-F12** cutaway clips its own marker points: **protect a tight cylinder around the live
  marker points**; the cut plane slides around the protected geometry. (Participant's own fix.)
- **D-F15** textured patch sometimes black though `*_atlas.jpg` exists: investigate atlas
  load in the patch canvas (timing/CORS/UV); **fall back to shaded-height if the texture
  fails to load** so it's never a black cell.
- **D-F16** patch shows partial geometry, expected a full circle: **clip/fill the footprint to
  the pin circle**, and **distinguish "no coverage" from "not drawn"** (e.g. hatch/empty
  styling) so partial overlap reads as intentional.
- **D-F5** probe samples meshes that look absent: the fixed ±10 m axial cylinder catches
  surfaces offset along the normal. **Shorten the default axial window** and/or **flag samples
  whose median sits ≫ radius from the pin centre as non-local** (de-emphasise + note). Verify
  the current length before changing.

---

# PART E — LOW-PRIORITY POLISH (do only after A–D land)

- **F17** replace the `hcol` magenta→blue patch gradient with a **perceptual colormap**
  (viridis/cividis or a terrain ramp) [Crameri 2020].
- **F18** default each patch cell's zoom to **fit the populated footprint**, not the full box.
- **F14** surface **▦ Pick in patches** more prominently when a pin is a registration pin +
  one-line inline hint.
- **F2** hover-probe mini chart: focus the y-scale on the **populated band** (or zoomable) +
  numeric median/IQR in the tooltip. (Partly subsumed by A1.)

---

# Verification (run yourself)
1. **Cull landed:** no accept/reject UI, no blocking anchor-review modal, no "landmark"/
   "anchor" in surface strings (grep); registration-pin ⟺ has-correspondence; demote-to-scanpin
   path works.
2. **Part A:** hover probe renders cylinder+normal in 3D; per-mesh signed-distance color map
   centers 0 at reference with LoD→neutral mid; violin value→3D ruler shows correct metric
   distance for the selected mesh; range brush highlights the in-interval vertices of that mesh
   only. Test the N-mesh case shows **one** mesh's encoding at a time.
3. **Part B:** LoD band labelled; in-band median ⇒ n.s. styling + plain verdict string;
   median-offset strip has row+axis labels + legend + pin-linked dots.
4. **Part C:** fine-ICP toggle hidden until a Stage-1 commit; coarse action labelled
   "Correspondence alignment"; first registration pin surfaces requirements; reference marker
   editable.
5. **Part D:** each bug has a regression test or a reproduced-then-fixed note; cutaway never
   clips its own markers; patch never renders a black cell (falls back).
6. Build clean; existing tests pass; summarize per-item status (done / reconciled-differently
   / not-reproduced) in `IMPLEMENTATION_NOTES.md`.

# Fixed decisions
Remove accept/reject + review modal; registration-pin ⟺ correspondence; rename per F11; ICP
demoted not removed; fine-mode toggle hidden pre-coarse; 3D distance encoding is
per-selected-mesh only (never all overlapping meshes at once); LoD band is a labelled verdict,
not a data interval; brushing-and-linking and per-mesh color-mapped signed distance are the
two sanctioned 3D idioms; perceptual colormap for patches. Anything not traceable to F1–F24
is out of scope.
