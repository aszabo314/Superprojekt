# Superprojekt — Application Description

A research prototype for interactive 3D inspection and annotation of geological mesh and pointcloud datasets. The app is a single-page web application that runs in the browser on both desktop and mobile, intended for use by domain experts who need to compare overlapping reconstructions of the same site, identify regions of disagreement, place annotated cross-section markers, and produce figures suitable for publication.

The app is styled as a light, high-contrast, print-friendly tool. Its goal is to be readable at first glance — colors, panels, and overlays are picked so that screenshots taken from the viewport drop straight into a report.

## What you see when the app loads

The screen is dominated by a large 3D viewport rendered against a soft blue-white gradient. While the dataset is loading, an Aardworx logo and animated spinner are shown. Once geometry arrives, the viewport frames the scene automatically.

Layered over the viewport:

- A **top action bar** with a hamburger menu button on the left, a dataset selector, the three pin-placement mode buttons (Profile, Plan, Auto), an Explore-mode toggle, a camera-reset button, a live world-coordinate readout, and a settings/debug gear.
- A **collapsible side panel** (toggled by the hamburger button) organized into tabs: **Scene**, **Overlay**, **Clip**, and **Pins**.
- **Floating overlays** anchored to the scene: a small coordinate-cross/orientation indicator in one corner, a dynamic scale bar in the other, and — when pins exist — one or more **floating pin diagrams** that hover next to their 3D anchor point.
- **Conditional overlays**: a fullscreen-mode badge listing mesh names and draw order, a circular "revolver" disk that lets you peek at neighbouring mesh layers under the cursor, and an Explore-mode tuning card.

Everything is reactive: dragging a slider, toggling a checkbox, or moving the cut plane updates the 3D view, the diagrams, and the relevant overlays in real time.

## Loading and switching datasets

The dataset dropdown in the top bar lists every dataset published by the server. Picking one loads its meshes (each dataset is a collection of related surfaces, e.g. multiple reconstructions of the same glacier or outcrop), recenters the camera on the combined extents, and resets the workspace — pin annotations, mesh-isolation state, filters, and Explore overlays are cleared so each dataset starts fresh. A default dataset is selected automatically on first load.

## Navigating the 3D scene

Standard orbit-camera controls work on both mouse and touch:

- **Drag** to orbit around the focus point.
- **Right-drag** (or two-finger drag on touch) to pan.
- **Scroll** or pinch to zoom.
- **Double-tap** (or double-click) on geometry to recenter the camera there.
- **Reset** button in the top bar returns to the framed overview.

Two transient inspection modes are bound to keys:

- Hold **Shift** to summon the **revolver disk** under the cursor — a circular cutout that reveals the next mesh layer underneath the front-most one. This is useful when several reconstructions of the same surface are stacked. Releasing Shift hides the disk again. The revolver can also be latched on from the GUI, in which case its center is fixed.
- Hold **Space** for **fullscreen review mode** — all panels disappear, leaving only the 3D scene and a small list of mesh names and their draw order. Useful for figure capture and uncluttered demonstration.

A small live readout in the corner shows the world-space coordinate currently under the cursor, so you always know where you are in the dataset's coordinate system.

## The Scene tab — choosing what to look at

The Scene tab is the primary mesh-control panel. Each mesh in the active dataset is listed by name with three controls:

- A **visibility toggle** (eye icon) hides or shows that mesh.
- A **solo button** isolates a single mesh, hiding everything else without losing the rest of your visibility selections.
- A **focus button** flies the camera to that mesh's bounding box.

Bulk **All / None** buttons at the top of the list flip every visibility toggle at once. A **rendering-style** picker switches between textured photorealistic surfaces, neutral shaded surfaces, and a flat white "study model" look. A **ghost silhouette** option renders hidden or clipped meshes as a faint translucent outline so the spatial relationship is still legible — its opacity is tunable.

The **scale** of a dataset can be adjusted here too, which is how the app handles datasets that ship in non-meter units.

## The Overlay tab — comparing meshes against each other

Where the Scene tab governs *which* meshes are drawn, the Overlay tab governs *how* they relate when more than one is visible:

- **Mesh order** controls which surface ends up on top when meshes overlap. The order can be cycled directly from the revolver disk too.
- **Difference rendering** turns on a heat-color overlay that visualizes the depth difference between front-most and back-most visible meshes per pixel. A min/max depth slider sets the world-space distance at which color saturation begins and ends, so the user can tune the color stretch to the magnitudes that actually matter for the dataset.
- **Revolver** and **fullscreen** toggles latch the corresponding inspection modes from the GUI rather than from key holds.

This is the tab a user lives in when juxtaposing two reconstructions of the same site to see where they disagree.

