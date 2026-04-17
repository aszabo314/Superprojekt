namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardvark.SceneGraph
open FSharp.Data.Adaptive
open Aardvark.Dom

/// Stratigraphy diagram renderer: unwrapped-cylinder strip of quads per angular column.
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
            positions.Add(V3f(float32 x0, float32 y0, 0.5f))
            positions.Add(V3f(float32 x1, float32 y0, 0.5f))
            positions.Add(V3f(float32 x1, float32 y1, 0.5f))
            positions.Add(V3f(float32 x0, float32 y1, 0.5f))
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

        // Connecting lines: link same-dataset events across adjacent columns.
        let addLineSegment (x0 : float) (y0 : float) (x1 : float) (y1 : float) (half : float) (col : V4f) =
            let baseIdx = positions.Count
            positions.Add(V3f(float32 x0, float32 (y0 - half), 0.4f))
            positions.Add(V3f(float32 x0, float32 (y0 + half), 0.4f))
            positions.Add(V3f(float32 x1, float32 (y1 + half), 0.4f))
            positions.Add(V3f(float32 x1, float32 (y1 - half), 0.4f))
            for _ in 1 .. 4 do colors.Add(col)
            indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2)
            indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3)

        let datasets =
            data.Columns
            |> Array.collect (fun c -> c.Events |> List.filter (fun (_, n) -> not (Set.contains n hidden)) |> List.map snd |> List.toArray)
            |> Array.distinct
        for ds in datasets do
            let color = datasetColors |> Map.tryFind ds |> Option.defaultValue (C4b(60uy,60uy,60uy)) |> toV4f
            let perColumn =
                data.Columns |> Array.map (fun col ->
                    col.Events
                    |> List.filter (fun (_, n) -> n = ds && not (Set.contains n hidden))
                    |> List.map fst |> List.sort)
            let maxLanes = perColumn |> Array.map List.length |> Array.fold max 0
            for lane in 0 .. maxLanes - 1 do
                for ci in 0 .. nCols - 1 do
                    let ni = (ci + 1) % nCols
                    let zsA = perColumn.[ci]
                    let zsB = perColumn.[ni]
                    if lane < zsA.Length && lane < zsB.Length then
                        let yA = toY ci zsA.[lane]
                        let yB = toY ni zsB.[lane]
                        let xA = float ci * dx + dx * 0.5
                        let xB = if ni > ci then float ni * dx + dx * 0.5 else 1.0
                        addLineSegment xA yA xB yB stripeHalf color

        positions.ToArray(), colors.ToArray(), indices.ToArray()

    /// Build a horizontal cut-plane indicator line across the diagram.
    /// In Normalized mode, the line is a polyline that follows the per-column normalization.
    let private buildIndicator
        (data : StratigraphyData)
        (display : StratigraphyDisplayMode)
        (dist : float)
        : V3f[] * V4f[] * int[] =

        let nCols = data.Columns.Length
        if nCols = 0 then [||], [||], [||]
        else
        let globalRange =
            let r = data.AxisMax - data.AxisMin
            if r < 1e-9 then 1.0 else r
        let normRange (i : int) =
            let lo = data.ColumnMinZ.[i]
            let hi = data.ColumnMaxZ.[i]
            let r = hi - lo
            let minR = globalRange * 0.01
            if r < minR then
                let pad = (minR - r) * 0.5
                lo - pad, hi + pad
            else lo, hi
        let toY i z =
            match display with
            | Undistorted -> (z - data.AxisMin) / globalRange
            | Normalized ->
                let lo, hi = normRange i
                let r = hi - lo
                if r < 1e-9 then 0.5 else (z - lo) / r

        let dx = 1.0 / float nCols
        let half = 0.006
        let outlineHalf = 0.012
        let lineColor = V4f(1.0f, 1.0f, 1.0f, 0.95f)
        let outlineColor = V4f(0.0f, 0.0f, 0.0f, 0.6f)
        let positions = ResizeArray<V3f>()
        let colors = ResizeArray<V4f>()
        let indices = ResizeArray<int>()
        let addStrip halfW zOff col =
            for i in 0 .. nCols - 1 do
                let y = toY i dist |> clamp 0.0 1.0
                let x0 = float i * dx
                let x1 = x0 + dx
                let baseIdx = positions.Count
                positions.Add(V3f(float32 x0, float32 (y - halfW), float32 zOff))
                positions.Add(V3f(float32 x1, float32 (y - halfW), float32 zOff))
                positions.Add(V3f(float32 x1, float32 (y + halfW), float32 zOff))
                positions.Add(V3f(float32 x0, float32 (y + halfW), float32 zOff))
                for _ in 1 .. 4 do colors.Add(col)
                indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2)
                indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3)
        addStrip outlineHalf 0.1 outlineColor
        addStrip half 0.0 lineColor
        positions.ToArray(), colors.ToArray(), indices.ToArray()

    /// Build the warm-overlay highlight strip for a between-space hover.
    /// For each column, re-pick the bracket around `hoverZ`. Columns with no
    /// valid bracket at that z produce a gap (nothing drawn).
    let private buildHighlight
        (data : StratigraphyData)
        (cache : BandCache option)
        (display : StratigraphyDisplayMode)
        (colIdx : int)
        (hoverZ : float)
        : V3f[] * V4f[] * int[] =

        let nCols = data.Columns.Length
        if nCols = 0 then [||], [||], [||]
        else
        let band =
            match cache with
            | Some c -> Stratigraphy.lookupBand2D c colIdx hoverZ
            | None -> Stratigraphy.floodContinuousBand data colIdx hoverZ
        if Map.isEmpty band then [||], [||], [||]
        else
        let dx = 1.0 / float nCols
        let globalRange =
            let r = data.AxisMax - data.AxisMin
            if r < 1e-9 then 1.0 else r
        let normRange (i : int) =
            let lo = data.ColumnMinZ.[i]
            let hi = data.ColumnMaxZ.[i]
            let r = hi - lo
            let minR = globalRange * 0.01
            if r < minR then
                let pad = (minR - r) * 0.5
                lo - pad, hi + pad
            else lo, hi
        let toY i z =
            match display with
            | Undistorted -> (z - data.AxisMin) / globalRange
            | Normalized ->
                let lo, hi = normRange i
                let r = hi - lo
                if r < 1e-9 then 0.5 else (z - lo) / r

        let color = V4f(1.0f, 0.55f, 0.1f, 0.40f)
        let positions = ResizeArray<V3f>()
        let colors    = ResizeArray<V4f>()
        let indices   = ResizeArray<int>()

        for KeyValue(i, brackets) in band do
            let x0 = float i * dx
            let x1 = x0 + dx
            for (zLo, zHi) in brackets do
                let y0 = toY i zLo
                let y1 = toY i zHi
                let b = positions.Count
                positions.Add(V3f(float32 x0, float32 y0, 0.3f))
                positions.Add(V3f(float32 x1, float32 y0, 0.3f))
                positions.Add(V3f(float32 x1, float32 y1, 0.3f))
                positions.Add(V3f(float32 x0, float32 y1, 0.3f))
                for _ in 1 .. 4 do colors.Add(color)
                indices.Add(b); indices.Add(b + 1); indices.Add(b + 2)
                indices.Add(b); indices.Add(b + 2); indices.Add(b + 3)

        positions.ToArray(), colors.ToArray(), indices.ToArray()

    /// Build a vertical angle indicator line for AlongAxis mode.
    let private buildAngleIndicator (data : StratigraphyData) (angleDeg : float) : V3f[] * V4f[] * int[] =
        let nCols = data.Columns.Length
        if nCols = 0 then [||], [||], [||]
        else
        let angNorm = ((angleDeg % 360.0) + 360.0) % 360.0
        let x = angNorm / 360.0 |> clamp 0.0 1.0
        let half = 0.006
        let outlineHalf = 0.012
        let lineColor = V4f(1.0f, 1.0f, 1.0f, 0.95f)
        let outlineColor = V4f(0.0f, 0.0f, 0.0f, 0.6f)
        let positions = [| V3f(float32 (x - outlineHalf), 0.0f, 0.1f); V3f(float32 (x + outlineHalf), 0.0f, 0.1f)
                           V3f(float32 (x + outlineHalf), 1.0f, 0.1f); V3f(float32 (x - outlineHalf), 1.0f, 0.1f)
                           V3f(float32 (x - half), 0.0f, 0.0f); V3f(float32 (x + half), 0.0f, 0.0f)
                           V3f(float32 (x + half), 1.0f, 0.0f); V3f(float32 (x - half), 1.0f, 0.0f) |]
        let colors = [| outlineColor; outlineColor; outlineColor; outlineColor; lineColor; lineColor; lineColor; lineColor |]
        let indices = [| 0;1;2; 0;2;3; 4;5;6; 4;6;7 |]
        positions, colors, indices

    /// Convert a diagram (x [0..1], y [0..1]) coordinate back to a z value (cut plane distance).
    let private yToZ (data : StratigraphyData) (display : StratigraphyDisplayMode) (x : float) (y : float) : float =
        let globalRange =
            let r = data.AxisMax - data.AxisMin
            if r < 1e-9 then 1.0 else r
        match display with
        | Undistorted ->
            data.AxisMin + y * globalRange
        | Normalized ->
            let nCols = data.Columns.Length
            if nCols = 0 then data.AxisMin + y * globalRange
            else
            let normRange (i : int) =
                let lo = data.ColumnMinZ.[i]
                let hi = data.ColumnMaxZ.[i]
                let r = hi - lo
                let minR = globalRange * 0.01
                if r < minR then
                    let pad = (minR - r) * 0.5
                    lo - pad, hi + pad
                else lo, hi
            let ci = int (x * float nCols) |> clamp 0 (nCols - 1)
            let lo, hi = normRange ci
            lo + y * (hi - lo)

    /// Render the stratigraphy diagram for the currently selected pin.
    let render (env : Env<Message>) (isPlacing : aval<bool>) (selectedPin : aval<ScanPin option>) =
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

        let indicatorStyle =
            selectedPin |> AVal.map (fun pinOpt ->
                match pinOpt with
                | Some pin ->
                    match pin.Stratigraphy, pin.CutPlane with
                    | Some data, CutPlaneMode.AcrossAxis dist ->
                        let globalRange = let r = data.AxisMax - data.AxisMin in if r < 1e-9 then 1.0 else r
                        let yFrac = (dist - data.AxisMin) / globalRange |> clamp 0.0 1.0
                        let topPct = (1.0 - yFrac) * 100.0
                        sprintf "position:absolute;left:0;right:0;top:%.2f%%;height:2px;background:white;box-shadow:0 0 3px rgba(0,0,0,0.7);pointer-events:none;z-index:10" topPct
                    | Some _data, CutPlaneMode.AlongAxis angleDeg ->
                        let xFrac = (((angleDeg % 360.0) + 360.0) % 360.0) / 360.0 |> clamp 0.0 1.0
                        let leftPct = xFrac * 100.0
                        sprintf "position:absolute;top:0;bottom:0;left:%.2f%%;width:2px;background:white;box-shadow:0 0 3px rgba(0,0,0,0.7);pointer-events:none;z-index:10" leftPct
                    | _ -> "display:none"
                | None -> "display:none")

        let hoverGeo =
            selectedPin |> AVal.map (fun pinOpt ->
                match pinOpt with
                | Some pin ->
                    match pin.Stratigraphy, pin.BetweenSpaceHover with
                    | Some data, Some h ->
                        buildHighlight data pin.BandCache pin.StratigraphyDisplay h.ColumnIdx h.HoverZ
                    | _ -> [||], [||], [||]
                | None -> [||], [||], [||])

        let dummyPos = [| V3f.Zero |]
        let dummyCol = [| V4f.Zero |]
        let safePos (p : V3f[]) = if p.Length = 0 then dummyPos else p
        let safeCol (c : V4f[]) = if c.Length = 0 then dummyCol else c

        let posBuffer = geometry |> AVal.map (fun (p, _, _) -> ArrayBuffer (safePos p) :> IBuffer)
        let colBuffer = geometry |> AVal.map (fun (_, c, _) -> ArrayBuffer (safeCol c) :> IBuffer)
        let idxBuffer = geometry |> AVal.map (fun (_, _, i) -> ArrayBuffer i :> IBuffer)
        let drawCount = geometry |> AVal.map (fun (_, _, i) -> i.Length)

        let hovPos = hoverGeo |> AVal.map (fun (p, _, _) -> ArrayBuffer (safePos p) :> IBuffer)
        let hovCol = hoverGeo |> AVal.map (fun (_, c, _) -> ArrayBuffer (safeCol c) :> IBuffer)
        let hovIdx = hoverGeo |> AVal.map (fun (_, _, i) -> ArrayBuffer i :> IBuffer)
        let hovCnt = hoverGeo |> AVal.map (fun (_, _, i) -> i.Length)

        div {
            Class "strat-view-container"
            renderControl {
                RenderControl.Samples 1
                Class "strat-view"

                Sg.View (AVal.constant Trafo3d.Identity)
                Sg.Proj (AVal.constant (Frustum.ortho (Box3d(V3d(0.0, 0.0, -1.0), V3d(1.0, 1.0, 1.0))) |> Frustum.projTrafo))

                let! size = RenderControl.ViewportSize

                let dragging = cval false

                let emitCutFromPointer (px : int) (py : int) =
                    if AVal.force isPlacing then
                        let sz = AVal.force size
                        let x = float px / float sz.X |> clamp 0.0 1.0
                        let y = 1.0 - float py / float sz.Y |> clamp 0.0 1.0
                        match AVal.force selectedPin with
                        | Some pin ->
                            match pin.CutPlane with
                            | CutPlaneMode.AcrossAxis _ ->
                                match pin.Stratigraphy with
                                | Some data ->
                                    let z = yToZ data pin.StratigraphyDisplay x y
                                    env.Emit [ScanPinMsg (SetCutPlaneDistance z)]
                                | None -> ()
                            | CutPlaneMode.AlongAxis _ ->
                                let angle = x * 360.0
                                env.Emit [ScanPinMsg (SetCutPlaneAngle angle)]
                        | None -> ()

                let emitHoverFromPointer (px : int) (py : int) =
                    match AVal.force selectedPin with
                    | Some pin ->
                        match pin.Stratigraphy with
                        | Some data ->
                            let sz = AVal.force size
                            let x = float px / float sz.X |> clamp 0.0 1.0
                            let y = 1.0 - float py / float sz.Y |> clamp 0.0 1.0
                            let nCols = data.Columns.Length
                            if nCols = 0 then env.Emit [ScanPinMsg (ClearBetweenSpaceHover pin.Id)]
                            else
                            let col = int (x * float nCols) |> clamp 0 (nCols - 1)
                            let z = yToZ data pin.StratigraphyDisplay x y
                            match Stratigraphy.tryBracket data.Columns.[col].Events z with
                            | Some _ -> env.Emit [ScanPinMsg (HoverBetweenSpace(pin.Id, col, z))]
                            | None -> env.Emit [ScanPinMsg (ClearBetweenSpaceHover pin.Id)]
                        | None -> ()
                    | None -> ()

                Dom.OnPointerDown((fun e ->
                    if e.Button = Button.Left then
                        if e.Shift then
                            match AVal.force selectedPin with
                            | Some pin ->
                                emitHoverFromPointer e.OffsetPosition.X e.OffsetPosition.Y
                                env.Emit [ScanPinMsg (PinBetweenSpaceHover pin.Id)]
                            | None -> ()
                        else
                            transact (fun () -> dragging.Value <- true)
                            emitCutFromPointer e.OffsetPosition.X e.OffsetPosition.Y
                ), pointerCapture = true)

                Dom.OnPointerUp((fun _ ->
                    transact (fun () -> dragging.Value <- false)
                ), pointerCapture = true)

                Dom.OnPointerMove(fun e ->
                    emitHoverFromPointer e.OffsetPosition.X e.OffsetPosition.Y
                    if AVal.force dragging then
                        emitCutFromPointer e.OffsetPosition.X e.OffsetPosition.Y)

                Dom.OnMouseLeave(fun _ ->
                    match AVal.force selectedPin with
                    | Some pin -> env.Emit [ScanPinMsg (ClearBetweenSpaceHover pin.Id)]
                    | None -> ())

                Dom.OnContextMenu(ignore, preventDefault = true)

                sg {
                    Sg.Active (drawCount |> AVal.map (fun c -> c > 0))
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
                sg {
                    Sg.Active (hovCnt |> AVal.map (fun c -> c > 0))
                    Sg.Shader { DefaultSurfaces.trafo; Shader.vertexColor }
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.BlendMode BlendMode.Blend
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions, BufferView(hovPos, typeof<V3f>)
                            string DefaultSemantic.Colors,    BufferView(hovCol, typeof<V4f>)
                        ])
                    Sg.Index(BufferView(hovIdx, typeof<int>))
                    Sg.Render hovCnt
                }
            }
            div {
                Class "strat-indicator"
                indicatorStyle |> AVal.map (fun s -> Some (Style s))
            }
        }
