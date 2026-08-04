namespace Superprojekt

open System
open Aardvark.Base

// Browser-localStorage checkpoints of the DATA state of one registration
// scenario — dataset name, registration graph, pins. Deliberately excludes
// every transient (camera, hover, selection, caches); derived pin figures
// (contact rings, reveals) are server-recomputed on demand and restore as
// not-fetched. Writing is plain sprintf, reading System.Text.Json — both ends
// invariant-culture round-trip.
module CheckpointStore =

    let private inv = System.Globalization.CultureInfo.InvariantCulture
    let private gf (v : float) = v.ToString("R", inv)
    let private esc (s : string) = s.Replace("\\", "\\\\").Replace("\"", "\\\"")

    let private v3 (v : V3d) = sprintf "[%s,%s,%s]" (gf v.X) (gf v.Y) (gf v.Z)

    let serialize (dataset : string) (g : RegGraph) (pins : ScanPin list) =
        let root =
            match g.Root with
            | Some r -> sprintf "\"%s\"" (esc r)
            | None -> "null"
        let edges =
            g.Edges |> Map.toList |> List.map (fun (_, e) ->
                let m = e.Transform.Forward
                let cells =
                    [ m.M00; m.M01; m.M02; m.M03
                      m.M10; m.M11; m.M12; m.M13
                      m.M20; m.M21; m.M22; m.M23
                      m.M30; m.M31; m.M32; m.M33 ]
                    |> List.map gf |> String.concat ","
                sprintf "{\"c\":\"%s\",\"p\":\"%s\",\"q\":%s,\"m\":[%s]}"
                    (esc e.Child) (esc e.Parent) (gf e.Quality) cells)
            |> String.concat ","
        let pinsJ =
            pins |> List.map (fun p ->
                sprintf "{\"id\":\"%s\",\"sn\":\"%s\",\"pa\":\"%s\",\"pb\":\"%s\",\"anchor\":\"%s\",\"centre\":%s,\"r\":%s,\"ptA\":%s,\"ptB\":%s,\"t\":\"%s\"}"
                    (let (ScanPinId.ScanPinId gd) = p.Id in gd.ToString())
                    (esc p.ShortName) (esc (fst p.Pair)) (esc (snd p.Pair)) (esc p.AnchorMesh)
                    (v3 p.CentreLocal) (gf p.InnerRadius) (v3 p.PointA) (v3 p.PointB)
                    (p.CreatedAt.ToString("o", inv)))
            |> String.concat ","
        sprintf "{\"dataset\":\"%s\",\"root\":%s,\"edges\":[%s],\"pins\":[%s]}"
            (esc dataset) root edges pinsJ

    let tryDeserialize (json : string) : (string * RegGraph * ScanPin list) option =
        try
            use doc = System.Text.Json.JsonDocument.Parse json
            let r = doc.RootElement
            let str (e : System.Text.Json.JsonElement) (n : string) = e.GetProperty(n).GetString()
            let v3of (e : System.Text.Json.JsonElement) =
                V3d(e.[0].GetDouble(), e.[1].GetDouble(), e.[2].GetDouble())
            let dataset = str r "dataset"
            let root =
                let e = r.GetProperty "root"
                if e.ValueKind = System.Text.Json.JsonValueKind.Null then None else Some (e.GetString())
            let edges =
                r.GetProperty("edges").EnumerateArray()
                |> Seq.map (fun e ->
                    let m = e.GetProperty "m"
                    let c = Array.init 16 (fun i -> m.[i].GetDouble())
                    let fwd =
                        M44d(c.[0], c.[1], c.[2], c.[3], c.[4], c.[5], c.[6], c.[7],
                             c.[8], c.[9], c.[10], c.[11], c.[12], c.[13], c.[14], c.[15])
                    let child = str e "c"
                    child,
                    { Child = child; Parent = str e "p"
                      Transform = Trafo3d(fwd, fwd.Inverse)
                      Quality = e.GetProperty("q").GetDouble() })
                |> Map.ofSeq
            let pins =
                r.GetProperty("pins").EnumerateArray()
                |> Seq.map (fun e ->
                    { Id = ScanPinId.ScanPinId (Guid.Parse (str e "id"))
                      ShortName = str e "sn"
                      Pair = (str e "pa", str e "pb")
                      AnchorMesh = str e "anchor"
                      CentreLocal = v3of (e.GetProperty "centre")
                      InnerRadius = e.GetProperty("r").GetDouble()
                      PointA = v3of (e.GetProperty "ptA")
                      PointB = v3of (e.GetProperty "ptB")
                      CreatedAt = DateTime.Parse(str e "t", inv, Globalization.DateTimeStyles.RoundtripKind)
                      ContactRings = RingsNone
                      RevealA = RevealNone
                      RevealB = RevealNone })
                |> Seq.toList
            Some (dataset, { Root = root; Edges = edges }, pins)
        with _ -> None