## The Clip tab — workspace clipping

The Clip tab carves the scene down to a rectangular region of interest. A master enable toggle turns clipping on, and three pairs of sliders define the X, Y, and Z bounds of the active clip box. The bounds are seeded from the dataset's union bounding box so the sliders always start in a usable range. Geometry outside the box is removed cleanly; if ghost silhouettes are on, the removed region appears as a faint outline so the user keeps a sense of context.

This is most useful when isolating a sub-feature of a large reconstruction (a single moraine, a particular terrace) for closer study.

## Explore mode — finding regions worth annotating

The **Explore** button on the top bar turns on a screen-space heatmap that highlights pixels where the visible meshes are both *steep* and *disagree with each other*. Steep regions are the geometrically interesting ones — cliff faces, fault scarps, vertical contacts — and depth disagreement between meshes flags the places where reconstructions don't quite line up.

A small floating Explore card lets the user tune the heatmap:

- **Steepness threshold** picks how flat a face has to be before it's filtered out.
- **Sensitivity** controls how much depth disagreement (in real-world meters, on a log scale) is needed before a pixel lights up.
- A **reference axis** choice (world up versus the current camera view) lets the user decide whether "steep" means physically vertical or simply oblique to the screen.
- A **highlight color** picker keeps the overlay readable against arbitrary surface textures.

Explore mode pairs naturally with **Auto-mode pin placement** (described below): the heatmap shows the user where to click, and the Auto pin then derives a plausible cylinder and cut plane from the local geometry automatically.

## Annotating with ScanPins

A **ScanPin** is the central annotation primitive. Conceptually each pin is:

1. A **selection prism** — a vertical cylindrical column placed somewhere in the scene, with a chosen radius and a chosen extent above and below its anchor point.
2. A **cut plane** that slices through that column, intersecting every visible mesh and producing a 2D cross-section.
3. A **stratigraphy column** — a sampled vertical record of where each mesh enters or leaves that cylinder, used to reason about layered structure.

Pins are placed using one of three modes selected in the top bar; clicking the active mode again cancels placement.

### Profile mode — vertical cuts along a chosen direction

Two clicks. The first picks the anchor point on a surface; an on-screen hint then asks for the second point, which sets the direction of the cut. The cut plane ends up vertical and oriented along that line — the obvious tool for "give me a vertical section across this feature."

### Plan mode — horizontal cuts at a chosen elevation

A click-and-drag gesture. The press point becomes the center, and the drag distance becomes the radius of the column's footprint. On release, the app probes the underlying surface at several points within the footprint and uses the median elevation to set the cut plane height — that is, the cut plane sits roughly *on* the local surface as a horizontal cross-section. This is the right tool for plan-view (top-down) analyses.

### Auto mode — single-click placement on data-driven hot spots

A single click. The app probes a small ray grid around the click point, weights the hits by steepness and proximity to the click, and derives a best-fit cylinder axis and cut-plane orientation from the dominant face direction in that neighborhood. While the cursor hovers in Auto mode, a translucent **ghost preview** shows the pin that would be placed if the user clicked now — radius, axis, and cut plane — so placement is essentially aim-and-confirm. If the area under the cursor isn't geometrically distinctive enough, no preview appears and clicking falls back to a placeholder pin.

### Refining a pin

After any placement, the pin enters an **adjustment** state and the side panel switches to a placement flyout with:

