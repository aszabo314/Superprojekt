//12d9db89-9ca3-a1f1-463c-ca14ea7897fa
//35303757-879c-8668-e839-a662f6364614
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
    let _LastPlacementMode_ = FSharp.Data.Adaptive.cval(value.LastPlacementMode)
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
            _LastPlacementMode_.Value <- value.LastPlacementMode
    member __.Current = __adaptive
    member __.Pins = _Pins_ :> FSharp.Data.Adaptive.amap<ScanPinId, ScanPin>
    member __.SelectedPin = _SelectedPin_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<ScanPinId>>
    member __.Placement = _Placement_ :> FSharp.Data.Adaptive.aval<PlacementState>
    member __.LastPlacementMode = _LastPlacementMode_ :> FSharp.Data.Adaptive.aval<PlacementMode>
[<System.Diagnostics.CodeAnalysis.SuppressMessage("NameConventions", "*")>]
type AdaptiveCardSystemModel(value : CardSystemModel) =
    let _Cards_ = FSharp.Data.Adaptive.cmap(value.Cards)
    let _DraggedCard_ = FSharp.Data.Adaptive.cval(value.DraggedCard)
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
            _DraggedCard_.Value <- value.DraggedCard
            _NextZOrder_.Value <- value.NextZOrder
    member __.Current = __adaptive
    member __.Cards = _Cards_ :> FSharp.Data.Adaptive.amap<CardId, Card>
    member __.DraggedCard = _DraggedCard_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<CardId>>
    member __.NextZOrder = _NextZOrder_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.int>

