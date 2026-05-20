namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering

// TODO: special rendering techniques (OIT compositing, explore heatmap, fullscreen
// slice overlays, ghost silhouette, difference rendering, provenance overlay,
// fusion-mode best-mesh selection, lasso clipping, anchor-blob ghost) used to
// live here. The forward-render rewrite drops them all. Re-add as
// dedicated effects when the corresponding features come back.
module BlitShader =

    [<Literal>]
    let MaxLassoPlanes = 32

    [<Literal>]
    let MaxProvenanceMeshes = 16

    [<Literal>]
    let MaxProvenanceAnchors = 32
