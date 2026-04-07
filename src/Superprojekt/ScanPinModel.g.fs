//13b8ceac-6dda-3d63-0483-df01d93a5274
//6888d692-32ef-a5f4-ca8c-faef0831a8e1
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
    member __.Current = __adaptive
    member __.Pins = _Pins_ :> FSharp.Data.Adaptive.amap<ScanPinId, ScanPin>
    member __.ActivePlacement = _ActivePlacement_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<ScanPinId>>
    member __.SelectedPin = _SelectedPin_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<ScanPinId>>
    member __.PlacingMode = _PlacingMode_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<FootprintMode>>

