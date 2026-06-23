#!/usr/bin/env python3
"""Decimate a dataset's meshes with MeshLab's Quadric Edge Collapse (with texture).

Same algorithm Blender's "Decimate (Collapse)" and MeshLab's
"Simplification: Quadric Edge Collapse Decimation (with texture)" use. UVs are
preserved (wedge texcoords are carried through the collapse via the extra
texture-coordinate quadric term), so the existing atlas keeps mapping correctly
and no re-texturing is needed.

By default writes a NEW sibling dataset (<dataset>_lowpoly) so the original is
untouched and the app lists both for A/B testing. Sidecar files (atlas .jpg,
.mtl, *centroid.txt, *bbox.txt) are copied verbatim — geometry positions are
unchanged by decimation, only the triangle count drops.

Usage:
  python3 tools/decimate.py JOB                  # -> data/JOB_lowpoly, keep 15% of faces
  python3 tools/decimate.py JOB --ratio 0.10     # keep 10%
  python3 tools/decimate.py JOB --out JOB_lp     # custom output dataset name
  python3 tools/decimate.py JOB --inplace        # overwrite originals (.obj backed up to .obj.orig)

Requires:  pip install pymeshlab
"""
import argparse, glob, os, re, shutil, subprocess, sys

import pymeshlab

DATA = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", "src", "Superserver", "data"))

IMG_EXT = (".jpg", ".jpeg", ".png")


def resize_image(src: str, dst: str, scale: float) -> tuple:
    """Downscale src by `scale` per axis into dst. Pillow if present, else macOS sips."""
    try:
        from PIL import Image
        im = Image.open(src)
        w, h = im.size
        nw, nh = max(1, round(w * scale)), max(1, round(h * scale))
        im.convert("RGB").resize((nw, nh), Image.LANCZOS).save(dst, "JPEG", quality=90)
        return (w, h), (nw, nh)
    except ImportError:
        pass
    info = subprocess.run(["sips", "-g", "pixelWidth", "-g", "pixelHeight", src],
                          capture_output=True, text=True).stdout
    w = int(re.search(r"pixelWidth: (\d+)", info).group(1))
    h = int(re.search(r"pixelHeight: (\d+)", info).group(1))
    nw = max(1, round(w * scale))
    subprocess.run(["sips", "--resampleWidth", str(nw), src, "--out", dst], capture_output=True)
    return (w, h), (nw, max(1, round(h * nw / w)))


def decimate(obj_in: str, obj_out: str, ratio: float) -> tuple[int, int]:
    ms = pymeshlab.MeshSet()
    ms.load_new_mesh(obj_in)
    before = ms.current_mesh().face_number()
    ms.meshing_decimation_quadric_edge_collapse_with_texture(
        targetperc=ratio,        # fraction of faces to KEEP
        extratcoordw=1.0,        # weight of the UV term — keeps texture coords aligned
        qualitythr=0.3,
        preserveboundary=False,  # border isn't precious here
        optimalplacement=True,
        planarquadric=True,      # collapse flat terrain harder, keep detail at features
        preservenormal=False)
    after = ms.current_mesh().face_number()
    ms.save_current_mesh(obj_out, save_wedge_texcoord=True)
    return before, after


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dataset", help="dataset folder under src/Superserver/data")
    ap.add_argument("--ratio", type=float, default=0.15, help="fraction of faces to KEEP, 0..1 (default 0.15)")
    ap.add_argument("--texscale", type=float, default=1.0, help="atlas downscale per axis, 0..1 (default 1.0 = unchanged; 0.25 = quarter size)")
    ap.add_argument("--out", default=None, help="output dataset name (default <dataset>_lowpoly)")
    ap.add_argument("--inplace", action="store_true", help="overwrite originals (.obj -> .obj.orig backup)")
    args = ap.parse_args()

    src = os.path.join(DATA, args.dataset)
    if not os.path.isdir(src):
        sys.exit(f"dataset not found: {src}")
    if not 0.0 < args.ratio < 1.0:
        sys.exit("--ratio must be between 0 and 1")

    dst = src if args.inplace else os.path.join(DATA, args.out or f"{args.dataset}_lowpoly")

    tot_b = tot_a = 0
    for meshdir in sorted(glob.glob(os.path.join(src, "*", ""))):
        name = os.path.basename(os.path.dirname(meshdir))
        objs = sorted(glob.glob(os.path.join(meshdir, "*.obj")))
        if not objs:
            continue
        tex = 0.0 < args.texscale < 1.0
        outdir = meshdir if args.inplace else os.path.join(dst, name)
        if not args.inplace:
            os.makedirs(outdir, exist_ok=True)
            for f in glob.glob(os.path.join(meshdir, "*")):
                if not f.lower().endswith(".obj"):
                    shutil.copy2(f, os.path.join(outdir, os.path.basename(f)))
        for obj in objs:
            out_obj = os.path.join(outdir, os.path.basename(obj))
            if args.inplace:
                shutil.copy2(obj, obj + ".orig")
            b, a = decimate(obj, out_obj, args.ratio)
            tot_b += b
            tot_a += a
            print(f"  {name}/{os.path.basename(obj)}: {b:,} -> {a:,} faces ({100 * a / max(1, b):.1f}%)")
        # Downscale atlases AFTER the mesh save — pymeshlab re-exports the full-res
        # texture on save, so this must run last to win. Resize via a temp + replace.
        if tex:
            for img in glob.glob(os.path.join(outdir, "*")):
                if not img.lower().endswith(IMG_EXT):
                    continue
                tmp = img + ".tmp.jpg"
                (ow, oh), (nw, nh) = resize_image(img, tmp, args.texscale)
                os.replace(tmp, img)
                print(f"  {name}/{os.path.basename(img)}: atlas {ow}x{oh} -> {nw}x{nh}")
    pct = 100 * tot_a / max(1, tot_b)
    print(f"TOTAL: {tot_b:,} -> {tot_a:,} faces ({pct:.1f}%)")
    if not args.inplace:
        print(f"wrote -> {dst}  (load it in the app as dataset '{os.path.basename(dst)}')")


if __name__ == "__main__":
    main()
