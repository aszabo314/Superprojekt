namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module Primitives =

    let meshPalette =
        [| C4b(228uy,26uy,28uy);  C4b(55uy,126uy,184uy); C4b(77uy,175uy,74uy)
           C4b(152uy,78uy,163uy); C4b(255uy,127uy,0uy);  C4b(255uy,255uy,51uy)
           C4b(166uy,86uy,40uy);  C4b(247uy,129uy,191uy);C4b(153uy,153uy,153uy) |]

    let c4bToV3d (c : C4b) = V3d(float c.R / 255.0, float c.G / 255.0, float c.B / 255.0)
    let c4bToRgbCss (c : C4b) = sprintf "rgb(%d,%d,%d)" (int c.R) (int c.G) (int c.B)

    let meshPaletteV4d =
        meshPalette |> Array.map (fun c -> V4d(c4bToV3d c, 1.0))

    let meshColor (idx : int) = meshPalette.[((idx % meshPalette.Length) + meshPalette.Length) % meshPalette.Length]

    // Pin palette — a separate qualitative set (ColorBrewer Dark2 + extensions),
    // visually distinct from the mesh palette (§C). Index-paired with the glyph set
    // so colour + shape redundantly code the same pin identity (preattentive,
    // greyscale- and colour-blind-robust).
    module PinPalette =
        let colors =
            [| C4b( 27uy,158uy,119uy); C4b(217uy, 95uy,  2uy); C4b(117uy,112uy,179uy)
               C4b(231uy, 41uy,138uy); C4b(102uy,166uy, 30uy); C4b(217uy,164uy,  6uy)
               C4b(166uy,118uy, 29uy); C4b(  8uy,145uy,178uy); C4b(124uy, 58uy,237uy)
               C4b(190uy, 24uy, 93uy) |]
        // Distinct Unicode silhouettes; index-paired with `colors`.
        let glyphs = [| "●"; "■"; "▲"; "◆"; "★"; "✚"; "▼"; "⬢"; "⬟"; "✦" |]
        let count = colors.Length
        let color (i : int) = colors.[((i % count) + count) % count]
        let glyph (i : int) = glyphs.[((i % glyphs.Length) + glyphs.Length) % glyphs.Length]

    // Pronounceable 2-char pin code = consonant + vowel, collision-checked against
    // names already taken (other pins' short names + the mesh numbers). Seeded by the
    // pin's guid hash, so it is effectively random yet deterministic per pin.
    module PinIdentity =
        let private consonants = "BDFGHJKLMNPRSTVWZ"
        let private vowels = "AEIOU"
        let shortName (taken : Set<string>) (seed : int) =
            let total = consonants.Length * vowels.Length
            let pick k =
                let k = ((k % total) + total) % total
                sprintf "%c%c" consonants.[k % consonants.Length] vowels.[k / consonants.Length]
            let start = (abs seed) % total
            let rec go i =
                if i >= total then pick start
                else
                    let cand = pick (start + i)
                    if Set.contains cand taken then go (i + 1) else cand
            go 0

    let c4bToHex (c : C4b) = sprintf "#%02x%02x%02x" c.R c.G c.B

    // Linear-diverging difference colourmap (§C — Kovesi CET-D style): blue (neg) →
    // neutral → red (pos). A near-zero perceptual boost (|t|^0.6) keeps small
    // deviations visible (no central flat-spot). Within ±lod → neutral (the LoD
    // gate); outside, ramp from the gate edge out to `range`. The SAME constants +
    // shape are mirrored in the FShade difference painters (FocusShaders / MeshShaders).
    module Diff =
        // RdYlBu-style diverging map: zero = light yellow (visible against the white
        // page — a grey/white centre vanished there), each sign runs through a vivid
        // mid hue to a dark end. `neutral` is reserved for NO SIGNAL (within the LoD
        // gate / no data), so grey now always means "nothing detectable", never "0".
        // Mirrored in MeshShaders (enc 1) + FocusShaders (mode 1) — keep in sync.
        let neutral = V3d(0.62, 0.63, 0.66)
        let zero    = V3d(1.0, 0.906, 0.541)
        let private posMid = V3d(0.957, 0.427, 0.263)
        let private posEnd = V3d(0.647, 0.0, 0.149)
        let private negMid = V3d(0.455, 0.678, 0.820)
        let private negEnd = V3d(0.192, 0.212, 0.584)
        let private ramp (v : float) (m : float) =
            let mid, e = if v >= 0.0 then posMid, posEnd else negMid, negEnd
            if m < 0.5 then zero + (mid - zero) * (m * 2.0)
            else mid + (e - mid) * ((m - 0.5) * 2.0)
        let colorV3 (lod : float) (range : float) (v : float) =
            let a = abs v
            if a <= lod then neutral
            else
                let t = clamp 0.0 1.0 ((a - lod) / max 1.0e-6 (range - lod))
                ramp v (t ** 0.6)
        let color (lod : float) (range : float) (v : float) =
            let c = colorV3 lod range v
            let b x = byte (clamp 0.0 255.0 (x * 255.0))
            C4b(b c.X, b c.Y, b c.Z)
        // Asymmetric signed range [lo ≤ 0, hi ≥ 0]: the zero yellow stays welded to 0,
        // each side normalized by its own end (same t^0.6 near-zero boost). Values
        // outside clamp to the end colours.
        let colorSignedV3 (lo : float) (hi : float) (v : float) =
            let t =
                if v >= 0.0 then clamp 0.0 1.0 (v / max 1.0e-6 hi)
                else clamp 0.0 1.0 (v / min -1.0e-6 lo)
            ramp v (t ** 0.6)
        // Value step (m) of the difference isolines: nice 1/2/5 step giving ~8 bands
        // over the signed span, so contour k sits at exactly k·step (0 included).
        let isoStep (lo : float) (hi : float) =
            let span = max 1.0e-6 (hi - lo)
            let raw = span / 8.0
            let mag = 10.0 ** floor (log10 raw)
            let n = raw / mag
            (if n < 1.5 then 1.0 elif n < 3.5 then 2.0 elif n < 7.5 then 5.0 else 10.0) * mag

    let shortName (name : string) =
        let mesh =
            let s = name.IndexOf('/')
            if s >= 0 then name.[s + 1 ..] else name
        if mesh.Length > 8 && mesh.[8] = '_' then
            let date = mesh.[..7]
            let si = mesh.LastIndexOf("_seg")
            if si > 0 then date + "_" + mesh.[si + 1 ..] else date
        else mesh

    let numbered (order : HashMap<string, int>) (name : string) =
        match HashMap.tryFind name order with
        | Some i -> sprintf "%d  %s" (i + 1) (shortName name)
        | None -> shortName name

    // Friendly display names: drop the dataset prefix, then strip the longest common
    // prefix + suffix shared across the whole roster, so e.g. {job_0789, job_0791, …}
    // reads {0789, 0791, …}. Trailing digits of the common prefix (and leading digits
    // of the common suffix) are kept, so a shared numeric id is never cut mid-number.
    let private meshLocal (name : string) =
        let s = name.IndexOf('/')
        if s >= 0 then name.[s + 1 ..] else name
    let private commonPrefixLen (a : string) (b : string) =
        let n = min a.Length b.Length
        let mutable i = 0
        while i < n && a.[i] = b.[i] do i <- i + 1
        i
    let private commonSuffixLen (a : string) (b : string) =
        let n = min a.Length b.Length
        let mutable i = 0
        while i < n && a.[a.Length - 1 - i] = b.[b.Length - 1 - i] do i <- i + 1
        i
    let friendlyMap (names : string list) : Map<string, string> =
        let locals = names |> List.map (fun n -> n, meshLocal n)
        match locals with
        | [] | [_] -> locals |> Map.ofList
        | _ ->
            let ls = locals |> List.map snd
            let lcp = ls |> List.reduce (fun a b -> a.Substring(0, commonPrefixLen a b))
            let lcs = ls |> List.reduce (fun a b -> a.Substring(a.Length - commonSuffixLen a b))
            // keep a shared numeric id intact: back the prefix off its trailing digits,
            // the suffix off its leading digits.
            let pre =
                let mutable e = lcp.Length
                while e > 0 && System.Char.IsDigit lcp.[e - 1] do e <- e - 1
                lcp.Substring(0, e)
            let suf =
                let mutable i = 0
                while i < lcs.Length && System.Char.IsDigit lcs.[i] do i <- i + 1
                lcs.Substring(i)
            locals |> List.map (fun (full, loc) ->
                let mutable r = loc
                if pre.Length > 0 && r.Length > pre.Length && r.StartsWith pre then r <- r.Substring(pre.Length)
                if suf.Length > 0 && r.Length > suf.Length && r.EndsWith suf then r <- r.Substring(0, r.Length - suf.Length)
                full, (if r = "" then loc else r))
            |> Map.ofList
    let friendlyName (names : string list) (name : string) =
        Map.tryFind name (friendlyMap names) |> Option.defaultValue (shortName name)
    let numberedFriendly (order : HashMap<string, int>) (names : string list) (name : string) =
        match HashMap.tryFind name order with
        | Some i -> sprintf "%d  %s" (i + 1) (friendlyName names name)
        | None -> friendlyName names name

    let private parseFloat (s : string) =
        match System.Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let showWhen (v : aval<bool>) =
        v |> AVal.map (fun on -> if on then None else Some (Class "hidden"))

    let showWhenNot (v : aval<bool>) =
        v |> AVal.map (fun on -> if on then Some (Class "hidden") else None)

    // Adds `cls` while the flag holds; the Not form adds it while the flag is clear.
    let classWhen (cls : string) (v : aval<bool>) =
        v |> AVal.map (fun on -> if on then Some (Class cls) else None)

    let classWhenNot (cls : string) (v : aval<bool>) =
        v |> AVal.map (fun on -> if on then None else Some (Class cls))

    let compactToggle (labelText : string) (value : aval<bool>) (onToggle : unit -> unit) =
        div {
            Class "ct"
            Dom.OnClick(fun _ -> onToggle ())
            span {
                Class "ct-box"
                value |> AVal.map (fun v -> if v then "■" else "□")
            }
            " " + labelText
        }

    let inlineSlider
            (labelText : string)
            (minV : float) (maxV : float) (stepV : float)
            (format : float -> string)
            (value : aval<float>)
            (onChange : float -> unit) =
        div {
            Class "is"
            span { Class "is-label"; labelText }
            input {
                Class "is-range"
                Attribute("type", "range")
                Attribute("min",  sprintf "%.6g" minV)
                Attribute("max",  sprintf "%.6g" maxV)
                Attribute("step", sprintf "%.6g" stepV)
                value |> AVal.map (fun v -> Some (Attribute("value", sprintf "%.6g" v)))
                Dom.OnInput(fun e -> parseFloat e.Value |> Option.iter onChange)
            }
            input {
                Class "is-value"
                Attribute("type", "text")
                value |> AVal.map (fun v -> Some (Attribute("value", format v)))
                Dom.OnChange(fun e -> parseFloat e.Value |> Option.iter onChange)
            }
        }

    let numberInput
            (labelText : string)
            (minV : float) (maxV : float) (stepV : float)
            (format : float -> string)
            (value : aval<float>)
            (onChange : float -> unit) =
        div {
            Class "is"
            span { Class "is-label"; labelText }
            input {
                Class "is-value"
                Attribute("type", "number")
                Attribute("min",  sprintf "%.6g" minV)
                Attribute("max",  sprintf "%.6g" maxV)
                Attribute("step", sprintf "%.6g" stepV)
                value |> AVal.map (fun v -> Some (Attribute("value", format v)))
                Dom.OnChange(fun e -> parseFloat e.Value |> Option.iter onChange)
            }
        }

    let inlineLogSlider
            (labelText : string)
            (minV : float) (maxV : float)
            (format : float -> string)
            (value : aval<float>)
            (onChange : float -> unit) =
        let toSlider v =
            let v = clamp minV maxV v
            log10 (v / minV) / log10 (maxV / minV) * 1000.0
        let fromSlider s =
            minV * (maxV / minV) ** (s / 1000.0)
        div {
            Class "is"
            span { Class "is-label"; labelText }
            input {
                Class "is-range"
                Attribute("type", "range")
                Attribute("min", "0")
                Attribute("max", "1000")
                Attribute("step", "1")
                value |> AVal.map (fun v -> Some (Attribute("value", sprintf "%.1f" (toSlider v))))
                Dom.OnInput(fun e -> parseFloat e.Value |> Option.iter (fun s -> onChange (fromSlider s)))
            }
            span { Class "is-value-ro"; value |> AVal.map format }
        }

    let compactButtonBar (items : (string * aval<bool> * (unit -> unit)) list) =
        div {
            Class "cbb"
            AList.ofList items
            |> AList.map (fun (label, isActive, onClick) ->
                button {
                    Class "cbb-btn"
                    classWhen "cbb-btn-active" isActive
                    Dom.OnClick(fun _ -> onClick ())
                    label
                })
        }

    // Attribute-driven SVG/DOM rendering via OnBoot JS (the Aardvark.Dom CE has no
    // yield!): parses the JSON attribute into `d`, runs `body`, re-renders on mutation.
    let observedRender (attr : string) (fallback : string) (body : string list) =
        OnBoot (
            [ "(function(){"
              "var el = __THIS__;"
              "var ns = 'http://www.w3.org/2000/svg';"
              "var last = '';"
              "function render(){"
              sprintf "var raw = el.getAttribute('%s') || '%s';" attr fallback
              "if(raw === last) return; last = raw;"
              "var d; try { d = JSON.parse(raw); } catch(e) { return; }"
              "el.innerHTML = '';" ]
            @ body @
            [ "}"
              "render();"
              sprintf "new MutationObserver(render).observe(el,{attributes:true,attributeFilter:['%s']});" attr
              "})();" ])