- **Radius** slider for the cylinder's footprint.
- **Length** sliders for how far the column extends above and below the anchor.
- **Cut plane mode** — vertical (rotate) or horizontal (slide up and down) — and a slider that drives that motion.
- **Ghost-clip controls** — Solo (only this pin's cylinder shows clipped geometry) and "+Cut" (also clip in front of the cut plane), so the user can drill into the column visually without losing the surrounding context entirely.
- **Commit** and **Discard** buttons. **Escape** also discards.

All sliders update the 3D view continuously, and the cut plane and stratigraphy diagrams refresh shortly after the user stops dragging.

The cut plane can also be manipulated **directly in 3D**: pressing on the visible cut-plane disk on a pin's cylinder and dragging rotates (in vertical mode) or slides (in horizontal mode) the plane in real time, while the orbit camera is suppressed for the duration of the drag.

## The Pins tab and the floating pin diagram

The **Pins** tab lists every pin in the workspace. Each row shows the pin's coordinates, a status dot (placement-in-progress versus committed), and per-pin controls: focus the camera on it, re-enter adjustment mode, or delete it. Selecting a pin from the list opens its **floating pin diagram**.

The pin diagram is a card-shaped overlay that floats next to the pin's 3D position by default. It can be:

- **Dragged** anywhere on the screen and stays where the user puts it.
- **Reattached** (pin icon in the header) so it once again tracks the 3D anchor.
- **Collapsed** to just its header bar to free up screen space.
- **Closed** (also deselects the pin).

Inside the card, the user gets three coordinated views of the same pin:

### The cut diagram (SVG cross-section)

A clean 2D plot of every mesh's intersection with the cut plane, drawn as polylines, each in the mesh's assigned color. Axes are labeled in real-world meters, ticks are picked to be readable, and a hover crosshair reports which mesh is under the cursor and at what coordinate. An **aspect** toggle switches between true 1:1 (so slopes match what you see in 3D) and a fitted view that fills the diagram. This is the figure that typically ends up in a publication.

### The stratigraphy diagram (cylindrical unwrap)

A second 2D plot showing what the pin's cylinder *contains*. The horizontal axis sweeps once around the cylinder (180 angular samples) and the vertical axis is height along the cylinder's axis. For each angular direction, the diagram plots which mesh surfaces enter and leave the column and at what elevation, with the gaps between successive surfaces — the "between-space" — shaded.

A **flat / normalized** toggle switches the vertical axis between true elevation in world coordinates (so layers across the cylinder line up at their real heights) and per-column normalized depth (so the geometry of the column itself is the focus). A **between-space** toggle highlights, on hover, a continuous gap volume that propagates around and through the cylinder via a flood-fill on overlapping bracket pairs — it answers "which mesh layers bound this hollow?". Clicking pins the highlight; the labels of the bracketing mesh pair are shown above the diagram. The same continuous gap is rendered as a translucent volume in the 3D scene, visible through every mesh, so the diagram and the 3D viewport share the same hover state.

### The core sample (mini 3D inspector)

A small 3D viewport embedded inside the same card, showing only the geometry inside the pin's cylinder, rotated so the cylinder's axis is straight up. This is the "rock core" view of the column — you can rotate it around its vertical axis with horizontal drag, pan along its axis with vertical drag, and zoom with scroll. A side / top view toggle is exposed in the UI. The cut plane and prism wireframe appear as overlays in this mini view, so the user can correlate the 2D diagrams with the actual 3D fragment they describe.

## Filtering by point query

Independent of the pin workflow, users can interrogate the geometry directly. **Ctrl-click** (or long-press on touch) on a surface fires a sphere query against every visible mesh at that point, and the resulting set of nearby vertices is highlighted. This is a quick "what's here?" probe, distinct from the more elaborate ScanPin annotation flow.

## Settings, diagnostics, and debug overlay

A gear button in the top bar opens a popover with secondary controls: the global reference-axis choice (used by Explore mode and pin placement), camera speed, dataset-level metadata (combined bounds, common centroid, per-mesh centroids), and a scrollable rolling **debug log** of the last operations. The debug overlay is intended for the developer-and-power-user audience; nothing here is required to use the app.

## Typical workflows

The app's intended usage flows compose the building blocks above:

1. **Compare two reconstructions of the same site.** Load the dataset, leave both meshes visible, turn on **difference rendering** in the Overlay tab, optionally add **ghost silhouettes** so hidden parts stay legible, and use the **revolver disk** (Shift) to peek at the under-layer wherever the heatmap calls attention.
2. **Find and explain a feature.** Switch on **Explore mode**, scan the heatmap for steep, disagreeing regions, then drop a **Profile** or **Auto** pin on the most interesting hot spot. Refine its radius and cut plane. Open the floating pin diagram for the publication-ready cross-section.
3. **Document a stratigraphy column.** Place a **Plan** pin centered on a feature, raise its top and lower its bottom to span the layers of interest, and read off the between-space volumes from the stratigraphy diagram. Pin the bracketing pair that matters and capture the figure.
4. **Carve down to a region of interest.** Use the **Clip** tab to trim the scene down to a sub-volume. Pins and diagrams continue to work inside the clip box, so a user can isolate one moraine or terrace and annotate it without the rest of the dataset cluttering the view.
5. **Capture figures.** Hold **Space** for fullscreen mode (or set the panels aside manually) and screenshot the viewport plus the floating pin diagrams together — the layout is designed to compose into a single publishable image.

## What the app is *not*

The current build is a research prototype. Some workflow steps are intentionally left as placeholders pending further work — for example, a polished arcball gizmo for pin axis manipulation, deserialization of saved annotations, a top-down core-sample mode, summary mesh generation, and an end-to-end report exporter are not yet wired up, even though the data needed to produce them is already in place. What *is* present is the full inspection and annotation pipeline: load, explore, clip, juxtapose, place pins, refine, read out cross-sections and stratigraphy, and capture.
