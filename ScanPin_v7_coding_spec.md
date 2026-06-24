# ScanPin v3 — Coding Spec

Rewrite spec for the registration workflow client. Imperative. Scope is fixed: **one registration, one before/after comparison. No persistence, no history, no save/load.** Existing source files referenced for deletions: `Gui*.fs`, `Cards*.fs`, `GuiWorkflow.fs`, `GuiStudy.fs`, `Primitives.fs`, `ScanPinScene.fs`, `SceneGraph.fs`, `MeshShaders.fs`, `wwwroot/style.css`.

---

## 0. Constraints

- **Max 2 live WebGL controls.** Main perspective viewport + one secondary control. Never instantiate a third.
- Keep the mesh palette and accents: meshes `#e41a1c #377eb8 #4daf4a #984ea3 #ff7f00 #ffff33 #a65628 #f781bf #999999`; primary accent `#1a56db`; reference amber `#b45309`; data accent `#0891b2`; significance green/red `#16a34a` / `#dc2626`.
- Diverging distance map: blue `#2563eb` (below) ↔ grey `#f1f5f9` (within LoD) ↔ red `#dc2626` (above), zero-centred, saturate at robust 95th pct.
- LoD₉₅ band: `1.96·(√(σ_ref²/n_ref + σ_M²/n_M) + reg)`. (Replace the current simplified `1.96·√(σ_ref²+σ_M²)`.)

## 1. Layout

- **Top bar (slim):** hamburger, peek-reference (hold), reset camera, debug menu (`⚙`). Nothing else.
- **Left rail (workflow spine):** vertical stepper, one active step expanded. Steps: 1 Reference · 2 Coarse align · 3 Fine ICP · 4 Inspect · 5 Commit. A PINS list lives under the rail.
- **Main viewport:** primary WebGL control. Meshes with per-mesh coloured outlines, pins/glyphs, origin cross.
- **Focus panel (right):** the secondary WebGL control + a small-multiples grid of per-mesh miniatures. Reused by step 2 for ortho views.
- **Violin flyout:** optional, opened from a pin; = the pin glyph's attentive level (§8).

## 2. Workflow rail

Each step gates the next; show inline readiness pills (blocker/warning/ready). No history list.

1. **Reference** — designate reference mesh (single-select), per-mesh visibility, sensor type.
2. **Coarse align** — translate-only manual alignment of the selected moving mesh. Secondary control switches to orthographic; cycle Top / Front / Side **one view at a time** (button group). Drag translates the moving mesh in the view plane. Correspondence picking also available here. Auto-ghost all meshes except the moved one (§9).
3. **Fine ICP** — optional. Run / re-run. Region-restricted toggle (weights toward correspondences inside pin ROIs).
4. **Inspect** — error layers (§6), movement layer in preview (§7), pin glyphs (§8).
5. **Commit** — preview pose vs committed pose, commit / discard. Single commit; no stack.

## 3. Data model

```
Mesh   { id, name, paletteIdx, isReference: bool, visible, sensorType, transform: Rigid }
Pin    { id, name, center: V3 (on reference), roiRadius,
         correspondence: Map<meshId, V3> option,   // present iff used in solve
         isCorrespondence: bool }
Solve  { perMesh: Map<meshId, { coarse: Rigid, fine: Rigid option, rms }> }
Preview{ active: bool, pose: Map<meshId, Rigid> }   // transient; no history
Selection { meshIds: Set, pinId: option, focusedPinId: option }
```

No `RegStep`, no workspace serialization, no retarget state.

## 4. Pin behavior

- One primitive. `isCorrespondence` promotes a pin to also carry a registration correspondence; do not create a second pin type.
- Placement: tap on the **reference** mesh sets `center`; `roiRadius` adjustable (log slider). ROI = sphere; defines the M3C2 probe neighborhood and the evaluation region.
- When `isCorrespondence`: auto-seed `correspondence[meshId]` per moving mesh by closest-point projection inside the ROI; user can re-pick. Constrain picks to within `roiRadius` of `center`.
- Evaluation (heatmaps, glyph stats) is computed **only inside pin ROIs**; surface outside all pins is not evaluated.
- Per-pin aggregate over the ROI: signed-distance distribution per moving mesh vs reference → median, IQR, n, significance (|median| vs LoD).

## 5. Coarse align (step 2 detail)

- Secondary control renders the scene orthographically; active axis = Top | Front | Side, switched by a button group (never simultaneously).
- Pointer drag → translate selected moving mesh in the two in-plane axes. No rotation.
- Show the moving mesh solid, all others ghosted (§9 auto-mode).

## 6. Inspect — heatmaps (primary error tool)

Heatmaps paint opaque fragments only; ghost shells stay flat palette colour.

