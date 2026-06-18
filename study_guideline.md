# Next Demo Study — Guideline (GUI-grounded)

Scope: one moderated, small-n session. Goal = validate the abstraction + the new
error-interpretation and 3D-sectioning elements, not summative proof. GUI references are to
commit `3d6a69e`. Element justifications double as the "why this exists" the facilitator can
probe and the paper can cite.

## Before the session
- **Level/scenario:** formative, validates encoding/idiom + abstraction [Munzner 2009];
  scenario = UWP + insight [Lam et al. 2012]. No domain-validation claims from it.
- **Fix blockers first:** ICP small-reference (D1) and host-aware pins (D2) — else you
  measure the defect, not the design.
- **≥2 datasets of differing geometry** (Mars + synthetic terrain), **≥3 participants** if
  reachable, so dataset effects separate from tool effects.
- **Run as Full app** (not study mode) so nothing is gated — you are testing discoverability
  of the real surfaces.

## Walkthrough — elements touched, and why each exists

**Step 1 · Orient & pick the reference.** Participant orbits (left-drag), frames meshes
(**⌖** per mesh row), and sets one mesh ★ in the **left panel Meshes list** or the
**Registration panel ▸ Meshes** section. *Why ★ exists:* registration needs a fixed gauge —
all error is reference-relative (no ground truth), so the reference is a first-class,
single-select choice, mirrored in both surfaces so it's reachable wherever the user is.
*Probe unaided:* do they find ★ without prompting? (Step-1 contamination last time — U11.)

**Step 2 · Inspect disagreement.** Participant opens the **Registration panel** (**⚲**
top-bar) and reads **§5.4 Error stats**: the RMS table, and the **median-offset-across-pins
strip**. *Why the strip exists:* it makes the H-A vs H-B decision visual — a flat row across
pins ⇒ rigid/datum offset (alignment error is correlated across an epoch [James et al. 2017]),
a varying row ⇒ spatially-varying real change [Lague 2013]. *Why the ±LoD95 band on the
strip:* significance must be read against noise, not by eye [Wheaton 2010]. Free-roam error
reading also uses **Ctrl+click hover probe** (one-shot M3C2 readout). *Probe:* do they read
the strip's flat-vs-varying meaning unaided?

**Step 3 · Place landmarks.** Participant enters placement (**○ Pin** top-bar), clicks
surfaces, tunes the **Adjust Anchor flyout** (inner-radius slider, X/Y/Z fields), **✓
Commit**. *Why the radius/influence sphere exists:* a ScanPin is a *region* probe, not a point
— the radius defines the M3C2 cylinder and the local error neighbourhood, which is the unit
the whole interpretation is built on. *Why placement is a distinct mode with its own flyout:*
separates "where" from "how big," and the isolation auto-suspend (gear **Isolate pins**)
keeps placement from fighting the ghosting (U2 fix). *Probe:* is the influence zone understood
as a region, not a click target (U4)?

**Step 4 · Make them landmarks & resolve anchors.** In the **pin card §6.3 Correspondence**,
participant flips **Use as registration landmark**; auto-seed fires the **Anchor review
modal** (accept/reject per pin×mesh by projection Δ). For bad ones they use **⊕ pick in 3D**,
**▦ Pick in patches** (small-multiples), or **Shift-click a violin column** (axial). *Why
auto-seed + review exists:* closest-point projection is ICP's own correspondence heuristic
[Besl & McKay 1992]; suggest-then-verify keeps the human in control without manual picking in
the good case. *Why three picking fallbacks:* direct 3D picking on overlapping meshes is
occluded/ambiguous; the patch picker moves picking into 2D where nothing overlaps. *Why the
landmark/anchor/pin glossary (hover the §6.3 title) exists:* the terminology confusion was a
real finding (U5). *Probe:* do they grasp one-landmark-many-anchors unaided?

**Step 4b · See the anchors in 3D (NEW).** During review and after, participant uses **✂
Cutaway** (pin card §6.3 — slices through the anchors facing the camera, ghost/hide submode)
and **📏 Rulers** (labels each anchor↔reference distance). *Why cutaway exists:* the four
occlusion requests collapse to one importance-driven, view-dependent cross-section — anchors
stay visible as the camera orbits [Diepstraten 2003; Viola 2004; Burns & Finkelstein 2008];
the cut contains the anchors' principal axis, matching a geological cross-section. *Why the
ruler exists:* it's the explicit-encoding companion to the chart — the pre-alignment gap is
literally measured, and shrinks to the residual after solving. *Probe:* is cutaway discovered
and used to resolve occlusion (the headline new feature)?

