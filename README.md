# Infinite Grass for Unity URP

Procedural, GPU-generated grass with explicit placement, editable density maps, geometric LOD, terrain color matching, and optional screen-space contact shadows.

**2.0 preview:** this branch targets **Unity 6.6 / URP 17.6**. Unity 6.7 is a separate validation target. The new rendering path has source and mathematical checks plus authored EditMode tests, but has **not yet been imported, built, or visually profiled in Unity**. See [validation and known limits](Documentation~/Validation.md) before adopting it in a production project.

## Install and set up

1. In Unity 6.6, install this Git package through Package Manager. For this implementation branch, use:
   `https://github.com/coen22/com.cone.grass.git#codex/grass-urp6-placement-contact-shadows`
2. Use a URP renderer with RenderGraph enabled. Add **Grass Data Renderer Feature** to its Renderer Features.
3. Assign `GrassPositionsCompute.compute` and the included `GrassHeightMapMat` to the feature. The height layer remains relevant to the legacy mesh surface path and unassigned mesh-surface fallback.
4. Add one **Infinite Grass Renderer** component to the scene and assign the included grass blade material, or a copy of it.
5. Leave **Placement Mode** at **Authored Areas**, add **Cone > Grass > Placement Area** to a GameObject, and assign its supporting **Terrain**.
6. Choose **Circle** or **Box**, set its size, density, and edge falloff, and position it where grass should grow. Rotating around Y and scaling X/Z change the patch.
7. To match the ground, assign the same **TerrainLayer** used below the grass and raise **Ground Color Strength**. The renderer provides overall blend strength and normalized blend height.

Authored terrain areas sample their assigned TerrainData directly, including holes. They do not need a grass layer on the Terrain. Areas remain constrained to their own footprint, even when a supporting Terrain is assigned. Enable **Use Terrain Bounds** only for a map intended to cover the entire terrain.

A positive area mask means **black = no grass, white = full grass**. Missing masks and empty painted assets produce no grass. Overlapping area density uses the maximum coverage, so overlap does not double the population.

### Painting individual spots

Select a Placement Area and create a persistent density asset in its inspector. Use the Scene brush to paint, hold **Shift** to erase temporarily, or select erase mode. Radius, strength, and hardness control the brush. Each stroke supports Undo/Redo; changes are saved with the asset.

Assign a Terrain or an explicit paint collider to hit the intended surface. The brush uses a horizontal plane only when no surface is assigned. The density asset stores editable bytes and derives a reusable linear R8 texture. It is not dependent on a texture being CPU-readable at runtime.

For external density maps, use a linear 2D texture with density in its red channel. Clamp mapping to the intended area. The generic core also accepts GPU textures; the producer must call `MarkDirty()` when its contents change without changing its texture reference.

### Mesh surfaces and legacy scenes

**Legacy Surface Layer** retains the broad layer-driven workflow. Select the mesh surface layer on the renderer feature. Mesh height capture stores world Y and uses vertex-color red as fractional surface eligibility. Color/mask/slope modifiers remain available.

The included old Sample Scene explicitly selects legacy mode. Existing user scenes should choose legacy mode during migration, or add Placement Areas and select their supporting surfaces. New renderers default to authored areas.

For an authored mesh patch, assign its **Paint Surface** collider with an associated Renderer. That explicit Renderer is captured independently of the layer mask. Patches without an assigned surface use the feature's height-layer fallback.

Call `InfiniteGrassRenderer.Instance.RefreshGrassData()` after changing a cached mesh surface, or disable **Cache Surface Data** while it moves. Authored terrain/mask changes invalidate through the placement components. Refresh after spawning new modifier renderers so the cached capture inventory includes them.

## MicroVerse spline workflow

Install the **MicroVerse Mask Bridge** sample from Package Manager. The sample adds no mandatory MicroVerse assembly dependency and contains no proprietary MicroVerse code.

1. Create a MicroVerse **Spline Area** for the grass region.
2. Use that same spline area and compatible filters/falloff for a **Texture Stamp**, painting the desired grass TerrainLayer.
3. Use the same region for a **positive Mask Stamp**, writing density to a saved **MaskTarget**.
4. Add a **Grass MicroVerse Bridge** and its adjacent Placement Area for each affected Terrain.
5. In the bridge inspector, choose the MaskTarget asset, the explicit generated texture name for that Terrain, and the same TerrainLayer.
6. Refresh the saved mask and save the scene. Spline edits then update through asset changes and optional editor polling. For a clean standalone build, resolve and save all mappings first.