// Single-vs-double click discrimination for controls whose SINGLE click toggles
// state (matrix cell locate/back-out, 3D pin-dot select/deselect): a double-click's
// two leading clicks/taps would toggle twice before the dblclick fires. `single`
// defers the action one double-click window; `double` on the same key supersedes
// any pending single and runs immediately. Double handlers must still be written
// to END in the desired state (select + zoom, not toggle) — a slow double-click
// can let the deferred single fire in between. Controls with idempotent single
// clicks bind plain OnClick + OnDoubleClick instead.
module ClickGate =

    let private pending = System.Collections.Generic.Dictionary<string, System.Threading.CancellationTokenSource>()

    let single (key : string) (action : unit -> unit) =
        match pending.TryGetValue key with
        | true, cts -> cts.Cancel()
        | _ -> ()
        let cts = new System.Threading.CancellationTokenSource()
        pending.[key] <- cts
        Async.Start(async {
            do! Async.Sleep 350
            // State is read at fire time, not click time, so a superseded toggle
            // never acts on a stale snapshot.
            if not cts.IsCancellationRequested then action () }, cts.Token)

    let double (key : string) (action : unit -> unit) =
        match pending.TryGetValue key with
        | true, cts -> cts.Cancel(); pending.Remove key |> ignore
        | _ -> ()
        action ()

