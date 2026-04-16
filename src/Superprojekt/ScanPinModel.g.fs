//7953dcfa-17e2-b80a-0f0e-d75d51bd76d3
//65ffa46b-7738-4bf6-b1c7-fdf1125bc4b1
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
    let _ActivePlacement_ = FSharp.Data.Adaptive.cval(value.ActivePlacement)
    let _SelectedPin_ = FSharp.Data.Adaptive.cval(value.SelectedPin)
    let _PlacingMode_ = FSharp.Data.Adaptive.cval(value.PlacingMode)
    let _BetweenSpaceEnabled_ = FSharp.Data.Adaptive.cval(value.BetweenSpaceEnabled)
    let mutable __value = value
    let __adaptive = FSharp.Data.Adaptive.AVal.custom((fun (token : FSharp.Data.Adaptive.AdaptiveToken) -> __value))
    static member Create(value : ScanPinModel) = AdaptiveScanPinModel(value)
    static member Unpersist = Adaptify.Unpersist.create (fun (value : ScanPinModel) -> AdaptiveScanPinModel(value)) (fun (adaptive : AdaptiveScanPinModel) (value : ScanPinModel) -> adaptive.Update(value))
    member __.Update(value : ScanPinModel) =
        if Microsoft.FSharp.Core.Operators.not((FSharp.Data.Adaptive.ShallowEqualityComparer<ScanPinModel>.ShallowEquals(value, __value))) then
            __value <- value
            __adaptive.MarkOutdated()
            _Pins_.Value <- value.Pins
            _ActivePlacement_.Value <- value.ActivePlacement
            _SelectedPin_.Value <- value.SelectedPin
            _PlacingMode_.Value <- value.PlacingMode
            _BetweenSpaceEnabled_.Value <- value.BetweenSpaceEnabled
    member __.Current = __adaptive
    member __.Pins = _Pins_ :> FSharp.Data.Adaptive.amap<ScanPinId, ScanPin>
    member __.ActivePlacement = _ActivePlacement_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<ScanPinId>>
    member __.SelectedPin = _SelectedPin_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<ScanPinId>>
    member __.PlacingMode = _PlacingMode_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<FootprintMode>>
    member __.BetweenSpaceEnabled = _BetweenSpaceEnabled_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
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