- **Intrinsic** (always available, per selected mesh, toggle channels): `incidence` (camera-incidence/grazing angle), `shape` (triangle min-angle/aspect quality), `range` (range-dependent reconstruction σ). Per-face scalar → false colour. No per-point numeric readout.
- **Extrinsic** (on selection): when reference + exactly one moving mesh are selected, paint pairwise `z-diff` or `m3c2` (toggle) on the moving mesh — diverging map, LoD neutral band.
- **Variance map** (all-meshes): when reference + ≥2 moving meshes selected, paint a single **disagreement/variance** scalar per point (variance of the extrinsic measure across the selected moving meshes). This is the only all-at-once extrinsic view; do not implement winner-take-all or screen-door.
- Focus panel shows the per-mesh extrinsic maps as small multiples (one miniature per moving mesh vs reference).

## 7. Movement layer (preview only)

Show the applied rigid motion over each pin ROI. Implement **both**, user-toggleable:

- **Glyph field:** sample a regular grid over the ROI; per sample draw a before→after displacement arrow.
- **Warped grid:** a lattice over the ROI, transformed by the preview pose; render original (faint) + warped.

Auto-engage ghost isolation: render only the moved mesh + movement glyphs solid, everything else ghosted (§9).

## 8. Pin glyph (semantic zoom)

One billboarded glyph per pin, planted at `center`, facing the camera. Two LOD levels by camera distance (or selection):

- **Far (preattentive):** pole + head. Head **colour** = verdict (green `#16a34a` if |median| ≤ LoD for all moving meshes, red `#dc2626` if significant). **Pole height** (or head size — pick one, make it configurable) = magnitude (max |median offset| across moving meshes).
- **Near (attentive):** head expands to a **split mini-violin** per moving mesh: left half = committed/before (desaturated grey), right half = preview/after (verdict colour); median tick; ±LoD band. Plus a **correspondence-completeness ring** showing `k/n` (filled per moving mesh with a marker; red ring if missing). This near view is identical to the violin flyout content.

Violin rules (both glyph-near and flyout): shared density scale across meshes; fall back to a raw strip below ~15–20 samples; KDE truncated to data range.

## 9. Ghost isolation (modifier tool)

Keep the opacity-ghost system (flat palette shell at ghost opacity; OFF = discard). Add three driven modes:

- **Align auto:** in step 2, ghost all but the moved mesh.
- **Pin-focus modifier:** hold a key (or rail toggle) → ghost everything outside the focused pin's ROI.
- **Movement auto:** in the movement layer, ghost everything except the moved mesh + glyphs.

## 10. Outlines

Per-mesh coloured outline incl. near-plane clip via **image-space deferred edge detection**: render view-space depth + normals to an offscreen buffer, run an edge filter, composite an outline in each mesh's palette colour. Do not use inverted-hull (misses the near-plane cut). Replace the opacity-ghost as the primary body-identity cue.

## 11. Debug menu

Move non-workflow controls here: dataset switch, rendering mode (Textured / Shaded / Slope), camera speed, ghost opacity, shading/slope params, centroid/bounds info, debug log. Keep it visually distinct (dark surface).

## 12. REMOVE (feature + backing code)

| Feature | Delete |
|---|---|
| Fusion | per-pixel compositor, CPU-raycast picking path, `◈` toggle |
| Save / Load | workspace JSON serialize/deserialize, gear Save/Load |
| History / persistence | `RegStep` type + history list, rollback `↩`, reset `↺`, `★ Set as final` |
| Median-offset strip | cross-pin offset strip in error stats |
| Retarget | retarget card + candidate-projection review state |
| Lasso | lasso card, clip polygon, SVG drawing layer |
| Hovering iso-plane | locked clip iso-plane gizmo + `⊟ slice` + `lock|d` path |
| Old error model | three-source `provBarJs` bar, D/A/C provenance heatmap (`Sources`), prov hover tooltip, error-metadata sensor-override UI tied to D/A/C |
| Floating pin card | rich `CardsPin.fs` surface (content folds into rail + glyph/flyout) |
| Dataset/view in top bar | move to debug menu |

After deletions, prune now-unreferenced reducers, messages, shaders, and styles.

## 13. Acceptance criteria

- ≤2 WebGL controls at all times.
- Reference designation, manual translate coarse align (ortho, one view at a time), optional ICP, preview, single commit all work end-to-end.
- Pins constrain correspondences to ROI; evaluation computed only inside ROIs.
- Intrinsic heatmaps toggle per mesh; extrinsic z-diff/M3C2 paint on reference+1 selection; variance map on reference+≥2.
- Pin glyphs render far (colour+magnitude) and near (split violin + completeness); flyout matches near content.
- Movement layer shows glyph field and warped grid (toggle), ghost-isolated, in preview.
- Per-mesh outlines render including at the near-plane clip.
- All REMOVE-list features and their dead code are gone; build is clean.
