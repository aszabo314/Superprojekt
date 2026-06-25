# Handoff — Correspondence-point management: current state

**Scope.** Everything from *placing a pin* → *getting registration correspondence markers* → *seeing/editing their state* → *feeding them into the coarse solve*. Read-only triage; nothing in this document has been changed in code.

**TL;DR.** Auto-seed works and a numbers-only state readout exists in the bottom dock. There is exactly **one** picker (the focus-2D click, just added). There is **no 3D picker** and **no linked highlighting** anywhere. Two pieces the specs said to keep — the per-mesh re-seed control and the pin-hover→glyph highlight — are present in the model layer but **unreachable dead code** (no UI emits/renders them). The rich "state of every point + pick in 3D + pick in 2D + linked highlighting" workflow you remember was a **pre-v7** surface that later specs deliberately deleted.

---

## 1. Spec history (why the picture is fragmented)

Three specs touch this area and each overrode the last. The current behaviour is the *sum* of all three, not any single one.

| Spec | What it said about correspondences |
|---|---|
| `ScanPin_v7_coding_spec.md` (§2, §4, §8) | Promote a pin (`isCorrespondence`) → **auto-seed** each moving mesh's point by closest-point projection **inside the ROI**; "user can re-pick", constrained to `roiRadius`. "Correspondence picking also available" in step 2. Pin glyph shows a **`k/n` completeness** ring. *No rich per-point panel / linked highlighting was ever specified here.* |
| `ScanPin_v7_pin_inspector_spec.md` (§A, §B3) — *deleted from disk; recovered from git `125128a`* | **Removed**: the object-centred orthographic **detail view**, the **linked-view highlight thread** ("keep *only* the pin-hover highlight on the 3D glyph"), and **both manual pickers** (3D one-shot + patch small-multiples) → auto-seed becomes the only source. **Added**: a **numbers-only** readout (B3) — per moving mesh `swatch · ✓/✗ · residual mm`. |
| `ScanPin_v3_focus_panel_spec.md` (§D step 2) — *just implemented* | **Re-added one picker**: click the focus large-single surface → `PickCorrespondenceAt`, ROI-constrained. Step 4 is passive. |

**The workflow you described** (state of each point shown, pick in 3D *and* in the focus 2D, linked highlighting between them) most closely matches the **pre-v7** implementation (the correspondence detail SVG + patch picker + `SetCorrMarkerHover` thread). The inspector spec deleted all of it on purpose.

---

## 2. Data model (current — `RegistrationModel.fs`)

```
ScanPin.Correspondence : Correspondence option        // None = plain scanpin; Some = registration pin

type Correspondence = {
    Enabled     : bool                                // true ⇒ this pin is a registration pin
    RefAnchor   : V3d option                          // the reference marker (pin centre if host=ref, else closest-pt projection)
    RefDistance : float
    Anchors     : Map<string, MeshAnchor>             // per moving mesh: the correspondence point
    Residuals   : Map<string, float>                  // per-pair residual from the last coarse solve (metres)
}
type MeshAnchor = { Point : V3d; Source : AnchorSource }   // Point = world-space at committed pose
type AnchorSource = AnchorAuto | AnchorPatch2D | AnchorPick3D | AnchorViolinAxial
```

- Markers are **world-space at the committed pose**; commit/rollback re-bases them (`UpdateHelpers.bakeAnchors`).
- A pin is a *registration pin* **iff** `Correspondence.Enabled`. There is one pin primitive — promotion is a flag, not a second type.
- **Only `AnchorAuto` and `AnchorPick3D` are ever constructed now.** `AnchorPatch2D` / `AnchorViolinAxial` are dead variants kept alive solely by `label`/`tag`/`ofTag` + the RegJson round-trip tests.

---

## 3. End-to-end pipeline (what fires when)

```
Place pin (tap reference surface)         → plain scanpin, NO correspondence yet
        │
Promote ⚲ (rail pin row / inspector B1)   → ToggleCorrespondence → Correspondence.empty (Enabled=true)
        │                                    └─ if reference set: seedAnchors [pinId]   (Update.fs:358)
        │
Auto-seed (seedAnchors, UpdateHelpers:149) → parallel /query/closest per moving mesh
        │                                    → AnchorsSeeded → Anchors[mesh] = { Point; AnchorAuto }  (Update.fs:371)
        │
Re-seed triggers: reference change (Update.fs:103), demote+re-promote.
        │          (the dedicated per-mesh ⟳ re-seed is DEAD — see §5)
        │
Manual override: focus-2D click in step 2  → PickCorrespondenceAt → Anchors[mesh] = { Point; AnchorPick3D }
        │                                    (ROI-checked against the probe cylinder, Update.fs:572)
        │
Solve coarse (rail "Solve coarse")         → /query/lsq-pairs over pins with ≥3 markers
                                             → CoarseSolved writes Correspondence.Residuals  (Update.fs:186)
```

**Important nuance:** the original spec said auto-seed must be *constrained to the ROI*. In practice `seedAnchors` does an **un-clamped closest-point** to the whole reference/moving mesh — there is **no radius constraint** on the auto-seed. Only the new **focus-2D pick** enforces an ROI (the probe cylinder: radius `InnerRadius`, length `fixedProbeLength = 20 m`, `Update.fs:577-585`).

---

## 4. What is implemented and reachable

