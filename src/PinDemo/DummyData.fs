namespace PinDemo

open System
open Aardvark.Base
open Superprojekt

/// A 2D height grid sampled on a regular lattice perpendicular to the prism axis.
/// Mirrors the binary shape of /api/query/grid-eval cell data.
type GridSampledSurface = {
    GridOrigin  : V2d
    CellSize    : float
    Resolution  : int
    Heights     : float[]   // row-major, length = Resolution * Resolution; NaN = no data
}

/// Per-dataset distribution stats over a core sample.
type DatasetCoreSampleStats = {
    MeshName  : string
    ZMin      : float
    ZQ1       : float
    ZMedian   : float
    ZQ3       : float
    ZMax      : float
    ZVariance : float
}

/// Per-cell aggregated statistics across all datasets.
type CellSummary = {
    Average  : float
    Q1       : float
    Q3       : float
    Min      : float
    Max      : float
    Variance : float
}

type SummaryGrid = {
    Resolution : int
    CellSize   : float
    GridOrigin : V2d
    Cells      : CellSummary[]   // row-major, NaN-aware
}

type DummyDataset = {
    MeshName : string
    Color    : C4b
    Grid     : GridSampledSurface
    Stats    : DatasetCoreSampleStats
}

module DummyData =

    let private rng = Random(42)

    let private datasetNames = [
        "demo/2024-09-15"
        "demo/2024-08-02"
        "demo/2024-06-18"
        "demo/2024-05-04"
        "demo/2024-03-22"
        "demo/2024-02-10"
        "demo/2023-12-28"
        "demo/2023-11-15"
        "demo/2023-10-01"
        "demo/2023-08-19"
        "demo/2023-07-06"
        "demo/2023-05-24"
    ]

    let private palette = [|
        C4b(220uy,  60uy,  60uy)
        C4b( 60uy, 120uy, 220uy)
        C4b( 40uy, 170uy,  90uy)
        C4b(220uy, 150uy,  40uy)
        C4b(150uy,  80uy, 200uy)
        C4b( 40uy, 180uy, 200uy)
        C4b(220uy, 100uy, 160uy)
        C4b(120uy, 160uy,  60uy)
        C4b( 80uy,  80uy, 200uy)
        C4b(200uy, 200uy,  60uy)
        C4b(180uy,  90uy, 120uy)
        C4b( 90uy, 180uy, 160uy)
    |]

    let private gridResolution = 32
    let private prismRadius    = 5.0
    let private extentForward  = 10.0
    let private extentBackward = 10.0

    /// Synthetic heightfield: each dataset is a small offset + sin bumps + a noise field.
    /// The "first" dataset is the most varied, the last is the flattest — gives the boxplot
    /// ranking some signal to display.
    let private buildGrid (index : int) : GridSampledSurface =
        let n = gridResolution
        let cell = (2.0 * prismRadius) / float (n - 1)
        let origin = V2d(-prismRadius, -prismRadius)
        let baseOffset = -3.0 + float index * 0.55
        let amp = 1.6 + float (12 - index) * 0.18
        let phaseX = float index * 0.42
        let phaseY = float index * 0.31
        let heights = Array.zeroCreate (n * n)
        for j in 0 .. n - 1 do
            for i in 0 .. n - 1 do
                let x = origin.X + float i * cell
                let y = origin.Y + float j * cell
                let r = sqrt (x * x + y * y)
                if r > prismRadius then
                    heights.[j * n + i] <- nan
                else
                    let bump =
                        amp * sin (x * 0.7 + phaseX) * cos (y * 0.6 + phaseY)
                        + 0.4 * sin (r * 1.5 + float index)
                    let noise = (rng.NextDouble() - 0.5) * 0.6
                    heights.[j * n + i] <- baseOffset + bump + noise
        { GridOrigin = origin; CellSize = cell; Resolution = n; Heights = heights }

    let private percentile (sorted : float[]) (p : float) =
        let n = sorted.Length
        if n = 0 then nan
        elif n = 1 then sorted.[0]
        else
            let idx = p * float (n - 1)
            let lo = int idx
            let hi = min (n - 1) (lo + 1)
            let t = idx - float lo
            sorted.[lo] * (1.0 - t) + sorted.[hi] * t

    let private statsOf (name : string) (g : GridSampledSurface) : DatasetCoreSampleStats =
        let valid = g.Heights |> Array.filter (fun v -> not (Double.IsNaN v))
        if valid.Length = 0 then
            { MeshName = name; ZMin = 0.0; ZQ1 = 0.0; ZMedian = 0.0; ZQ3 = 0.0; ZMax = 0.0; ZVariance = 0.0 }
        else
            let sorted = Array.sort valid
            let mean = Array.average valid
            let varSum = valid |> Array.sumBy (fun v -> let d = v - mean in d * d)
            let variance = varSum / float valid.Length
            { MeshName = name
              ZMin = sorted.[0]
              ZQ1 = percentile sorted 0.25
              ZMedian = percentile sorted 0.5
              ZQ3 = percentile sorted 0.75
              ZMax = sorted.[sorted.Length - 1]
              ZVariance = variance }

    let datasets : DummyDataset[] =
        datasetNames
        |> List.mapi (fun i name ->
            let grid = buildGrid i
            let stats = statsOf name grid
            { MeshName = name; Color = palette.[i % palette.Length]; Grid = grid; Stats = stats })
        |> List.toArray

    /// Per-cell summary statistics across all datasets.
    let summary : SummaryGrid =
        let n = gridResolution
        let cells = Array.zeroCreate (n * n)
        for j in 0 .. n - 1 do
            for i in 0 .. n - 1 do
                let vals =
                    datasets
                    |> Array.map (fun d -> d.Grid.Heights.[j * n + i])
                    |> Array.filter (fun v -> not (Double.IsNaN v))
                if vals.Length < 2 then
                    cells.[j * n + i] <-
                        { Average = nan; Q1 = nan; Q3 = nan; Min = nan; Max = nan; Variance = nan }
                else
                    let sorted = Array.sort vals
                    let mean = Array.average vals
                    let varSum = vals |> Array.sumBy (fun v -> let d = v - mean in d * d)
                    cells.[j * n + i] <-
                        { Average = mean
                          Q1 = percentile sorted 0.25
                          Q3 = percentile sorted 0.75
                          Min = sorted.[0]
                          Max = sorted.[sorted.Length - 1]
                          Variance = varSum / float vals.Length }
        { Resolution = n; CellSize = (2.0 * prismRadius) / float (n - 1)
          GridOrigin = V2d(-prismRadius, -prismRadius); Cells = cells }

    let averageGrid : GridSampledSurface =
        { GridOrigin = summary.GridOrigin; CellSize = summary.CellSize; Resolution = summary.Resolution
          Heights = summary.Cells |> Array.map (fun c -> c.Average) }

    let q1Grid : GridSampledSurface =
        { GridOrigin = summary.GridOrigin; CellSize = summary.CellSize; Resolution = summary.Resolution
          Heights = summary.Cells |> Array.map (fun c -> c.Q1) }

    let q3Grid : GridSampledSurface =
        { GridOrigin = summary.GridOrigin; CellSize = summary.CellSize; Resolution = summary.Resolution
          Heights = summary.Cells |> Array.map (fun c -> c.Q3) }

    /// One hardcoded committed pin: vertical axis, ~5m radius, centered at origin.
    let pin : ScanPin =
        let footprintVerts =
            [ for k in 0 .. 31 ->
                let a = 2.0 * Math.PI * float k / 32.0
                V2d(prismRadius * cos a, prismRadius * sin a) ]
        let prism : SelectionPrism =
            { AnchorPoint    = V3d.Zero
              AxisDirection  = V3d.OOI
              Footprint      = { Vertices = footprintVerts }
              ExtentForward  = extentForward
              ExtentBackward = extentBackward }
        let datasetColors =
            datasets
            |> Array.map (fun d -> d.MeshName, d.Color)
            |> Map.ofArray
        // Synthetic profile polylines for the SVG diagram: sample each dataset along
        // the X-axis cut at y=0 and emit (along, height) pairs. Matches the V1 elevation
        // profile shape.
        let cutResults =
            datasets
            |> Array.map (fun d ->
                let n = d.Grid.Resolution
                let row = n / 2
                let pts =
                    [ for i in 0 .. n - 1 do
                        let h = d.Grid.Heights.[row * n + i]
                        if not (Double.IsNaN h) then
                            let x = d.Grid.GridOrigin.X + float i * d.Grid.CellSize
                            yield V2d(x, h) ]
                d.MeshName,
                { MeshName = d.MeshName; Polylines = [pts] })
            |> Map.ofArray
        { Id                  = ScanPinId.create()
          Phase               = PinPhase.Committed
          Prism               = prism
          CutPlane            = CutPlaneMode.AlongAxis 0.0
          CreationCameraState = { Center = V3d.Zero; Radius = 30.0; Phi = 0.6; Theta = 0.8 }
          CutResults          = cutResults
          DatasetColors       = datasetColors }
