namespace Superprojekt

open System
open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Rendering
open Aardvark.Application
open Superprojekt
open Aardvark.Dom

[<AutoOpen>]
module TimeUtilities =

    let private sw = System.Diagnostics.Stopwatch.StartNew()

    let now() = sw.MicroTime

type MouseEvent =
    {
        pixel     : V2d
        viewport  : V2d
        button    : MouseButtons
        pointerId : int
        alt       : bool
        ctrl      : bool
        shift     : bool
    }

type KeyEvent =
    {
        key   : Keys
        repeat : bool
        alt   : bool
        ctrl  : bool
        shift : bool
    }

module Integrator =

    let inline private dbl one = one + one

    let inline rungeKutta (f : ^t -> ^a -> ^da) (y0 : ^a) (h : ^t) : ^a =
        let twa : ^t = dbl LanguagePrimitives.GenericOne
        let half : ^t = LanguagePrimitives.GenericOne / twa
        let hHalf = h * half
        let k1 = h * f LanguagePrimitives.GenericZero y0
        let k2 = h * f hHalf (y0 + k1 * half)
        let k3 = h * f hHalf (y0 + k2 * half)
        let k4 = h * f h (y0 + k3)
        let sixth = LanguagePrimitives.GenericOne / (dbl twa + twa)
        y0 + (k1 + twa*k2 + twa*k3 + k4) * sixth

    let inline euler (f : ^t -> ^a -> ^da) (y0 : ^a) (h : ^t) : ^a =
        y0 + h * f LanguagePrimitives.GenericZero y0

    let rec integrate (maxDt : float) (f : 'm -> float -> 'm) (m0 : 'm) (dt : float) =
        if dt <= maxDt then
            f m0 dt
        else
            integrate maxDt f (f m0 maxDt) (dt - maxDt)

type OrbitMessage =
    | PointerDown of id : int * button : Button * isTouch : bool * pos : V2i
    | PointerUp   of id : int * isTouch : bool * V2i
    | PointerMove of id : int * button : Button * isTouch : bool * V2i
    | Wheel       of shift : bool * delta : V2d

    | Rendered
    | SetTargetCenter of user : bool * AnimationKind * V3d
    | SetTargetPhi    of user : bool * float
    | SetTargetTheta  of user : bool * float
    | SetTargetRadius of user : bool * float
    | SetTarget       of user : bool * center : V3d * radius : float * phi : float * theta : float

    | SetPhi    of float
    | SetTheta  of float
    | SetRadius of float
    | SetCenter of V3d

    | Set of center : V3d * radius : float * phi : float * theta : float

    | UpdateCenter of V3d

    | SetSpeed of float

    | Nothing