| Capability | State | Location |
|---|---|---|
| Auto-seed on promote + on reference change | ✅ works | `UpdateHelpers.seedAnchors:149`, `Update.fs:103,358` |
| Per-point **state readout** — per moving mesh: `swatch · ✓/✗ placed · residual mm`, row-click → `SetInspectorMesh` | ✅ works | `GuiInspector.fs` B3 (`:216`, residual `:241`) |
| `k/n` completeness count | ✅ works | `GuiInspector.fs` B1 (`:148`, `:175`) |
| Promote/demote ⚲, delete ✕ | ✅ works | rail pin row `GuiRail.fs:163,176`; inspector B1 `GuiInspector.fs:180` |
| **3D markers** drawn (wireframe tetra + line to reference marker, per moving mesh, selected pin brighter) | ✅ display-only | `ScanPinScene.anchorGlyphs:214` |
| **Focus-2D pick** (step 2 only, click the large single → server raycast → set marker, ROI-checked) | ✅ just added | `GuiFocus.pickAt` → `PickCorrespondenceAt`; handler `Update.fs:572` |
| Coarse solve consumes markers; residuals written back | ✅ works | `Update.fs SolveCoarse / CoarseSolved` |

The 3D markers (`anchorGlyphs`) are **line geometry** → not pickable, not hover-reactive. They are a read-only cue.

---

## 5. Removed, or present-but-dead (the gaps)

### Genuinely removed (by the inspector spec, on purpose)
- The **object-centred correspondence detail view** (orthographic SVG, contour/ridge symbolic mesh, on-screen rulers).
- The **marker-level linked-highlight thread**: `SetCorrMarkerHover` (detail/violin marker hover → main-view brighten + violin column highlight). **This is the "linked highlighting between 3D and 2D" you remember — it is gone.**
- The **3D one-shot correspondence pick** (`StartAnchorPick`/`AnchorPickHit`, with the reference-normal guide line) and the **patch small-multiples picker**.

### Present in the model layer but **unreachable** (dead code — no UI drives them)
1. **Readiness engine + nav actions.** `Readiness.compute` has **zero callers** (`RegistrationModel.fs:237` is just its definition), and `NavTo` is **never emitted** by any view (`Update.fs:619` is only the handler). Consequences:
   - The **per-mesh correspondence matrix** and the **`⟳` re-seed-this-mesh button** — which lived in the now-deleted `GuiWorkflow.fs` — are **gone from the UI**. `NavAction.ReseedCorrespondence` (`RegistrationModel.fs:246,323`) is dead. Re-seeding is only reachable by demote→re-promote.
   - The other nav actions (`SelectPinOpenCard`, `HighlightReferenceColumn`, `RunCoarse`, etc.) are likewise unreachable.
   - The rail's readiness pill is a **separate, inline** reimplementation (`GuiRail.fs:44-67`) that does **not** use the engine.
2. **Pin-hover → 3D-glyph highlight.** The inspector spec said to *keep* this. It is wired on the *read* side — `WorkflowPinHover` field (`Model.fs:194`), reducer (`Update.fs:534`), reader (`ScanPinScene.fs:182`) — but **`SetWorkflowPinHover` is never emitted**, so hovering a pin/readout row highlights nothing.

### Other smells
- `AnchorPatch2D` / `AnchorViolinAxial` are now unconstructable variants.
- The focus-2D pick gives **no visual feedback**: existing markers are not drawn in the focus panel (neither the large single nor the canvas cells), there is no placement confirmation, and no link back to the 3D glyph or the B3 row. You pick essentially blind.
- Auto-seed is **not** ROI-constrained (see §3), contradicting the original spec's "within `roiRadius`" rule.
- Residual in B3 is **post-solve only** (`Correspondence.Residuals` is written by `CoarseSolved`); before a solve every row shows `—`.

---

## 6. The remembered workflow vs. reality

| You remember… | Reality now |
|---|---|
| State of each correspondence point shown | ✅ Yes — bottom dock B3 (numbers-only: `✓/✗` + residual mm), plus `k/n` and read-only 3D tetra glyphs |
| Pick correspondence in 3D | ❌ Removed |
| Pick correspondence in the focus 2D view | ✅ Yes (step 2, large single click) — but blind, no marker rendered there |
| Linked highlighting between 3D and 2D | ❌ None wired (the thread that did this was deleted; the one "kept" highlight has no emitter) |
| Per-mesh re-seed control | ❌ Gone from the UI (engine orphaned); re-seed = demote→re-promote |

---

## 7. Options for review (not started)

These are independent; pick any subset.

- **A. Re-wire the pin-hover highlight.** Emit `SetWorkflowPinHover` from the rail pin row + inspector B3 rows (pointer enter/leave). Smallest change; makes a "kept" feature actually work. ~1 reducer-free view edit.
- **B. Restore a re-seed control + correspondence matrix.** Render the still-present `ReadinessView`/`Readiness.compute` diagnostics (which already carry `ReseedCorrespondence`, fly-to, select-pin nav actions) somewhere in the rail or inspector. Brings back the per-mesh `⟳`. Medium.
- **C. Make the focus-2D pick legible.** Draw existing markers in the focus panel (project them with the same `FocusProject`/server projection), show a placement confirmation, and link hover ↔ B3 ↔ 3D glyph. Restores the "linked highlighting" experience inside the new panel. Larger.
- **D. Re-add a 3D pick.** Reinstate a one-shot 3D correspondence pick in the main viewport (the deleted `AnchorPick3D` path), ROI-constrained. Larger; partly conflicts with the inspector spec's intent.
- **E. Prune instead.** If the orphaned machinery won't be revived, delete `Readiness.compute` / `NavTo` / `NavAction` / `WorkflowPinHover` / the unused `AnchorSource` variants to stop them reading as live features. Cleanup only.
- **F. Fix auto-seed ROI.** Clamp `seedAnchors` to `roiRadius` per the original spec (or explicitly decide un-clamped is intended and document it).

My suggestion if the goal is to recover the workflow you remember: **A + B + C** (wire the highlight, surface the re-seed/matrix, make the 2D picker legible+linked), and **F** for correctness. **E** only if you'd rather not revive any of it.
