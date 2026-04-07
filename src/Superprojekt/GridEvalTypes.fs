namespace Superprojekt

open Aardvark.Base

type GridCellStats = {
    Average  : float
    Q1       : float
    Q3       : float
    Min      : float
    Max      : float
    Variance : float
}

type DatasetCoreSampleStats = {
    MeshName  : string
    ZMin      : float
    ZQ1       : float
    ZMedian   : float
    ZQ3       : float
    ZMax      : float
    ZVariance : float
}

type GridEvalData = {
    Resolution   : int
    GridOrigin   : V2d
    CellSize     : float
    Cells        : GridCellStats[]
    DatasetStats : DatasetCoreSampleStats[]
}
