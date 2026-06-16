namespace Superprojekt

open System
open System.Text
open System.Text.Json
open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom.Utilities.OrbitController

module Persistence =

    let private inv = System.Globalization.CultureInfo.InvariantCulture
    let private f (v : float) = v.ToString("G17", inv)

    let private esc (s : string) =
        let sb = StringBuilder(s.Length + 2)
        for c in s do
            match c with
            | '"'  -> sb.Append("\\\"") |> ignore
            | '\\' -> sb.Append("\\\\") |> ignore
            | '\n' -> sb.Append("\\n")  |> ignore
            | '\r' -> sb.Append("\\r")  |> ignore
            | '\t' -> sb.Append("\\t")  |> ignore
            | c when c < ' ' -> sb.Append(sprintf "\\u%04x" (int c)) |> ignore
            | c -> sb.Append(c) |> ignore
        sb.ToString()
    let private q (s : string) = "\"" + esc s + "\""

    let private v3 (v : V3d) = sprintf "[%s,%s,%s]" (f v.X) (f v.Y) (f v.Z)
    let private v2 (v : V2d) = sprintf "[%s,%s]" (f v.X) (f v.Y)
    let private v4 (v : V4d) = sprintf "[%s,%s,%s,%s]" (f v.X) (f v.Y) (f v.Z) (f v.W)
    let private v2i (v : V2i) = sprintf "[%d,%d]" v.X v.Y
    let private trafoJ (t : Trafo3d) =
        let m = t.Forward
        sprintf "[%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s]"
            (f m.M00) (f m.M01) (f m.M02) (f m.M03)
            (f m.M10) (f m.M11) (f m.M12) (f m.M13)
            (f m.M20) (f m.M21) (f m.M22) (f m.M23)
            (f m.M30) (f m.M31) (f m.M32) (f m.M33)
    let private c4bJ (c : C4b) = sprintf "[%d,%d,%d,%d]" (int c.R) (int c.G) (int c.B) (int c.A)

    let private sensorTag = function
        | RoverStereo -> "rover" | Satellite -> "sat"
        | Photogrammetry -> "photo" | LiDAR -> "lidar"
        | UnknownSensor -> "unknown"
    let private sensorOf = function
        | "rover" -> RoverStereo | "sat" -> Satellite
        | "photo" -> Photogrammetry | "lidar" -> LiDAR
        | _ -> UnknownSensor
    let private regModeTag = function
        | TraditionalIcp -> "traditional"
        | RegionRestrictedIcp -> "region"
    let private regModeOf = function
        | "region" -> RegionRestrictedIcp
        | _ -> TraditionalIcp
    let private renderModeTag = function
        | Textured -> "textured" | Shaded -> "shaded" | SlopeColor -> "slope"
    let private renderModeOf = function
        | "shaded" -> Shaded | "slope" -> SlopeColor | _ -> Textured

    let private corrJ (c : Correspondence option) =
        match c with Some c -> RegJson.correspondenceJ c | None -> "null"

    let private pinPhaseTag = function
        | PinPhase.Placement -> "placement"
        | PinPhase.Committed -> "committed"
    let private pinPhaseOf = function
        | "committed" -> PinPhase.Committed
        | _ -> PinPhase.Placement
    let private pinJ (p : ScanPin) =
        let (ScanPinId.ScanPinId guid) = p.Id
        let colors =
            p.DatasetColors |> Map.toSeq
            |> Seq.map (fun (k, v) -> sprintf "%s:%s" (q k) (c4bJ v))
            |> String.concat ","
        sprintf "{\"id\":%s,\"name\":%s,\"phase\":\"%s\",\"centre\":%s,\"inner\":%s,\"corr\":%s,\"host\":%s,\"colors\":{%s},\"createdAt\":%s,\"probeLock\":%b,\"probeRange\":\"%s\"}"
            (q (guid.ToString())) (q p.Name) (pinPhaseTag p.Phase) (v3 p.Centre)
            (f p.InnerRadius) (corrJ p.Correspondence)
            (match p.HostMeshName with Some n -> q n | None -> "null")
            colors (q (p.CreatedAt.ToString("O")))
            p.ProbeLockOrder (ProbeXRange.tag p.ProbeXRange)

    let serialize (model : Model) : string =
        let sb = StringBuilder()
        sb.Append("{") |> ignore
        sb.Append("\"version\":3,") |> ignore
        sb.Append("\"dataset\":") |> ignore
        sb.Append(match model.ActiveDataset with Some d -> q d | None -> "null") |> ignore
        sb.Append(",\"pins\":[") |> ignore
        sb.Append(model.ScanPins.Pins |> HashMap.toSeq |> Seq.map (snd >> pinJ) |> String.concat ",") |> ignore
        sb.Append("],\"meshTransforms\":{") |> ignore
        sb.Append(model.MeshTransforms |> Map.toSeq
                  |> Seq.map (fun (k, v) -> sprintf "%s:%s" (q k) (trafoJ v))
                  |> String.concat ",") |> ignore
        sb.Append("},\"meshVisible\":{") |> ignore
        sb.Append(model.MeshVisible |> Map.toSeq
                  |> Seq.map (fun (k, v) -> sprintf "%s:%b" (q k) v)
                  |> String.concat ",") |> ignore
        sb.Append("},\"sensors\":{") |> ignore
        sb.Append(model.MeshSensorTypes |> Map.toSeq
                  |> Seq.map (fun (k, v) -> sprintf "%s:\"%s\"" (q k) (sensorTag v))
                  |> String.concat ",") |> ignore
        sb.Append("},\"datasetErrors\":{") |> ignore
        sb.Append(model.MeshDatasetErrors |> Map.toSeq
                  |> Seq.map (fun (k, v) -> sprintf "%s:%s" (q k) (f v))
                  |> String.concat ",") |> ignore
        sb.Append("},\"lasso\":") |> ignore
        match model.LassoVolume with
        | Some lv ->
            sb.Append(sprintf "{\"enabled\":%b,\"planes\":[%s],\"polygon\":[%s],\"vp\":%s}"
                model.LassoEnabled
                (lv.Planes |> Array.map v4 |> String.concat ",")
                (lv.ScreenPolygon |> Array.map v2 |> String.concat ",")
                (v2i lv.CommitVpSize)) |> ignore
        | None ->
            sb.Append(sprintf "{\"enabled\":%b}" model.LassoEnabled) |> ignore
        sb.Append(",\"regMode\":\"") |> ignore
        sb.Append(regModeTag model.Registration.Mode) |> ignore
        sb.Append("\",\"refMesh\":") |> ignore
        sb.Append(match model.Registration.ReferenceMesh with Some m -> q m | None -> "null") |> ignore
        // PendingReg is deliberately NOT persisted — a preview never survives
        // a save/load cycle, only committed steps do.
        sb.Append(",\"regLog\":") |> ignore
        sb.Append(RegJson.regLogJ model.RegistrationLog) |> ignore
        sb.Append(",\"lastSolve\":") |> ignore
        sb.Append(RegJson.lastSolveJ model.LastSolve) |> ignore
        // Diff mode only exists while a preview is pending; persist the mode
        // it would revert to instead.
        let persistedHeatmap =
            match model.HeatmapMode with
            | HeatDiff -> model.HeatmapPrev
            | m -> m
        sb.Append(sprintf ",\"settings\":{\"ghostSilhouette\":%b,\"ghostOpacity\":%s,\"shading\":%s,\"slopeDeg\":%s,\"anchorGhost\":%b,\"heatmapMode\":\"%s\",\"provThreshold\":%s,\"fusion\":%b,\"renderMode\":\"%s\"}"
            model.GhostSilhouette (f model.GhostOpacity) (f model.ShadingStrength)
            (f model.SlopeThresholdDeg) model.AnchorGhostMode (HeatmapMode.tag persistedHeatmap)
            (f model.ProvenanceThreshold) model.FusionMode
            (renderModeTag model.RenderingMode)) |> ignore
        sb.Append(",\"camera\":{") |> ignore
        let cam = model.Camera
        sb.Append(sprintf "\"center\":%s,\"phi\":%s,\"theta\":%s,\"radius\":%s,\"sky\":%s"
            (v3 cam.center) (f cam.phi) (f cam.theta) (f cam.radius) (v3 cam.sky)) |> ignore
        sb.Append("}}") |> ignore
        sb.ToString()

    let private rV3 (e : JsonElement) =
        let a = e.EnumerateArray() |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
        V3d(a.[0], a.[1], a.[2])
    let private rV2 (e : JsonElement) =
        let a = e.EnumerateArray() |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
        V2d(a.[0], a.[1])
    let private rV4 (e : JsonElement) =
        let a = e.EnumerateArray() |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
        V4d(a.[0], a.[1], a.[2], a.[3])
    let private rV2i (e : JsonElement) =
        let a = e.EnumerateArray() |> Seq.map (fun x -> x.GetInt32()) |> Array.ofSeq
        V2i(a.[0], a.[1])
    let private rTrafo (e : JsonElement) =
        let a = e.EnumerateArray() |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
        let fwd =
            M44d(a.[0],  a.[1],  a.[2],  a.[3],
                 a.[4],  a.[5],  a.[6],  a.[7],
                 a.[8],  a.[9],  a.[10], a.[11],
                 a.[12], a.[13], a.[14], a.[15])
        Trafo3d(fwd, fwd.Inverse)
    let private rC4b (e : JsonElement) =
        let a = e.EnumerateArray() |> Seq.map (fun x -> x.GetInt32()) |> Array.ofSeq
        C4b(byte a.[0], byte a.[1], byte a.[2], byte a.[3])
    let private tryProp (name : string) (e : JsonElement) =
        match e.TryGetProperty(name) with
        | true, v -> Some v
        | _       -> None

    // Correspondence, read from the new flat field and falling back to a
    // legacy "payload" object (removed line/patch payloads load as plain pins).
    let private rCorrespondence (e : JsonElement) =
        let fromObj o =
            match tryProp "corr" o with
            | Some v when v.ValueKind <> JsonValueKind.Null -> Some (RegJson.readCorrespondence v)
            | _ -> None
        match fromObj e with
        | Some c -> Some c
        | None -> tryProp "payload" e |> Option.bind fromObj
    let private rPin (e : JsonElement) =
        let idStr = e.GetProperty("id").GetString()
        let id = ScanPinId.ScanPinId (Guid.Parse idStr)
        let colorsE = e.GetProperty("colors")
        let colors =
            colorsE.EnumerateObject() |> Seq.map (fun p -> p.Name, rC4b p.Value) |> Map.ofSeq
        let host =
            match e.GetProperty("host").ValueKind with
            | JsonValueKind.Null -> None
            | _ -> Some (e.GetProperty("host").GetString())
        let createdAt =
            match e.GetProperty("createdAt").GetString() |> DateTime.TryParse with
            | true, dt -> dt
            | _ -> DateTime.UtcNow
        let probeLock =
            match tryProp "probeLock" e with
            | Some v -> v.GetBoolean()
            | None -> false
        let probeRange =
            match tryProp "probeRange" e with
            | Some v -> ProbeXRange.ofTag (v.GetString())
            | None -> ProbeXAuto
        {
            Id = id
            Name =
                match tryProp "name" e with
                | Some v when v.ValueKind = JsonValueKind.String -> v.GetString()
                | _ -> PinNames.generate id
            Phase = pinPhaseOf (e.GetProperty("phase").GetString())
            Centre = rV3 (e.GetProperty("centre"))
            InnerRadius = e.GetProperty("inner").GetDouble()
            Correspondence = rCorrespondence e
            HostMeshName = host
            CreatedAt = createdAt
            DatasetColors = colors
            Probe = ProbeNone
            ProbePreview = ProbeNone
            ProbeLockOrder = probeLock
            ProbeXRange = probeRange
            ContactRings = RingsNone
        }

    type ParseError = string

    // Apply a parsed workspace to the existing model. Fields not present in
    // the JSON are left at the current model value (forward-compatible).
    let apply (json : string) (model : Model) : Result<Model, ParseError> =
        try
            let doc = JsonDocument.Parse json
            let r = doc.RootElement
            let dataset =
                match tryProp "dataset" r with
                | Some e when e.ValueKind = JsonValueKind.Null -> model.ActiveDataset
                | Some e -> Some (e.GetString())
                | None -> model.ActiveDataset
            let pins =
                match tryProp "pins" r with
                | Some e ->
                    e.EnumerateArray()
                    |> Seq.map (fun pe ->
                        let p = rPin pe
                        p.Id, p)
                    |> HashMap.ofSeq
                | None -> model.ScanPins.Pins
            let meshTransforms =
                match tryProp "meshTransforms" r with
                | Some e ->
                    e.EnumerateObject()
                    |> Seq.map (fun p -> p.Name, rTrafo p.Value)
                    |> Map.ofSeq
                | None -> model.MeshTransforms
            let meshVisible =
                match tryProp "meshVisible" r with
                | Some e ->
                    e.EnumerateObject()
                    |> Seq.map (fun p -> p.Name, p.Value.GetBoolean())
                    |> Map.ofSeq
                | None -> model.MeshVisible
            let sensors =
                match tryProp "sensors" r with
                | Some e ->
                    e.EnumerateObject()
                    |> Seq.map (fun p -> p.Name, sensorOf (p.Value.GetString()))
                    |> Map.ofSeq
                | None -> model.MeshSensorTypes
            let datasetErrors =
                match tryProp "datasetErrors" r with
                | Some e ->
                    e.EnumerateObject()
                    |> Seq.map (fun p -> p.Name, p.Value.GetDouble())
                    |> Map.ofSeq
                | None -> model.MeshDatasetErrors
            let lassoEnabled, lassoVolume =
                match tryProp "lasso" r with
                | Some le ->
                    let en =
                        match tryProp "enabled" le with
                        | Some b -> b.GetBoolean()
                        | None -> model.LassoEnabled
                    let lv =
                        match tryProp "planes" le, tryProp "polygon" le, tryProp "vp" le with
                        | Some pl, Some po, Some vp ->
                            Some {
                                Planes = pl.EnumerateArray() |> Seq.map rV4 |> Array.ofSeq
                                ScreenPolygon = po.EnumerateArray() |> Seq.map rV2 |> Array.ofSeq
                                CommitVpSize = rV2i vp
                            }
                        | _ -> None
                    en, lv
                | None -> model.LassoEnabled, model.LassoVolume
            let regMode =
                match tryProp "regMode" r with
                | Some e -> regModeOf (e.GetString())
                | None -> model.Registration.Mode
            let refMesh =
                match tryProp "refMesh" r with
                | Some e when e.ValueKind = JsonValueKind.Null -> None
                | Some e -> Some (e.GetString())
                | None -> model.Registration.ReferenceMesh
            let regLog =
                match tryProp "regLog" r with
                | Some e -> RegJson.readRegLog e
                | None -> []
            // version 3; older workspaces default to empty diagnostics
            let lastSolve =
                match tryProp "lastSolve" r with
                | Some e -> RegJson.readLastSolve e
                | None -> Map.empty
            let settings =
                match tryProp "settings" r with
                | Some e -> e
                | None -> r // empty fallback won't match any keys

            let sOrElseB name fallback =
                match tryProp name settings with
                | Some v -> v.GetBoolean()
                | None -> fallback
            let sOrElseF name fallback =
                match tryProp name settings with
                | Some v -> v.GetDouble()
                | None -> fallback
            let sOrElseS name fallback =
                match tryProp name settings with
                | Some v -> v.GetString()
                | None -> fallback
            let renderMode =
                renderModeOf (sOrElseS "renderMode" (renderModeTag model.RenderingMode))
            // Version 2 writes heatmapMode; version 1 wrote a provHeatmap bool.
            let heatmapMode =
                match tryProp "heatmapMode" settings with
                | Some v -> HeatmapMode.ofTag (v.GetString())
                | None ->
                    match tryProp "provHeatmap" settings with
                    | Some v -> if v.GetBoolean() then HeatProvenance else HeatOff
                    | None -> model.HeatmapMode
            let cam =
                match tryProp "camera" r with
                | Some ce ->
                    let center = rV3 (ce.GetProperty("center"))
                    let phi = ce.GetProperty("phi").GetDouble()
                    let theta = ce.GetProperty("theta").GetDouble()
                    let radius = ce.GetProperty("radius").GetDouble()
                    let sky =
                        match tryProp "sky" ce with
                        | Some s -> rV3 s
                        | None -> model.Camera.sky
                    { model.Camera with
                        center = center
                        phi = phi; theta = theta; radius = radius
                        sky = sky
                        targetPhi = phi; targetTheta = theta; targetRadius = radius
                        userModifiedAngles = true; userModifiedCenter = true; userModifiedRadius = true
                        centerAnimation = None; locationAnimation = None; panAnimation = None }
                | None -> model.Camera

            Result.Ok {
                model with
                    ActiveDataset = dataset
                    ScanPins = { model.ScanPins with Pins = pins }
                    MeshTransforms = meshTransforms
                    MeshVisible = meshVisible
                    MeshSensorTypes = sensors
                    MeshDatasetErrors = datasetErrors
                    LassoEnabled = lassoEnabled
                    LassoVolume = lassoVolume
                    Registration = { model.Registration with Mode = regMode; ReferenceMesh = refMesh; Running = false }
                    RegistrationLog = regLog
                    LastSolve = lastSolve
                    // Transient registration state never survives a load.
                    PendingReg = None
                    AnchorReview = AnchorReviewIdle
                    AnchorPick = None
                    PatchPicker = None
                    GhostSilhouette = sOrElseB "ghostSilhouette" model.GhostSilhouette
                    GhostOpacity = sOrElseF "ghostOpacity" model.GhostOpacity
                    ShadingStrength = sOrElseF "shading" model.ShadingStrength
                    SlopeThresholdDeg = sOrElseF "slopeDeg" model.SlopeThresholdDeg
                    AnchorGhostMode = sOrElseB "anchorGhost" model.AnchorGhostMode
                    HeatmapMode = heatmapMode
                    HeatmapPrev = (match heatmapMode with HeatDiff -> HeatOff | m -> m)
                    ProvenanceThreshold = sOrElseF "provThreshold" model.ProvenanceThreshold
                    FusionMode = sOrElseB "fusion" model.FusionMode
                    RenderingMode = renderMode
                    Camera = cam
            }
        with ex -> Result.Error ex.Message