The bridge rebinds uniquely named texture subassets after replacement, rejects ambiguous/missing/sRGB outputs, and clears stale coverage on failure. It never guesses which tile belongs to a terrain.

This first integration consumes **saved mask outputs**. Native MicroVerse generation-complete/cancellation hooks require verification against the installed MicroVerse version and are not implemented. The editor poll is eventually consistent; it cannot identify a native generation transaction. See the [sample guide](Integrations~/MicroVerse/README.md) for refresh, build, and test details.

## Terrain and blade blending

The ground capture samples unlit albedo using the selected TerrainLayer's world tiling, tile offset, and URP diffuse remap scale. Blades blend their lower color and diffuse response toward that ground color. Color capture uses premultiplied coverage, then decodes it before shading to avoid dark fringes along filtered area edges.

**Ground Blend Height** is a fraction of blade height; the root transition remains consistent between geometry LODs. The optional material setting **Use Ground Normal** reconstructs a normal from the cached world-height map and helps align root lighting on slopes. It costs extra height samples and is off by default.

A single TerrainLayer does not reproduce a mixture of several terrain layers or an arbitrary custom terrain material. For those areas, supply a density-aligned **Ground Color Texture** containing the final blended **albedo**, with no baked direct lighting or shadows. That texture overrides the selected layer. A native full-terrain-material albedo baker is not included.

The MicroVerse Texture Stamp remains visible as distant blades thin out. Keep its boundary and the positive density mask aligned; changing only one stamp's filters or falloff creates a visible mismatch.

## URP contact shadows

Enable **Contact Shadows > Enabled** on Infinite Grass Renderer. The effect supports contacts between indirect grass and visible opaque scene surfaces using the main directional light.

| Setting | Initial value | Purpose |
|---|---:|---|
| Strength | 0.35 | Amount of contact darkening |
| Ray length | 0.75 m | Maximum local search length |
| Steps | 8 | Quality/cost of the depth search |
| Bias | 0.03 m | Reduces self-intersection |
| Thickness | 0.15 m | Tolerance around sampled depth |
| Maximum distance | 50 m | Limits contacts to nearby receivers |

The feature requests scene depth and draws a dedicated grass depth/coverage pass with matching deformation. Contact evaluation reads those depths and multiplies the existing camera color attachment through fixed-function blending. It preserves MSAA color samples and alpha, and does not sample the color attachment it writes. Disabled contacts schedule no contact passes or intermediates.

This is a **custom screen-space approximation**. Unity's [pipeline comparison](https://docs.unity3d.com/6000.6/Documentation/Manual/render-pipelines-feature-comparison.html) lists built-in contact shadows for HDRP, not URP. Off-screen and hidden casters are unavailable. Only the nearest grass depth is stored, so fractional/MSAA silhouettes are approximate. The final color multiplication also attenuates ambient/emissive contributions. It supplements normal main-light shadow reception; it does not add grass to the directional light's shadow atlas.

## Reducing aliasing

The new path combines several controls:

- Stable world-space seeds for roots, density decisions, and LOD selection.
- Fractional population coverage near density transitions.
- Real geometry LOD with a stable transition band.
- A projected **Minimum Pixel Width**, with proportional coverage compensation so widening thin blades does not simply make the field denser.
- Analytic blade-edge coverage.
- **Alpha to Coverage** enabled only when the actual camera target uses MSAA; deterministic coverage discard otherwise.
- A distance range that fades narrow specular highlights.
- Shared deformation and coverage for color and contact depth.

