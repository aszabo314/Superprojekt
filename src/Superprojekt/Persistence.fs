namespace Superprojekt

open System
open System.Text.Json
open System.Text.Json.Nodes
open Aardvark.Base
open FSharp.Data.Adaptive

/// V6 §D.13 — workspace save/load. Serialises the user-facing parts of
/// the model to JSON and rehydrates them back. Mesh references survive
/// across sessions via `(DatasetId, MeshId)` name pairs; anchor centres
/// are stored in **world space** so a reload against a re-scaled dataset
/// still places them correctly.
module Persistence =

    let private fileVersion = 1

    let private writeV3 (n : JsonObject) (key : string) (v : V3d) =
        let a = JsonArray()
        a.Add(JsonValue.Create v.X) |> ignore
        a.Add(JsonValue.Create v.Y) |> ignore
        a.Add(JsonValue.Create v.Z) |> ignore
        n.[key] <- a

    let private readV3 (n : JsonNode) =
        let a = n.AsArray()
        V3d(a.[0].GetValue<float>(), a.[1].GetValue<float>(), a.[2].GetValue<float>())

    let private writeBox (n : JsonObject) (key : string) (b : Box3d) =
        let o = JsonObject()
        writeV3 o "min" b.Min
        writeV3 o "max" b.Max
        n.[key] <- o

    let private readBox (n : JsonNode) =
        let o = n.AsObject()
        Box3d(readV3 o.["min"], readV3 o.["max"])

    let private writeM44 (n : JsonObject) (key : string) (m : M44d) =
        let a = JsonArray()
        for v in [ m.M00; m.M01; m.M02; m.M03
                   m.M10; m.M11; m.M12; m.M13
                   m.M20; m.M21; m.M22; m.M23
                   m.M30; m.M31; m.M32; m.M33 ] do
            a.Add(JsonValue.Create v) |> ignore
        n.[key] <- a

    let private readM44 (n : JsonNode) =
        let a = n.AsArray()
        M44d(a.[0].GetValue<float>(),  a.[1].GetValue<float>(),  a.[2].GetValue<float>(),  a.[3].GetValue<float>(),
             a.[4].GetValue<float>(),  a.[5].GetValue<float>(),  a.[6].GetValue<float>(),  a.[7].GetValue<float>(),
             a.[8].GetValue<float>(),  a.[9].GetValue<float>(),  a.[10].GetValue<float>(), a.[11].GetValue<float>(),
             a.[12].GetValue<float>(), a.[13].GetValue<float>(), a.[14].GetValue<float>(), a.[15].GetValue<float>())

    let private sensorToString = function
        | RoverStereo    -> "rover"
        | Satellite      -> "satellite"
        | Photogrammetry -> "photogrammetry"
        | LiDAR          -> "lidar"
        | UnknownSensor  -> "unknown"

    let private stringToSensor = function
        | "rover"          -> RoverStereo
        | "satellite"      -> Satellite
        | "photogrammetry" -> Photogrammetry
        | "lidar"          -> LiDAR
        | _                -> UnknownSensor

    let private payloadKindToString (p : PayloadType) =
        match p with
        | Point _ -> "point"
        | Line _  -> "line"
        | Patch _ -> "patch"

    /// Renders the workspace's persistable state into a JSON string.
    /// Anchor centres are converted from render space (model's
    /// convention) to world space for storage so a reload survives
    /// a dataset-scale change.
    let serialize (model : Model) : string =
        let root = JsonObject()
        root.["version"] <- JsonValue.Create fileVersion
        root.["savedAt"] <- JsonValue.Create (DateTime.UtcNow.ToString("o"))
        match model.ActiveDataset with
        | Some d -> root.["activeDataset"] <- JsonValue.Create d
        | None -> ()

        let scale =
            model.ActiveDataset
            |> Option.bind (fun d -> Map.tryFind d model.DatasetScales)
            |> Option.defaultValue 1.0
        let cc = model.CommonCentroid

        // Anchors (committed + in-flight; both rehydrate cleanly).
        let pinsArr = JsonArray()
        for (id, pin) in HashMap.toSeq model.ScanPins.Pins do
            let o = JsonObject()
            o.["id"] <- JsonValue.Create (match id with ScanPinId.ScanPinId g -> g.ToString())
            o.["phase"] <-
                JsonValue.Create (match pin.Phase with PinPhase.Placement -> "placement" | PinPhase.Committed -> "committed")
            // Centre + sigma + radius in world space (mesh-independent).
            let worldCentre = pin.Centre / scale + cc
            let worldSigma  = pin.Sigma / scale
            let worldRadius = pin.Radius / scale
            writeV3 o "centre" worldCentre
            o.["radius"] <- JsonValue.Create worldRadius
            o.["sigma"]  <- JsonValue.Create worldSigma
            match pin.HostMeshName with
            | Some h -> o.["host"] <- JsonValue.Create h
            | None -> ()
            o.["payloadKind"] <- JsonValue.Create (payloadKindToString pin.Payload)
            match pin.Payload with
            | Point pp ->
                o.["reliability"] <- JsonValue.Create pp.ReliabilityWeight
            | Line lp ->
                let mode =
                    match lp.Mode with
                    | ElevationIsoline e -> sprintf "isoline:%g" e
                    | CurvatureRidge -> "ridge"
                o.["lineMode"] <- JsonValue.Create mode
            | Patch pp ->
                o.["patchRadius"] <- JsonValue.Create pp.Radius
                o.["patchSource"] <- JsonValue.Create pp.SourceMeshName
            o.["createdAt"] <- JsonValue.Create (pin.CreatedAt.ToString("o"))
            pinsArr.Add o |> ignore
        root.["anchors"] <- pinsArr

        // Per-mesh transforms (render-space rigid; rehydrated as-is).
        let txObj = JsonObject()
        for (name, t) in Map.toSeq model.MeshTransforms do
            let o = JsonObject()
            writeM44 o "forward" t.Forward
            txObj.[name] <- o
        root.["meshTransforms"] <- txObj

        // Sensor types + dataset error overrides.
        let sensorObj = JsonObject()
        for (name, s) in Map.toSeq model.MeshSensorTypes do
            sensorObj.[name] <- JsonValue.Create (sensorToString s)
        root.["meshSensors"] <- sensorObj

        let errObj = JsonObject()
        for (name, v) in Map.toSeq model.MeshDatasetErrors do
            errObj.[name] <- JsonValue.Create v
        root.["meshDatasetErrors"] <- errObj

        // Mesh visibility + dataset scales (so a reload preserves user choices).
        let visObj = JsonObject()
        for (name, v) in Map.toSeq model.MeshVisible do
            visObj.[name] <- JsonValue.Create v
        root.["meshVisible"] <- visObj

        let scaleObj = JsonObject()
        for (name, v) in Map.toSeq model.DatasetScales do
            scaleObj.[name] <- JsonValue.Create v
        root.["datasetScales"] <- scaleObj

        // Clip + lasso state.
        let clip = JsonObject()
        clip.["active"] <- JsonValue.Create model.ClipActive
        writeBox clip "box" model.ClipBox
        root.["clip"] <- clip

        match model.LassoVolume with
        | Some v ->
            let lo = JsonObject()
            let planes = JsonArray()
            for p in v.Planes do
                let a = JsonArray()
                a.Add(JsonValue.Create p.X) |> ignore
                a.Add(JsonValue.Create p.Y) |> ignore
                a.Add(JsonValue.Create p.Z) |> ignore
                a.Add(JsonValue.Create p.W) |> ignore
                planes.Add a |> ignore
            lo.["planes"] <- planes
            let poly = JsonArray()
            for p in v.ScreenPolygon do
                let a = JsonArray()
                a.Add(JsonValue.Create p.X) |> ignore
                a.Add(JsonValue.Create p.Y) |> ignore
                poly.Add a |> ignore
            lo.["polygon"] <- poly
            lo.["vpX"] <- JsonValue.Create v.CommitVpSize.X
            lo.["vpY"] <- JsonValue.Create v.CommitVpSize.Y
            root.["lassoVolume"] <- lo
        | None -> ()

        // Explore mode (dual-signal state).
        let ex = JsonObject()
        ex.["enabled"] <- JsonValue.Create model.Explore.Enabled
        let writeSignal (s : SignalState) =
            let o = JsonObject()
            o.["enabled"]   <- JsonValue.Create s.Enabled
            o.["threshold"] <- JsonValue.Create s.Threshold
            let cArr = JsonArray()
            cArr.Add(JsonValue.Create (float s.Color.R)) |> ignore
            cArr.Add(JsonValue.Create (float s.Color.G)) |> ignore
            cArr.Add(JsonValue.Create (float s.Color.B)) |> ignore
            cArr.Add(JsonValue.Create (float s.Color.A)) |> ignore
            o.["color"] <- cArr
            o
        ex.["fc"] <- writeSignal model.Explore.FeatureConfidence
        ex.["dg"] <- writeSignal model.Explore.Disagreement
        ex.["mix"] <-
            JsonValue.Create (match model.Explore.MixMode with
                              | SideBySide -> "side"
                              | Blended -> "blend"
                              | Alternating -> "alt")
        ex.["alpha"] <- JsonValue.Create model.Explore.HighlightAlpha
        root.["explore"] <- ex

        // Other top-level toggles.
        root.["fullscreen"]    <- JsonValue.Create model.FullscreenOn
        root.["ghostOn"]       <- JsonValue.Create model.GhostSilhouette
        root.["ghostDetail"]   <-
            JsonValue.Create (match model.GhostDetail with
                              | OutlineOnly -> "outline"
                              | PlusCurvature -> "curv"
                              | PlusTerrainFeatures -> "terrain")
        root.["ghostOpacity"]  <- JsonValue.Create model.GhostOpacity
        root.["fusion"]        <- JsonValue.Create model.FusionMode
        root.["provHeatmap"]   <- JsonValue.Create model.ProvenanceHeatmap
        root.["provThreshold"] <- JsonValue.Create model.ProvenanceThreshold
        root.["falloffOnly"]   <- JsonValue.Create model.FalloffZoneOnly
        root.["refAxis"] <-
            JsonValue.Create (match model.ReferenceAxis with
                              | AlongWorldZ -> "z"
                              | AlongCameraView -> "view")

        // Registration state (mode + reference + per-mesh transforms cover the rest).
        let reg = JsonObject()
        reg.["mode"] <-
            JsonValue.Create (match model.Registration.Mode with
                              | TraditionalIcp -> "icp"
                              | RegionRestrictedIcp -> "region"
                              | PointPairPlusRefinement -> "pp")
        match model.Registration.ReferenceMesh with
        | Some r -> reg.["reference"] <- JsonValue.Create r
        | None -> ()
        root.["registration"] <- reg

        let opts = JsonSerializerOptions(WriteIndented = true)
        root.ToJsonString(opts)

    /// Apply a snapshot to a model. Anchors arrive in world space and
    /// are converted back to the model's render-space convention using
    /// the **current** dataset scale + centroid. If `activeDataset` in
    /// the snapshot differs from the current model, we just keep the
    /// current one (the spec's "loaded workspace references a dataset
    /// not currently available" warning lives in the caller).
    let private applySnapshot (current : Model) (json : string) : Result<Model, string> =
        try
            let root = JsonNode.Parse(json).AsObject()
            let scale =
                current.ActiveDataset
                |> Option.bind (fun d -> Map.tryFind d current.DatasetScales)
                |> Option.defaultValue 1.0
            let cc = current.CommonCentroid

            // Anchors.
            let mutable pinMap = HashMap.empty
            match root.["anchors"] with
            | null -> ()
            | a ->
                for el in a.AsArray() do
                    let o = el.AsObject()
                    let id = ScanPinId.ScanPinId (Guid.Parse(o.["id"].GetValue<string>()))
                    let worldC = readV3 o.["centre"]
                    let renderCentre = (worldC - cc) * scale
                    let radius = o.["radius"].GetValue<float>() * scale
                    let sigma  = o.["sigma"].GetValue<float>() * scale
                    let tryStr (key : string) : string option =
                        let v = o.[key]
                        if isNull v then None else Some (v.GetValue<string>())
                    let tryFloat (key : string) : float option =
                        let v = o.[key]
                        if isNull v then None else Some (v.GetValue<float>())
                    let host = tryStr "host"
                    let payload =
                        let kind = o.["payloadKind"].GetValue<string>()
                        match kind with
                        | "line" ->
                            let mode =
                                match tryStr "lineMode" with
                                | Some s ->
                                    if s.StartsWith "isoline:" then
                                        let sub : string = s.Substring 8
                                        let mutable parsed = 0.0
                                        if Double.TryParse(sub, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, &parsed) then ElevationIsoline parsed
                                        else ElevationIsoline 0.0
                                    elif s = "ridge" then CurvatureRidge
                                    else ElevationIsoline 0.0
                                | None -> ElevationIsoline 0.0
                            Line {
                                Mode = mode
                                Points = [||]
                                ScalarVals = [||]
                                CrossMeshTraces = Map.empty
                            }
                        | "patch" ->
                            let pr = tryFloat "patchRadius" |> Option.defaultValue radius
                            let src = tryStr "patchSource" |> Option.defaultValue (host |> Option.defaultValue "")
                            Patch {
                                CenterOnMesh = renderCentre
                                Radius = pr
                                SourceMeshName = src
                                ProjectedPoints = [||]
                                CompassNorth = V2d(1.0, 0.0)
                                RefDirWorld = V3d.OIO
                                NormalWorld = V3d.OOI
                            }
                        | _ ->
                            let w = tryFloat "reliability" |> Option.defaultValue 1.0
                            Point { ReliabilityWeight = w }
                    let phase =
                        match o.["phase"].GetValue<string>() with
                        | "placement" -> PinPhase.Placement
                        | _ -> PinPhase.Committed
                    let createdAt =
                        match tryStr "createdAt" with
                        | Some s ->
                            let mutable parsed = DateTime.UtcNow
                            if DateTime.TryParse(s, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind, &parsed) then parsed
                            else DateTime.UtcNow
                        | None -> DateTime.UtcNow
                    let pin = {
                        Id = id
                        Phase = phase
                        Centre = renderCentre
                        Radius = radius
                        Sigma = sigma
                        Payload = payload
                        HostMeshName = host
                        CorrespondenceLinkId = None
                        CreationCameraState = { Center = current.Camera.center; Radius = current.Camera.radius; Phi = current.Camera.phi; Theta = current.Camera.theta }
                        CreatedAt = createdAt
                        DatasetColors =
                            current.MeshNames |> IndexList.toArray
                            |> Array.mapi (fun i n -> n, Primitives.meshColor i)
                            |> Map.ofArray
                    }
                    pinMap <- HashMap.add id pin pinMap

            // Per-mesh transforms.
            let mutable txMap = Map.empty
            match root.["meshTransforms"] with
            | null -> ()
            | n ->
                for kv in n.AsObject() do
                    let fwd = readM44 (kv.Value.AsObject().["forward"])
                    txMap <- Map.add kv.Key (Trafo3d(fwd, fwd.Inverse)) txMap

            // Sensor types.
            let mutable sensors = Map.empty
            match root.["meshSensors"] with
            | null -> ()
            | n ->
                for kv in n.AsObject() do
                    sensors <- Map.add kv.Key (stringToSensor (kv.Value.GetValue<string>())) sensors

            let mutable errors = Map.empty
            match root.["meshDatasetErrors"] with
            | null -> ()
            | n ->
                for kv in n.AsObject() do
                    errors <- Map.add kv.Key (kv.Value.GetValue<float>()) errors

            let mutable vis = current.MeshVisible
            match root.["meshVisible"] with
            | null -> ()
            | n ->
                for kv in n.AsObject() do
                    vis <- Map.add kv.Key (kv.Value.GetValue<bool>()) vis

            // Clip box.
            let mutable clipActive = current.ClipActive
            let mutable clipBox = current.ClipBox
            match root.["clip"] with
            | null -> ()
            | n ->
                let o = n.AsObject()
                clipActive <- o.["active"].GetValue<bool>()
                clipBox <- readBox o.["box"]

            // Lasso.
            let lassoVol =
                match root.["lassoVolume"] with
                | null -> None
                | n ->
                    let o = n.AsObject()
                    let planes =
                        o.["planes"].AsArray() |> Seq.map (fun el ->
                            let a = el.AsArray()
                            V4d(a.[0].GetValue<float>(), a.[1].GetValue<float>(),
                                a.[2].GetValue<float>(), a.[3].GetValue<float>()))
                        |> Array.ofSeq
                    let poly =
                        o.["polygon"].AsArray() |> Seq.map (fun el ->
                            let a = el.AsArray()
                            V2d(a.[0].GetValue<float>(), a.[1].GetValue<float>()))
                        |> Array.ofSeq
                    Some {
                        Planes = planes
                        ScreenPolygon = poly
                        CommitVpSize = V2i(o.["vpX"].GetValue<int>(), o.["vpY"].GetValue<int>())
                    }

            // Explore mode.
            let exMode =
                match root.["explore"] with
                | null -> current.Explore
                | n ->
                    let o = n.AsObject()
                    let readSignal (s : JsonNode) =
                        let so = s.AsObject()
                        let c = so.["color"].AsArray()
                        {
                            Enabled = so.["enabled"].GetValue<bool>()
                            Threshold = so.["threshold"].GetValue<float>()
                            Color = C4f(float32 (c.[0].GetValue<float>()), float32 (c.[1].GetValue<float>()),
                                        float32 (c.[2].GetValue<float>()), float32 (c.[3].GetValue<float>()))
                        }
                    {
                        Enabled = o.["enabled"].GetValue<bool>()
                        FeatureConfidence = readSignal o.["fc"]
                        Disagreement = readSignal o.["dg"]
                        MixMode =
                            match o.["mix"].GetValue<string>() with
                            | "side" -> SideBySide
                            | "alt" -> Alternating
                            | _ -> Blended
                        HighlightAlpha = o.["alpha"].GetValue<float>()
                    }

            let registrationMode =
                match root.["registration"] with
                | null -> current.Registration
                | n ->
                    let o = n.AsObject()
                    let m =
                        match o.["mode"].GetValue<string>() with
                        | "region" -> RegionRestrictedIcp
                        | "pp" -> PointPairPlusRefinement
                        | _ -> TraditionalIcp
                    let refNode = o.["reference"]
                    let r =
                        if isNull refNode then None else Some (refNode.GetValue<string>())
                    { current.Registration with Mode = m; ReferenceMesh = r }

            let jbool (key : string) (dflt : bool) =
                let n : JsonNode = root.[key]
                if isNull n then dflt else n.GetValue<bool>()
            let jfloat (key : string) (dflt : float) =
                let n : JsonNode = root.[key]
                if isNull n then dflt else n.GetValue<float>()
            let jstr (key : string) (dflt : string) =
                let n : JsonNode = root.[key]
                if isNull n then dflt else n.GetValue<string>()

            let ghostDetail =
                match jstr "ghostDetail" "outline" with
                | "curv" -> PlusCurvature
                | "terrain" -> PlusTerrainFeatures
                | _ -> OutlineOnly

            let refAxis =
                match jstr "refAxis" "z" with
                | "view" -> AlongCameraView
                | _ -> AlongWorldZ

            Ok { current with
                    ScanPins = { current.ScanPins with Pins = pinMap; Placement = PlacementIdle }
                    MeshTransforms = txMap
                    MeshSensorTypes = sensors
                    MeshDatasetErrors = errors
                    MeshVisible = vis
                    ClipActive = clipActive
                    ClipBox = clipBox
                    LassoVolume = lassoVol
                    LassoDrawing = None
                    Explore = exMode
                    Registration = registrationMode
                    FullscreenOn = jbool "fullscreen" current.FullscreenOn
                    GhostSilhouette = jbool "ghostOn" current.GhostSilhouette
                    GhostDetail = ghostDetail
                    GhostOpacity = jfloat "ghostOpacity" current.GhostOpacity
                    FusionMode = jbool "fusion" current.FusionMode
                    ProvenanceHeatmap = jbool "provHeatmap" current.ProvenanceHeatmap
                    ProvenanceThreshold = jfloat "provThreshold" current.ProvenanceThreshold
                    FalloffZoneOnly = jbool "falloffOnly" current.FalloffZoneOnly
                    ReferenceAxis = refAxis }
        with ex ->
            Result.Error ex.Message

    let deserialize (current : Model) (json : string) : Result<Model, string> =
        applySnapshot current json
