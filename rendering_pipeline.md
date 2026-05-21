# Rendering pipeline — current state

Hybrid Forward + Weighted-Blended-OIT (WBOIT). Two offscreen passes share one
framebuffer with three colour attachments and a shared depth attachment; a
compose pass blits the result into the main framebuffer. The main FB depth
attachment is never written to, so post-compose overlays are always-on-top and
all hit-testing is driven by explicit CPU/server raycasts (no `e.Location.Depth`
readback).

## File layout

| File | Role |
|---|---|
| `src/Superprojekt/BlitShader.fs` | `OIT` module — shaders + semantics + blend modes |
| `src/Superprojekt/MeshView.fs`   | Per-mesh `aset<ISceneNode>` build for the mesh scene |
| `src/Superprojekt/SceneGraph.fs` | Pipeline assembly — FBO, two render tasks, compose, overlays |
| `src/Superprojekt/View.fs`       | Sg event handlers and the `tryPickRender` CPU raycast helper |
| `src/Superprojekt/ScanPinScene.fs` | Pin scene (uses `OIT.weightedBlend`) |
| `src/Superprojekt/LineShader.fs` | Pixel-constant 3D lines — two variants: `Lines.render` (WBOIT) and `Lines.renderForward` (passOne) |

## Resources

### Offscreen textures (viewport-sized)

| Texture | Format | Purpose |
|---|---|---|
| `forwardColorTex` | `Rgba8` | Closest α ≥ τ fragment per pixel from pass 1 |
| `accumTex`        | `Rgba16f` | WBOIT numerator `Σ(rgb·α·w, α·w)` from pass 2 |
| `revealageTex`    | `Rgba8` | WBOIT denominator `∏(1−α)` from pass 2 |
| `depthTex`        | `Depth24Stencil8` | Shared depth — written in pass 1, read-only in pass 2 |

### Framebuffer signature

```fsharp
[
    OIT.ForwardColorSemantic,     TextureFormat.Rgba8
    OIT.AccumSemantic,            TextureFormat.Rgba16f
    OIT.RevealageSemantic,        TextureFormat.Rgba8
    DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
]
```

### Per-attachment blend modes (identical in both passes)

| Attachment | Mode | Math |
|---|---|---|
| `ForwardColor` | `BlendMode.Blend`            | `dst' = src.a·src + (1−src.a)·dst` |
| `Accum`        | `BlendMode.Add`              | `dst' = src + dst` |
| `Revealage`    | `OIT.revealageBlendMode` (`Zero` / `InvSourceColor`) | `dst' = dst · (1 − src.rgb)` |

**Invariant**: `src = (0,0,0,0)` is a no-op on every attachment. That is what lets
pass 1 leave Accum/Revealage untouched and pass 2 leave ForwardColor untouched —
no per-pass MRT mask routing required.

### Clears (before pass 1, every frame)

```fsharp
clear {
    colors [
        OIT.ForwardColorSemantic, C4f.Zero
        OIT.AccumSemantic,        C4f.Zero
        OIT.RevealageSemantic,    C4f.White   // multiplicative identity for ∏(1−α)
    ]
    depth 1.0
    stencil 0
}
```

## Shaders (`BlitShader.fs`)

### `OIT.hybridBlend` — used by meshes

Branches on the `IsForwardPass` uniform (set by the outer scene wrapper):

```fsharp
let alpha = f.c.W
if alpha < 1e-4 then discard()
if IsForwardPass then
    if alpha < 0.97 then discard()
    return { forward = (rgb, 1); accum = 0; revealage = 0 }
else
    if alpha >= 0.97 then discard()
    let a = alpha*8 + 0.01
    let b = -fc.Z*0.95 + 1                            // clip-space depth bias
    let w = clamp 1e-2 3e2 (a*a*a * 1e8 * b*b*b)
    return { forward = 0
             accum = (rgb·α, α) · w
             revealage = (α, 0, 0, 0) }
```

`alphaThreshold = 0.97` is a `[<Literal>]` at the top of the `OIT` module.

