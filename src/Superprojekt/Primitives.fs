namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module Primitives =

    // Mesh identity — distinct vivid hues (teal · orange · purple · green ·
    // magenta · brown · sky · pink · olive), maximally separable at a glance.
    // Slot 7 is a bright SKY blue (#0ea5e9), deliberately far in lightness from
    // the dark teal — the old dark cyan #0e7490 was near-indistinguishable from
    // it. The hues stay clear of the diverging difference map's ends (#2151DB
    // blue / #C00206 red) and its near-white centre; gold stays the reference
    // accent. Identity rides on thin marks only.
    let meshPalette =
        [| C4b( 13uy,148uy,136uy); C4b(234uy, 88uy, 12uy); C4b(147uy, 51uy,234uy)
           C4b( 22uy,163uy, 74uy); C4b(192uy, 38uy,211uy); C4b(146uy, 64uy, 14uy)
           C4b( 14uy,165uy,233uy); C4b(219uy, 39uy,119uy); C4b( 77uy,124uy, 15uy) |]

    let c4bToV3d (c : C4b) = V3d(float c.R / 255.0, float c.G / 255.0, float c.B / 255.0)
    let c4bToRgbCss (c : C4b) = sprintf "rgb(%d,%d,%d)" (int c.R) (int c.G) (int c.B)
    let c4bToRgbaCss (c : C4b) (a : float) = sprintf "rgba(%d,%d,%d,%.2f)" (int c.R) (int c.G) (int c.B) a

    let meshPaletteV4d =
        meshPalette |> Array.map (fun c -> V4d(c4bToV3d c, 1.0))

    let meshColor (idx : int) = meshPalette.[((idx % meshPalette.Length) + meshPalette.Length) % meshPalette.Length]

    // Pin identity is NAME-ONLY: no pin colours, no glyphs. Every pin
    // mark and label uses this ONE near-black dark warm grey — deliberately not
    // pure #000 (the slice cells' data ink) and warmer than the slate UI text,
    // so pin marks stay recognisable without owning a hue family.
    let pinInk    = C4b(41uy, 37uy, 36uy)          // #292524
    let pinInkV3d = V3d(41.0 / 255.0, 37.0 / 255.0, 36.0 / 255.0)
    // The --ref-gold CSS token's F# mirror (#d4a106) — every render-side root
    // marker (3D bbox outline, tile footprint overlay) reads THIS, never a
    // re-derived gold.
    let refGoldV3d = V3d(212.0 / 255.0, 161.0 / 255.0, 6.0 / 255.0)
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

    // Linear-diverging difference colourmap (Coolwarm, Colorcet CET-D01 as
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

    // Friendly display names: drop the dataset prefix, then strip the longest common
    // prefix + suffix shared across the whole mesh set, so e.g. {job_0789, job_0791, …}
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
        Map.tryFind name (friendlyMap names) |> Option.defaultValue (meshLocal name)
    let parseFloat (s : string) =
        match System.Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let showWhen (v : aval<bool>) =
        v |> AVal.map (fun on -> if on then None else Some (Class "hidden"))

    let classWhen (cls : string) (v : aval<bool>) =
        v |> AVal.map (fun on -> if on then Some (Class cls) else None)

    let classWhenNot (cls : string) (v : aval<bool>) =
        v |> AVal.map (fun on -> if on then None else Some (Class cls))

    let compactToggle (labelText : string) (value : aval<bool>) (onToggle : unit -> unit) =
        div {
            Class "ct"
            classWhen "ct-on" value
            Dom.OnClick(fun _ -> onToggle ())
            span {
                Class "ct-box"
                value |> AVal.map (fun v -> if v then "✓" else "")
            }
            labelText
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