**Step 5 · Read the error per landmark.** In the **pin card §6.2 Distance probe**:
participant reads the **violin** — median = bias, width = precision/roughness (shared scale),
**±LoD95 band** = significance, count badge, bimodal = two surfaces. They hover (elevation
cursor + 3D disk), and may **⊟ slice** (clip above the iso-plane), **Alt-click** to lock the
plane. They read the **three-source bar** (Dataset/Algorithm/Conditioning). *Why the LoD band
exists:* converts significance from eyeballing to an explicit verdict (the confirm/refute
signal) [Lague 2013]. *Why the small-sample strip fallback exists:* KDE fabricates shape below
~20 samples, so a sparse cylinder shows raw dots, not a smooth lie. *Why three-source split
exists:* separates "noisy sensor" (Dataset) from "bad alignment" (Algorithm, correlated across
the mesh) from "weak local geometry" (Conditioning) — the three things a disagreement can mean.
*Probe:* do they attribute disagreement to the right source, and read in-band medians as n.s.?

**Step 6 · Solve, preview, commit.** In **Registration panel §5.3**: there are **no Solve
buttons** — participant acts on the **Diagnostics list** (**▶** on "Ready for coarse solve").
Result is a **pending preview** (banner + **⇄ Hold: before**), reviewed via the split violins
(**§6.2**), the **Diff heatmap** (left panel), and the shrinking rulers, then **✓ Commit**.
Then **Fine ICP mode** (Traditional / Region-restricted) → **▶** → preview → commit. *Why
diagnostics-not-buttons exists:* the readiness engine states exactly what's missing and
navigates to the fix, so the user can't run an under-constrained solve and can't get lost.
*Why preview-before-commit exists:* registration is destructive to interpret; showing
before/after first caught the ICP divergence last session and prevents silent bad commits.
*Why two ICP modes exist:* region-restricted weights the fit toward pins when the reference
covers only part of the scene (the small-reference regime, D1). *Probe:* do they find the
ICP-mode toggle unaided (U8)?

**Step 7 · Explore alternatives & decide.** Participant runs the other ICP mode, compares via
preview/diff, and uses **History ↩ rollback** / **↺ Reset** to restore the better one. *Why
history/rollback exists:* the persistence + provenance contribution — registrations are
tractable and reversible, so alternatives can be explored without losing work. *Probe (NEW,
refutation-first):* before showing the post-solve evidence, ask what pattern would *refute*
their hypothesis; then check the LoD verdict for it.

## During the session — conduct
- **Refutation-first, not confirm-first.** Elicit the refuting pattern before post-solve
  evidence; use the LoD band/verdict as the explicit confirm/refute signal — no eyeballing.
- **Quarantine the facilitator** on discoverability items (★ reference, ICP-mode toggle,
  cutaway, isolation-vs-placement, peek **👁/R**). No prompts; **log every prompt** given. A
  prompted step is contaminated for usability claims.
- **Think-aloud + full capture:** audio + interaction log + the workspace/registration-log
  artifact (Save via gear) [Ragan et al. 2016].

## What to probe specifically this round
1. The **four hypotheses** read off the displays unaided (flat strip H-A / varying strip H-B /
   violin width H-C / in-LoD-band H-D).
2. **LoD band** understood as significance.
3. **Sectioning** (✂ Cutaway, 👁 Peek, ⊟ slice) discovered and used to resolve occlusion.
4. **Diagnostics-driven solving** understood (no Solve button) and not experienced as a dead
   end.

## After the session
- **Insight count + coded transcripts** [North 2006; Saraiya et al. 2005], not impressions.
- **Qualitative rigor:** audit trail of codes; **member-check** interpretations with the
  participant — this is what upgrades the facilitator's working model of the violin into a
  defensible finding (closes OQ-1).
- **Report per nested level + pitfalls avoided** [Sedlmair, Meyer & Munzner 2012]; feed into
  the powered, pre-registered broad study (FULL vs NUM) next.

**One-line rule:** moderated for *insight*, unmoderated-within-session for *discoverability*,
refute before you confirm, fix defects before you test.