### `OIT.weightedBlend` — used by pins, lines, anything always-translucent

Same WBOIT math as the `else` branch above, no `IsForwardPass` branch, always
emits `forward = 0`.

### `OIT.compose` — fullscreen quad

```fsharp
let oitColor = accum.rgb / max(accum.w, 1e-5)
let density  = 1 − revealage.r
let inv      = 1 − density
let finalRGB = oitColor·density + forward.rgb·forward.a·inv
let finalA   = density + forward.a·inv
```

Then alpha-blended (`BlendMode.Blend`) into the main FB.

Behaviourally: forward sits underneath any WBOIT contribution that survived the
depth gate. If forward.a = 1 and density = 0 → finalRGB = forward.rgb,
finalA = 1 (clean opaque). If density = α_ghost on top of opaque → finalRGB =
α·ghost + (1−α)·opaque (standard "ghost over opaque" composition).

## Pass 1 — Forward opaque

```fsharp
let forwardOpaqueScene =
    sg {
        Sg.View view; Sg.Proj proj
        Sg.DepthMask (AVal.constant true)
        Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
        Sg.BlendMode(OIT.ForwardColorSemantic, BlendMode.Blend)
        Sg.BlendMode(OIT.AccumSemantic,        BlendMode.Add)
        Sg.BlendMode(OIT.RevealageSemantic,    OIT.revealageBlendMode)
        Sg.Uniform("IsForwardPass", AVal.constant true)
        meshScene
    }
```

- Scene content: meshes only (pins/lines have no α≥τ content and would only
  discard).
- The hybrid shader discards every fragment with α < τ; survivors compete via
  depth test. The front-most α=1 fragment per pixel wins; its colour lands in
  `ForwardColor` and its depth lands in the shared depth attachment.
- Writes zero to Accum/Revealage — blend modes make this a no-op.

## Pass 2 — WBOIT translucent

```fsharp
let wbOitScene =
    sg {
        Sg.View view; Sg.Proj proj
        Sg.DepthMask (AVal.constant false)
        Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
        Sg.BlendMode(OIT.ForwardColorSemantic, BlendMode.Blend)
        Sg.BlendMode(OIT.AccumSemantic,        BlendMode.Add)
        Sg.BlendMode(OIT.RevealageSemantic,    OIT.revealageBlendMode)
        Sg.Uniform("IsForwardPass", AVal.constant false)
        ASet.unionMany [ meshScene; pinScene ]
    }
```

- Scene content: meshes + pins (+ any WBOIT-targeted line widgets).
- Depth test is `LessOrEqual` against the depth pass 1 wrote, but `DepthMask`
  is OFF — pass 2 cannot occlude pass 1, only be occluded by it.
- The hybrid shader discards α≥τ fragments here; meshes that do contribute go
  through WBOIT. Pins/lines use `OIT.weightedBlend` directly (no branch).
- Translucent fragments *behind* a forward-pass opaque (`z > depth_buffer`) are
  depth-rejected and never reach Accum/Revealage — the accepted "ghost behind
  opaque" trade-off.

## Driver

Wired through one `AVal.custom` that runs every dirty tick:

```fsharp
oitClear.Run(tok, RenderToken.Empty, oFbo)
forwardTask.Run(tok, RenderToken.Empty, oFbo)
wbOitTask.Run (tok, RenderToken.Empty, oFbo)
// then the compose ISceneNode is rendered as part of the main Sg tree
```

## Compose pass — `composeNode`

Plain Sg node returning a fullscreen quad in clip space, rendered at the
default render pass into the main framebuffer:

```fsharp
sg {
    Sg.Shader { DefaultSurfaces.trafo; OIT.compose }
    Sg.Uniform("ForwardColorTexture", forwardOut)
    Sg.Uniform("AccumTexture",        accumOut)
    Sg.Uniform("RevealageTexture",    revealageOut)
    Sg.BlendMode (AVal.constant BlendMode.Blend)
    Sg.DepthTest (AVal.constant DepthTest.None)
    Sg.View Trafo3d.Identity
    Sg.Proj Trafo3d.Identity
    // quad geometry...
}
```

