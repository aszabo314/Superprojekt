namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module Primitives =

    // Mesh identity — the cool/earth family (teal · ochre · slate · cyan · brown …).
    // Identity hues deliberately avoid everything the scalar gradients own (§B1):
    // red/blue (diverging + variance + displacement + range), green/yellow
    // (incidence/shape), pale grey (no-data) and gold (reference). Pins own the
    // complementary vivid warm/purple family. Identity rides on thin marks only.
    let meshPalette =
        [| C4b( 15uy,118uy,110uy); C4b(180uy, 83uy,  9uy); C4b( 71uy, 85uy,105uy)
           C4b( 14uy,116uy,144uy); C4b(113uy, 63uy, 18uy); C4b( 19uy, 78uy, 74uy)
           C4b( 51uy, 65uy, 85uy); C4b(146uy, 64uy, 14uy); C4b( 21uy, 94uy,117uy) |]

    let c4bToV3d (c : C4b) = V3d(float c.R / 255.0, float c.G / 255.0, float c.B / 255.0)
    let c4bToRgbCss (c : C4b) = sprintf "rgb(%d,%d,%d)" (int c.R) (int c.G) (int c.B)
    let c4bToRgbaCss (c : C4b) (a : float) = sprintf "rgba(%d,%d,%d,%.2f)" (int c.R) (int c.G) (int c.B) a

    let meshPaletteV4d =
        meshPalette |> Array.map (fun c -> V4d(c4bToV3d c, 1.0))

    let meshColor (idx : int) = meshPalette.[((idx % meshPalette.Length) + meshPalette.Length) % meshPalette.Length]

    // Pin identity — the vivid warm/purple family (orange · fuchsia · violet ·
    // pink …), disjoint from both the scalar-gradient hues and the cool/earth mesh
    // family (§B1). Identity = colour + the 2-char ShortName, shown as a
    // colour-filled element with the name inside.
    module PinPalette =
        let colors =
            [| C4b(234uy, 88uy, 12uy); C4b(192uy, 38uy,211uy); C4b(124uy, 58uy,237uy)
               C4b(219uy, 39uy,119uy); C4b(134uy, 25uy,143uy); C4b(162uy, 28uy,175uy)
               C4b(190uy, 24uy, 93uy); C4b(109uy, 40uy,217uy); C4b(194uy, 65uy, 12uy)
               C4b(147uy, 51uy,234uy) |]
        let count = colors.Length
        let color (i : int) = colors.[((i % count) + count) % count]

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

    // Luminance greyscale of an identity colour — while a pin is selected, the
    // OTHER pins' 3D/focus marks drop to this so the selection owns the colour.
    let c4bToGrey (c : C4b) =
        let l = byte (clamp 0.0 255.0 (0.299 * float c.R + 0.587 * float c.G + 0.114 * float c.B))
        C4b(l, l, l)
    let v3dToGrey (v : V3d) =
        let l = 0.299 * v.X + 0.587 * v.Y + 0.114 * v.Z
        V3d(l, l, l)

    // Linear-diverging difference colourmap (§C — Coolwarm, Colorcet CET-D01 as
    // shipped by Maple): blue (neg) → near-white → red (pos). A near-zero perceptual
    // boost (|t|^0.6) keeps small deviations visible (no central flat-spot).
    // The SAME constants + shape are mirrored in the FShade difference painters
    // (FocusShaders / MeshShaders).
    module Diff =
        // Coolwarm anchors sampled from CET-D01 at 0 / ¼ / ½ / ¾ / 1: zero = the
        // ramp's near-white centre, each sign runs through its mid hue to a saturated
        // end. `neutral` is reserved for NO SIGNAL (within the LoD gate / no data —
        // the shader painters' gate colour), so grey always means "nothing
        // detectable", never "0".
        // Mirrored in MeshShaders (enc 1) + FocusShaders (mode 1) — keep in sync.
        let neutral = V3d(0.62, 0.63, 0.66)
        let zero    = V3d(0.930, 0.907, 0.917)
        let private posMid = V3d(0.906, 0.549, 0.464)
        let private posEnd = V3d(0.752, 0.008, 0.022)
        let private negMid = V3d(0.627, 0.612, 0.908)
        let private negEnd = V3d(0.128, 0.316, 0.858)
        let private ramp (v : float) (m : float) =
            let mid, e = if v >= 0.0 then posMid, posEnd else negMid, negEnd
            if m < 0.5 then zero + (mid - zero) * (m * 2.0)
            else mid + (e - mid) * ((m - 0.5) * 2.0)
        // Asymmetric signed range [lo ≤ 0, hi ≥ 0]: the zero centre stays welded to 0,
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

    // Immediate action that also supersedes any pending deferred single on `key`
    // — for IDEMPOTENT controls sharing a click neighbourhood with gated toggles
    // (matrix row/column heads vs the gated cells): without this, a quick
    // cell-then-head sequence lets the cell's deferred single fire last and
    // override the head's selection.
    let now = double

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
            let moving =
                match reg.ReferenceMesh with
                | Some r -> names |> List.filter (fun n -> n <> r)
                | None -> []
            let enabledPins =
                pins |> HashMap.toList
                |> List.choose (fun (_, p) ->
                    match ScanPin.correspondence p with
                    | Some c ->
                        let marked =
                            moving
                            |> List.filter (fun m -> Map.containsKey m c.Anchors)
                            |> Set.ofList
                        Some {
                            RefAnchor     = c.RefAnchor |> Option.map (fun ra -> ra, 1.0)
                            Accepted      = marked
                        }
                    | _ -> None)
            {
                ReferenceMesh = reg.ReferenceMesh
                MovingMeshes  = moving
                EnabledPins   = enabledPins
            })
