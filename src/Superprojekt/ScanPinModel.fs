namespace Superprojekt

open System
open Aardvark.Base
open FSharp.Data.Adaptive
open Adaptify

[<RequireQualifiedAccess>]
type ScanPinId = ScanPinId of Guid with
    static member create () = ScanPinId (Guid.NewGuid())

type FootprintPolygon = {
    Vertices : V2d list
}

type SelectionPrism = {
    AnchorPoint    : V3d
    AxisDirection  : V3d
    Footprint      : FootprintPolygon
    ExtentForward  : float
    ExtentBackward : float
}

[<RequireQualifiedAccess>]
type CutPlaneMode =
    | AlongAxis  of angleDegrees : float
    | AcrossAxis of distanceFromAnchor : float

type CutResult = {
    MeshName  : string
    Polylines : V2d list list
}


[<RequireQualifiedAccess>]
type PinPhase =
    | Placement
    | Committed

type CameraSnapshot = {
    Center : V3d
    Radius : float
    Phi    : float
    Theta  : float
}

type ScanPin = {
    Id                   : ScanPinId
    Phase                : PinPhase
    Prism                : SelectionPrism
    CutPlane             : CutPlaneMode
    CreationCameraState  : CameraSnapshot
    CutResults           : Map<string, CutResult>
    DatasetColors        : Map<string, C4b>
    GridEval             : GridEvalData option
}

[<RequireQualifiedAccess>]
type FootprintMode =
    | Circle
    | Polygon

type PlacementState =
    | PlacementIdle
    | DefiningFootprint of
        anchorPoint : V3d *
        anchorRenderPos : V3d *
        axisDirection : V3d *
        currentRadius : float *
        footprintMode : FootprintMode
    | DefiningCutPlane of prism : SelectionPrism
    | Adjusting of pinId : ScanPinId

[<ModelType>]
type ScanPinModel = {
    Pins             : HashMap<ScanPinId, ScanPin>
    ActivePlacement  : ScanPinId option
    SelectedPin      : ScanPinId option
    PlacingMode      : FootprintMode option
}

module ScanPinModel =
    let initial = {
        Pins            = HashMap.empty
        ActivePlacement = None
        SelectedPin     = None
        PlacingMode     = None
    }

type CoreSampleViewMode = SideView | TopView

module ScanPinSerialize =
    open System.Text.Json

    let private writeV3d (w : Utf8JsonWriter) (v : V3d) =
        w.WriteStartArray(); w.WriteNumberValue(v.X); w.WriteNumberValue(v.Y); w.WriteNumberValue(v.Z); w.WriteEndArray()

    let private writeV2d (w : Utf8JsonWriter) (v : V2d) =
        w.WriteStartArray(); w.WriteNumberValue(v.X); w.WriteNumberValue(v.Y); w.WriteEndArray()

    let serializePin (pin : ScanPin) =
        use ms = new System.IO.MemoryStream()
        use w = new Utf8JsonWriter(ms, JsonWriterOptions(Indented = true))
        w.WriteStartObject()
        let (ScanPinId.ScanPinId guid) = pin.Id
        w.WriteString("id", guid.ToString())
        w.WriteString("phase", match pin.Phase with PinPhase.Placement -> "placement" | PinPhase.Committed -> "committed")
        w.WriteStartObject("prism")
        w.WritePropertyName("anchorPoint"); writeV3d w pin.Prism.AnchorPoint
        w.WritePropertyName("axisDirection"); writeV3d w pin.Prism.AxisDirection
        w.WriteStartArray("footprint")
        for v in pin.Prism.Footprint.Vertices do writeV2d w v
        w.WriteEndArray()
        w.WriteNumber("extentForward", pin.Prism.ExtentForward)
        w.WriteNumber("extentBackward", pin.Prism.ExtentBackward)
        w.WriteEndObject()
        match pin.CutPlane with
        | CutPlaneMode.AlongAxis deg ->
            w.WriteStartObject("cutPlane"); w.WriteString("mode", "alongAxis"); w.WriteNumber("angle", deg); w.WriteEndObject()
        | CutPlaneMode.AcrossAxis dist ->
            w.WriteStartObject("cutPlane"); w.WriteString("mode", "acrossAxis"); w.WriteNumber("distance", dist); w.WriteEndObject()
        w.WriteStartObject("camera")
        w.WritePropertyName("center"); writeV3d w pin.CreationCameraState.Center
        w.WriteNumber("radius", pin.CreationCameraState.Radius)
        w.WriteNumber("phi", pin.CreationCameraState.Phi)
        w.WriteNumber("theta", pin.CreationCameraState.Theta)
        w.WriteEndObject()
        w.WriteStartObject("datasetColors")
        for KeyValue(name, c) in pin.DatasetColors do
            w.WriteString(name, sprintf "#%02x%02x%02x" c.R c.G c.B)
        w.WriteEndObject()
        w.WriteStartArray("datasets")
        for KeyValue(name, _) in pin.CutResults do w.WriteStringValue(name)
        w.WriteEndArray()
        w.WriteEndObject()
        w.Flush()
        System.Text.Encoding.UTF8.GetString(ms.ToArray())

    let serializeAllPins (model : ScanPinModel) =
        let committed =
            model.Pins |> HashMap.toSeq
            |> Seq.filter (fun (_, p) -> p.Phase = PinPhase.Committed)
            |> Seq.map (fun (_, p) -> serializePin p)
            |> Seq.toList
        "[" + (committed |> String.concat ",\n") + "]"