Writes only colour. The main FB depth attachment stays at its clear value.

## `passOne` overlays

```fsharp
ASet.unionMany [ ASet.single composeNode; indicatorNodes; labelNodes ]
```

`indicatorNodes` / `labelNodes` are forward-shaded passOne nodes:

- `axisBoxForward` — flat-colour box at the origin (world gizmo body).
- `Lines.renderForward` — pixel-constant 3D lines for the axis arms / ticks.
- `Sg.Text` — X / Y / Z and integer-metre labels.

Every passOne node sets `Sg.DepthTest (AVal.constant DepthTest.None)` — because
the main FB has no usable depth, these render as always-on-top world gizmos.

## Picking (`View.fs`)

Aardvark.Dom's Sg picker depth-gates `Sg.OnTap` / `OnDoubleTap` / `OnLongPress`
hits against the main FB depth attachment. With the unified compose not writing
depth, `e.Location.Depth` is always ≈ 1 and `e.WorldPosition` is at the far
plane — useless. So picking is fully CPU/server driven:

### `tryPickRender`

```fsharp
tryPickRender
    (model : AdaptiveModel)
    (cursorPx : V2d) (vpSize : V2i)
    (viewT : Trafo3d) (projT : Trafo3d)
    (k : V3d option -> unit)
```

1. Construct render-space cursor ray (`pickRay`).
2. Bbox-prefilter `model.MeshNames` against the ray (respecting `MeshVisible`
   and `ActivePickingLayer`).
3. Group surviving candidates by dataset (render↔world conversion uses a
   per-dataset scale).
4. Per group: convert ray to world space, call `Query.rayBatch` (server BVH via
   Embree), convert hit back to render space.
5. Pick the closest hit by render-space `t = dot(hit − rayOrigin, rayDir)`.
6. Callback with `Some renderPos` or `None`.

Consumers:

| Handler | Behaviour |
|---|---|
| `Sg.OnTap` (anchor placement, ctrl-filter, hover update) | Immediate `tryPickRender` on `cursorScreen.Value` |
| `Sg.OnDoubleTap` | Camera retarget on hit |
| `Sg.OnLongPress` | Server sphere filter on hit |
| `Sg.OnPointerMove` | Routes to `queueHoverPick` (120 ms debounce + cancellation token), updates `hoverCoord` and `placementHover` |

`queueHoverPick` cancels any pending pick on every move, waits 120 ms, then
runs `tryPickRender`. Result lands in two cvals via a single `transact`.

## Requirements

### Per-pixel transparency / opacity decision

The pipeline must select between two compositing regimes *per fragment* based on
the fragment's α:

- **α ≥ τ (0.97)** — strict opaque. Front-most wins via depth test; no bleed
  between stacked opaque surfaces.
- **α < τ** — translucent. Depth-tested against the closest opaque (back ghosts
  occluded), then WBOIT-accumulated.

The decision lives in the fragment shader and is gated by a single uniform
(`IsForwardPass`) plus the threshold. There is no scene-graph split or
shader-stage routing — the same `Sg.Render` call services both passes because
the outer scene wrapper supplies different `IsForwardPass` values per pass.

### Opaque lasso / Gaussian-blob regions

`MeshShader.shade` computes the per-pixel α:

```
maskFactor = lassoMask ∪ blobMax              // max over Gaussian pin blobs
α          = GhostOpacity + (1 − GhostOpacity) · maskFactor
```

- `maskFactor → 1` inside the lasso polygon or the centre of a blob → α → 1 →
  forward path → strict opaque rendering, no bleed.
- `maskFactor → 0` outside → α → `GhostOpacity` → WBOIT path → ghost rendering.
- Smooth Gaussian transitions traverse WBOIT continuously up to α = τ; the
  sliver `α ∈ [τ, 1]` switches to forward. The visual seam is approximately a
  `(1 − τ) = 3 %` brightness step.