Start by checking 4x MSAA, a one-pixel minimum width, and restrained distant width expansion in the target scene. Compare camera pans and wind motion with contacts both on and off. Unity documents that [AlphaToMask requires MSAA](https://docs.unity3d.com/6000.6/Documentation/Manual/writing-shader-alpha-to-mask.html); enabling it on a single-sample target has platform-dependent results.

**TAA motion vectors are not implemented.** The custom indirect draw and wind deformation need a proper previous-frame motion-vector path before complete TAA support can be claimed. Camera-only vectors cannot describe animated blades. FXAA/SMAA may help the final image but do not replace stable geometry and coverage.

## Rendering and performance changes

Position buffers, per-LOD indirect arguments, counters, and capture targets belong to individual camera resources and persist between frames. Buffer capacity changes rebuild resources deliberately; optional count previews use asynchronous readback.

At the default five subdivisions, the new meshes use:

| LOD | Vertices per blade | Triangles per blade |
|---|---:|---:|
| Near | 13 | 11 |
| Middle | 7 | 5 |
| Far | 3 | 1 |

The old near mesh duplicated row vertices and used 23 vertices. Its distance deformation retained all 11 triangles. The new near mesh shares rows; middle/far draws use separate meshes and independent GPU queues.

Generation checks dispatch boundaries, surface/area coverage, distance density, and a conservative frustum radius before writing bounded queues. Distance density remains full only to **Full Density Distance**, then fades toward **Draw Distance**. Overflow is safely dropped and available in diagnostics. Tune queue weights/capacity if diagnostics show overflow.

Static placement and height captures reuse data until their mapping or source revision changes. Different supporting surfaces keep separate positive-density maps to prevent coverage leaking between their dispatches. Moving modifiers can refresh separately. **Texture Update Threshold** controls capture recentering, and **Capture Resolution** trades small boundary detail against capture memory/work. Increase **Culling Padding** to cover unusually tall, wide, or strongly bent grass.

These are structural reductions, **not measured frame-rate claims**. The richer fragment lighting, extra ground samples, and enabled contact shadows also cost GPU time. Profile the target scene at equal visual coverage. GPU Resident Drawer does not automatically optimize these custom indirect draws.

## Migration notes

- Package baseline changes from the inconsistent Unity 2021.3/URP 17.1 declaration to Unity 6.6/URP 17.6.
- This is a `2.0.0-preview.1` API preview.
- New components use **Authored Areas**. Set **Legacy Surface Layer** explicitly for previous layer-based scenes.
- `_GrassPositions.w` stores **coverage**, not camera distance. Custom blade shaders must compute distance from the world pivot.
- `ArgsBuffer` and the synchronous debug `Buffer` fields are removed. The renderer feature owns per-camera resources. Use `VisibleGrassCount` and `OverflowGrassCount` for asynchronous diagnostics.
- `GetGrassMeshCache()` remains a near-mesh compatibility accessor; `GetLodMeshes()` returns the three shared meshes.
- Custom capture shaders should use `_GrassCaptureVP` and put their capture `LightMode` tag and name on the actual pass.
- Custom blade shaders need the `GrassForward` pass and `_GrassInstanceOffset`; contact support additionally needs the `GrassContactDepth` contract.
- Main-light and scene ambient-probe SH lighting are the default; the draw explicitly supplies its SH coefficients and does not sample local light probes per grass root. Optional additional-light shading targets Forward+ clustered lighting; ordinary Forward per-object light indices are unavailable to this indirect draw.
- The intended initial camera target is single-view Game/Scene base cameras. Overlay, reflection, preview, and XR cameras are excluded. Deferred, camera stacking variants, TAA, and additional graphics APIs require explicit validation.

## Implementation tracking

| Issue | Workstream |
|---|---|
| [#27](https://github.com/coen22/com.cone.grass/issues/27) | GPU lifetime, compute safety, RenderGraph dependencies |
| [#28](https://github.com/coen22/com.cone.grass/issues/28) | Generation, capture memory, geometric LOD, profiling |
| [#29](https://github.com/coen22/com.cone.grass/issues/29) | Unity 6.6 baseline and 6.7 validation |
| [#30](https://github.com/coen22/com.cone.grass/issues/30) | Authored placement and painting |
| [#31](https://github.com/coen22/com.cone.grass/issues/31) | MicroVerse spline masks and terrain textures |
| [#32](https://github.com/coen22/com.cone.grass/issues/32) | Grass/scene contact shadows |
| [#33](https://github.com/coen22/com.cone.grass/issues/33) | Terrain/root blending |
| [#34](https://github.com/coen22/com.cone.grass/issues/34) | Aliasing and temporal stability |

Issues stay open until their Unity acceptance checks pass. See [validation](Documentation~/Validation.md) and the [MicroVerse guide](Integrations~/MicroVerse/README.md).

## Original demo

The [original preview video](https://youtu.be/NwVtPIxUuCY) shows the earlier renderer and interaction effects. It is not a benchmark or visual validation of this preview branch.

Original package author: Youssef Afella. See [LICENSE](LICENSE).