// Readiness-engine adapter: builds the engine input from individual model leaves
// (adaptive-performance rule — never depend on the whole record).
module ReadinessView =

    open FSharp.Data.Adaptive

    let input (model : AdaptiveModel) : aval<ReadinessInput> =
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let meshNamesVal = model.MeshNames |> AList.toAVal
        AVal.custom (fun t ->
            let pins = pinsVal.GetValue t
            let reg = model.Registration.GetValue t
            let names = meshNamesVal.GetValue t |> IndexList.toList
            let visible = model.MeshVisible.GetValue t
            let movingVisible =
                match reg.ReferenceMesh with
                | Some r ->
                    names |> List.filter (fun n ->
                        n <> r && (Map.tryFind n visible |> Option.defaultValue true))
                | None -> []
            let enabledPins =
                pins |> HashMap.toList
                |> List.choose (fun (_, p) ->
                    match ScanPin.correspondence p with
                    | Some c ->
                        let marked =
                            movingVisible
                            |> List.filter (fun m -> Map.containsKey m c.Anchors)
                            |> Set.ofList
                        Some {
                            RefAnchor     = c.RefAnchor |> Option.map (fun ra -> ra, 1.0)
                            Accepted      = marked
                        }
                    | _ -> None)
            {
                ReferenceMesh       = reg.ReferenceMesh
                VisibleMovingMeshes = movingVisible
                EnabledPins         = enabledPins
            })