This is what is meant by "the lasso / blob is composed within OIT": there is no
separate alpha-only or depth-only pass producing a cutout — the mesh shader
emits one α and the hybrid blend routes it.

### Composition with non-mesh geometry

| Element | Where | Shader tail | Depth state |
|---|---|---|---|
| Pins (`ScanPinScene.build`) | Pass 2 | `OIT.weightedBlend` | DepthTest LessOrEqual, DepthMask OFF |
| WBOIT lines (`Lines.render`) | Pass 2 (wherever invoked) | `OIT.weightedBlend` | Inherits from outer scope (typically LessOrEqual / DepthMask OFF in pass 2) |
| Forward lines (`Lines.renderForward`) | `RenderPass.passOne` | `LineShader.line; LineShader.fragment` | DepthTest.None — always on top |
| Axis box (`axisBoxForward`) | `RenderPass.passOne` | `Shader.flatColor` | DepthTest.None — always on top |
| Text (`Sg.Text`) | `RenderPass.passOne` | (Aardvark.Text internal) | DepthTest.None — always on top |

WBOIT-targeted elements (pins, in-OIT lines) get the same depth gate against
forward-pass opaque as the mesh translucent path. Forward overlays (axes,
labels) render straight into the main FB after compose with no depth source —
hence always-on-top semantics.

### Picking depth-source independence

The main FB has no live depth attachment, so:

- `e.Location.Depth` and `e.WorldPosition` from Sg events are unusable.
- All hit-testing must go through `tryPickRender` (or, for objects with their
  own 3D extent that need point-style pick, a custom `Dom.OnPointerDown` +
  ray-vs-shape test — see the cylinder-drag picker in `View.fs`).

## Known limitations / open trade-offs

1. **Ghost behind opaque is hidden.** A fragment with α < τ at z greater than
   the closest forward-pass z is depth-rejected. Mathematically consistent with
   α = 1 ≡ full opacity, but a regression from the "ghost-through-opaque inside
   the lasso interior" that the unified-WBOIT-only version provided via bleed.
   Recovery requires capping the lasso-interior α (then accept WBOIT-style ~10 %
   bleed everywhere it overlaps) or moving to MLAB / depth peeling.

2. **τ seam.** A ~3 % brightness step at the α ≈ τ boundary. Adjustable by
   changing `alphaThreshold` in `BlitShader.fs`. Lower τ shrinks the seam but
   pushes more of the α range into the forward path; higher τ widens the seam
   but reduces forward-path coverage.

3. **Tiny-depth-gap intra-WBOIT bleed.** Inside the soft region, two ghosts at
   very close z are still mildly front-biased by `b^3` clip-space weight. Not
   currently a problem in practice; can be sharpened with linearised z or a
   steeper `b^k` if needed.

4. **Cost.** 2N draws per frame for N meshes (vs N in the previous single-pass
   form). Both passes share the same FBO; resource allocation does not change.

5. **Picking latency.** Each tap costs one server `Query.rayBatch` roundtrip
   (~10–50 ms localhost). Hover adds 120 ms debounce on top of that.

6. **Always-on-top world gizmo.** Axis cross + labels render over everything.
   Standard for a world-origin gizmo; would need a dedicated forward depth-only
   pass to restore mesh-aware occlusion of those overlays.

## Future levers (if needed)

- **MLAB (K = 2..4)**: keeps K front-most layers exact, rest WBOIT. Restores
  ghost-through-opaque while keeping bleed-free opaque. Costs K × (color +
  depth) attachments.
- **Per-mesh front-to-back sort + per-mesh WBOIT slice**: scales well with the
  small mesh count (typically 1–10); exact inter-mesh occlusion.
- **Steeper / α-aware WBOIT depth weight**: shader-only change in `weightedBlend` /
  the `else`-branch of `hybridBlend`. Reduces intra-WBOIT bleed for stacked
  ghosts without touching the pipeline.
- **Conditional `gl_FragDepth` at τ**: a single-pass form of the current
  hybrid. Collapses 2N draws back to N, kills early-Z for the whole pass,
  worse depth-state ergonomics.
