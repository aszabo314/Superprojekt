//d3860a41-936f-3aac-f4e0-ffffcda4bb75
//1885c636-9154-85ee-3139-98bf9fe59dd8
#nowarn "49" // upper case patterns
#nowarn "66" // upcast is unncecessary
#nowarn "1337" // internal types
#nowarn "1182" // value is unused
namespace rec Superprojekt

open System
open FSharp.Data.Adaptive
open Adaptify
open Superprojekt
[<System.Diagnostics.CodeAnalysis.SuppressMessage("NameConventions", "*")>]
type AdaptiveScanPinModel(value : ScanPinModel) =
    let _Pins_ = FSharp.Data.Adaptive.cmap(value.Pins)
    let _Placement_ = FSharp.Data.Adaptive.cval(value.Placement)
    let mutable __value = value
    let __adaptive = FSharp.Data.Adaptive.AVal.custom((fun (token : FSharp.Data.Adaptive.AdaptiveToken) -> __value))
    static member Create(value : ScanPinModel) = AdaptiveScanPinModel(value)
    static member Unpersist = Adaptify.Unpersist.create (fun (value : ScanPinModel) -> AdaptiveScanPinModel(value)) (fun (adaptive : AdaptiveScanPinModel) (value : ScanPinModel) -> adaptive.Update(value))
    member __.Update(value : ScanPinModel) =
        if Microsoft.FSharp.Core.Operators.not((FSharp.Data.Adaptive.ShallowEqualityComparer<ScanPinModel>.ShallowEquals(value, __value))) then
            __value <- value
            __adaptive.MarkOutdated()
            _Pins_.Value <- value.Pins
            _Placement_.Value <- value.Placement
    member __.Current = __adaptive
    member __.Pins = _Pins_ :> FSharp.Data.Adaptive.amap<ScanPinId, ScanPin>
    member __.Placement = _Placement_ :> FSharp.Data.Adaptive.aval<PlacementState>

