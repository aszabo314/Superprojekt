namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardvark.SceneGraph
open FSharp.Data.Adaptive
open Aardvark.Dom

/// V3 Phase 1.6: stratigraphy diagram renderer.
/// Renders an unwrapped-cylinder stratigraphy as a quad mesh inside an embedded
/// `renderControl`. Each angular column becomes a vertical strip of quads:
/// alternating neutral fills for the between-spaces, and colored stripes at each
/// mesh contact.
module StratigraphyView =

    /// Vertical pixel half-thickness of a contact stripe relative to the unit-height plot.
    /// Drawn after the between-space fills so it always sits on top.
    let private stripeHalf = 0.005

    let private gray1 = V4f(0.86f, 0.88f, 0.92f, 1.0f)
    let private gray2 = V4f(0.78f, 0.82f, 0.88f, 1.0f)

    let private toV4f (c : C4b) =
        let f = c.ToC4f()
        V4f(f.R, f.G, f.B, 1.0f)

    /// Build the vertex/color/index buffers for one stratigraphy snapshot.
    /// Geometry lives in the unit square [0,1] × [0,1]. The renderControl uses an
    /// orthographic projection that maps that square to the canvas.
    let private buildGeometry
        (data : StratigraphyData)
        (display : StratigraphyDisplayMode)
        (datasetColors : Map<string, C4b>)
        (hidden : Set<string>)
        : V3f[] * V4f[] * int[] =

        let nCols = data.Columns.Length
        if nCols = 0 then [||], [||], [||]
        else
        let dx = 1.0 / float nCols

        let positions = ResizeArray<V3f>()
        let colors    = ResizeArray<V4f>()
        let indices   = ResizeArray<int>()

        let addQuad (x0 : float) (x1 : float) (y0 : float) (y1 : float) (col : V4f) =
            let baseIdx = positions.Count
            positions.Add(V3f(float32 x0, float32 y0, 0.0f))
            positions.Add(V3f(float32 x1, float32 y0, 0.0f))
            positions.Add(V3f(float32 x1, float32 y1, 0.0f))
            positions.Add(V3f(float32 x0, float32 y1, 0.0f))
            for _ in 1 .. 4 do colors.Add(col)
            indices.Add(baseIdx + 0); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2)
            indices.Add(baseIdx + 0); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3)

        let globalRange =
            let r = data.AxisMax - data.AxisMin
            if r < 1e-9 then 1.0 else r

        // Per-column normalization parameters
        let normRange (i : int) =
            let lo = data.ColumnMinZ.[i]
            let hi = data.ColumnMaxZ.[i]
            let r = hi - lo
            // Clamp tiny ranges to 1% of global range so the diagram doesn't blow up.
            let minR = globalRange * 0.01
            if r < minR then
                let pad = (minR - r) * 0.5
                lo - pad, hi + pad
            else lo, hi

        let toY i z =
            match display with
            | Undistorted ->
                (z - data.AxisMin) / globalRange
            | Normalized ->
                let lo, hi = normRange i
                let r = hi - lo
                if r < 1e-9 then 0.5
                else (z - lo) / r

        for i in 0 .. nCols - 1 do
            let col = data.Columns.[i]
            let x0 = float i * dx
            let x1 = x0 + dx
            // Filter hidden datasets out of this column.
            let visibleEvents =
                col.Events
                |> List.filter (fun (_, name) -> not (Set.contains name hidden))

            // Between-space fills: alternating gray bands between consecutive contacts.
            // The first band runs from y=0 to the first event; the last from the last event to y=1.
            let zSeq =
                let lo = data.AxisMin
                let hi = data.AxisMax
                let inner = visibleEvents |> List.map fst
                lo :: inner @ [hi]
            zSeq
            |> List.pairwise
            |> List.iteri (fun gapIdx (a, b) ->
                if b > a then
                    let yA = toY i a
                    let yB = toY i b
                    let g = if gapIdx % 2 = 0 then gray1 else gray2
                    addQuad x0 x1 yA yB g)

            // Mesh contact stripes drawn on top.
            for (z, name) in visibleEvents do
                let y = toY i z
                let c = datasetColors |> Map.tryFind name |> Option.defaultValue (C4b(60uy,60uy,60uy)) |> toV4f
                addQuad x0 x1 (y - stripeHalf) (y + stripeHalf) c

        positions.ToArray(), colors.ToArray(), indices.ToArray()

    /// Render the stratigraphy diagram for the currently selected pin.
    let render (selectedPin : aval<ScanPin option>) =
        let geometry =
            (selectedPin, RankingState.datasetHidden :> aset<_> |> ASet.toAVal)
            ||> AVal.map2 (fun pinOpt hiddenSet ->
                match pinOpt with
                | Some pin ->
                    match pin.Stratigraphy with
                    | Some data ->
                        let hidden = hiddenSet |> Seq.toList |> Set.ofList
                        buildGeometry data pin.StratigraphyDisplay pin.DatasetColors hidden
                    | None -> [||], [||], [||]
                | None -> [||], [||], [||])

        let posBuffer = geometry |> AVal.map (fun (p, _, _) -> ArrayBuffer p :> IBuffer)
        let colBuffer = geometry |> AVal.map (fun (_, c, _) -> ArrayBuffer c :> IBuffer)
        let idxBuffer = geometry |> AVal.map (fun (_, _, i) -> ArrayBuffer i :> IBuffer)
        let drawCount = geometry |> AVal.map (fun (_, _, i) -> i.Length)

        renderControl {
            RenderControl.Samples 1
            Class "strat-view"

            Sg.View (AVal.constant Trafo3d.Identity)
            Sg.Proj (AVal.constant (Frustum.ortho (Box3d(V3d(0.0, 0.0, -1.0), V3d(1.0, 1.0, 1.0))) |> Frustum.projTrafo))

            Sg.NoEvents

            sg {
                Sg.Shader { DefaultSurfaces.trafo; Shader.vertexColor }
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.NoEvents
                Sg.VertexAttributes(
                    HashMap.ofList [
                        string DefaultSemantic.Positions, BufferView(posBuffer, typeof<V3f>)
                        string DefaultSemantic.Colors,    BufferView(colBuffer, typeof<V4f>)
                    ])
                Sg.Index(BufferView(idxBuffer, typeof<int>))
                Sg.Render drawCount
            }
        }
