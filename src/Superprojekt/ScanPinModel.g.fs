//3483fa29-5db5-cf1f-3ba8-9fc36cbbcfdf
//c98545e1-8134-45ad-d869-2ee87a661c91
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
    let _SelectedPin_ = FSharp.Data.Adaptive.cval(value.SelectedPin)
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
            _SelectedPin_.Value <- value.SelectedPin
            _Placement_.Value <- value.Placement
    member __.Current = __adaptive
    member __.Pins = _Pins_ :> FSharp.Data.Adaptive.amap<ScanPinId, ScanPin>
    member __.SelectedPin = _SelectedPin_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<ScanPinId>>
    member __.Placement = _Placement_ :> FSharp.Data.Adaptive.aval<PlacementState>
[<System.Diagnostics.CodeAnalysis.SuppressMessage("NameConventions", "*")>]
type AdaptiveCardSystemModel(value : CardSystemModel) =
    let _Cards_ = FSharp.Data.Adaptive.cmap(value.Cards)
    let _NextZOrder_ = FSharp.Data.Adaptive.cval(value.NextZOrder)
    let mutable __value = value
    let __adaptive = FSharp.Data.Adaptive.AVal.custom((fun (token : FSharp.Data.Adaptive.AdaptiveToken) -> __value))
    static member Create(value : CardSystemModel) = AdaptiveCardSystemModel(value)
    static member Unpersist = Adaptify.Unpersist.create (fun (value : CardSystemModel) -> AdaptiveCardSystemModel(value)) (fun (adaptive : AdaptiveCardSystemModel) (value : CardSystemModel) -> adaptive.Update(value))
    member __.Update(value : CardSystemModel) =
        if Microsoft.FSharp.Core.Operators.not((FSharp.Data.Adaptive.ShallowEqualityComparer<CardSystemModel>.ShallowEquals(value, __value))) then
            __value <- value
            __adaptive.MarkOutdated()
            _Cards_.Value <- value.Cards
            _NextZOrder_.Value <- value.NextZOrder
    member __.Current = __adaptive
    member __.Cards = _Cards_ :> FSharp.Data.Adaptive.amap<CardId, Card>
    member __.NextZOrder = _NextZOrder_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.int>

