namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiOverlays =

    let meshWheelLabel (model : AdaptiveModel) (cursorScreen : aval<V2d option>) =
        let meshOrderMap = model.MeshOrder.Content
        div {
            Class "mesh-wheel-label"
            (model.ActivePickingLayer, cursorScreen) ||> AVal.map2 (fun layer cOpt ->
                match layer, cOpt with
                | Some _, Some pos ->
                    Some (Style [
                        Left (sprintf "%.0fpx" (pos.X + 14.0))
                        Top  (sprintf "%.0fpx" (pos.Y - 10.0))
                    ])
                | _ -> Some (Style [Display "none"]))
            (model.ActivePickingLayer, meshOrderMap) ||> AVal.map2 (fun layer order ->
                match layer with
                | Some name -> Primitives.numbered order name
                | None -> "")
        }

    // Transient feedback for blocked/failed actions (auto-clears).
    let toast (model : AdaptiveModel) =
        div {
            Class "app-toast"
            Primitives.showWhen (model.Toast |> AVal.map Option.isSome)
            model.Toast |> AVal.map (Option.defaultValue "")
        }

    let private niceRoundDistance (d : float) =
        if d <= 0.0 || System.Double.IsNaN d || System.Double.IsInfinity d then 1.0
        else
            let steps = [| 0.1; 0.2; 0.5; 1.0; 2.0; 5.0; 10.0; 20.0; 50.0; 100.0; 200.0; 500.0; 1000.0 |]
            let mag = 10.0 ** floor (log10 d)
            let norm = d / mag
            let picked =
                if norm < 0.15 then 0.1
                elif norm < 0.35 then 0.2
                elif norm < 0.75 then 0.5
                elif norm < 1.5 then 1.0
                elif norm < 3.5 then 2.0
                elif norm < 7.5 then 5.0
                else 10.0
            let v = picked * mag
            steps |> Array.minBy (fun s -> abs (log (s / v)))

    let private formatMeters (m : float) =
        if m >= 1000.0 then sprintf "%g km" (m / 1000.0)
        elif m >= 1.0 then sprintf "%g m" m
        else sprintf "%g cm" (m * 100.0)

    let scaleBar (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let targetPx = 100.0
        let barInfo =
            AVal.custom (fun t ->
                let radius = model.Camera.radius.GetValue t
                let vp = viewportSize.GetValue t
                let scale = DatasetScale.active (model.ActiveDataset.GetValue t) (model.DatasetScales.GetValue t)
                let h = max 1 vp.Y
                let verticalFov = 90.0 * Constant.RadiansPerDegree
                let renderPerPixel = 2.0 * tan (verticalFov * 0.5) * radius / float h
                let realAt100 = targetPx * renderPerPixel / scale
                let nice = niceRoundDistance realAt100
                let px = nice * scale / renderPerPixel
                let px = if System.Double.IsNaN px || System.Double.IsInfinity px then targetPx else max 10.0 (min 400.0 px)
                px, formatMeters nice)
        let barPx = barInfo |> AVal.map fst
        let barLabel = barInfo |> AVal.map snd
        div {
            Class "scale-bar"
            div {
                Class "sb-bar"
                barPx |> AVal.map (fun px -> Some (Style [Width (sprintf "%.0fpx" px)]))
                span { Class "sb-cap sb-cap-l" }
                span { Class "sb-line" }
                span { Class "sb-cap sb-cap-r" }
            }
            div { Class "sb-label"; barLabel }
        }

    let orientationIndicator (model : AdaptiveModel) =
        let axisJson =
            model.Camera.view |> AVal.map (fun cv ->
                let vt = CameraView.viewTrafo cv
                let tr (v : V3d) = vt.Forward.TransformDir v
                let x = tr V3d.IOO
                let y = tr V3d.OIO
                let z = tr V3d.OOI
                let fmt (v : V3d) (name : string) (color : string) =
                    sprintf "{\"x\":%f,\"y\":%f,\"z\":%f,\"n\":\"%s\",\"c\":\"%s\"}" v.X v.Y v.Z name color
                sprintf "[%s,%s,%s]"
                    (fmt x "X" "#dc2626")
                    (fmt y "Y" "#16a34a")
                    (fmt z "Z" "#2563eb"))
        div {
            Class "orient-indicator"
            axisJson |> AVal.map (fun json -> Some (Attribute("data-axes", json)))
            Primitives.observedRender "data-axes" "[]" [
                "  var W = 60, H = 60, L = 22, cx = W/2, cy = H/2;"
                "  var svg = document.createElementNS(ns, 'svg');"
                "  svg.setAttribute('width', W); svg.setAttribute('height', H);"
                "  svg.setAttribute('viewBox', '0 0 ' + W + ' ' + H);"
                "  d.sort(function(a,b){return a.z - b.z;});"
                "  d.forEach(function(a){"
                "    var ex = cx + a.x * L, ey = cy - a.y * L;"
                "    var ln = document.createElementNS(ns, 'line');"
                "    ln.setAttribute('x1', cx); ln.setAttribute('y1', cy);"
                "    ln.setAttribute('x2', ex); ln.setAttribute('y2', ey);"
                "    ln.setAttribute('stroke', a.c); ln.setAttribute('stroke-width','2');"
                "    ln.setAttribute('stroke-linecap','round');"
                "    svg.appendChild(ln);"
                "    if(a.z > -0.2){"
                "      var tx = document.createElementNS(ns, 'text');"
                "      tx.setAttribute('x', cx + a.x * (L + 6));"
                "      tx.setAttribute('y', cy - a.y * (L + 6) + 3);"
                "      tx.setAttribute('fill', a.c);"
                "      tx.setAttribute('font-size','9');"
                "      tx.setAttribute('font-family','monospace');"
                "      tx.setAttribute('text-anchor','middle');"
                "      tx.textContent = a.n;"
                "      svg.appendChild(tx);"
                "    }"
                "  });"
                "  el.appendChild(svg);"
            ]
        }
