# Infinite Grass for Unity URP

Procedural, GPU-generated grass with explicit placement, editable density maps, geometric LOD, terrain color matching, and optional screen-space contact shadows.

**2.0 preview:** this package targets **Unity 6.6 / URP 17.6**. Unity 6.7 is a separate validation target. GitHub runs package checks and Roslyn C# 9 syntax validation; the Unity job requires configured licensing credentials. The new rendering path has **not yet been imported, built, or visually profiled in Unity**. See [validation and known limits](Documentation~/Validation.md) for the exact evidence and test commands.

## Install and set up

1. In Unity 6.6, install this Git package through Package Manager:
   `https://github.com/coen22/com.cone.grass.git#main`
2. Use a URP renderer with RenderGraph enabled. Add **Grass Data Renderer Feature** to its Renderer Features.
3. Assign `GrassPositionsCompute.compute` and the included `GrassHeightMapMat` to the feature. The height layer remains relevant to the legacy mesh surface path and unassigned mesh-surface fallback.
4. Add one **Infinite Grass Renderer** component to the scene and assign the included grass blade material, or a copy of it.
5. Leave **Placement Mode** at **Authored Areas**, add **Cone > Grass > Placement Area** to a GameObject, and assign its supporting **Terrain**.
6. Choose **Circle** or **Box**, set its size, density, and edge falloff, and position it where grass should grow. Rotating around Y and scaling X/Z change the patch.
7. To match the ground, assign the same **TerrainLayer** used below the grass and raise **Ground Color Strength**. The renderer provides overall blend strength and normalized blend height.

Authored terrain areas sample their assigned TerrainData directly, including holes. They do not need a grass layer on the Terrain. Areas remain constrained to their own footprint, even when a supporting Terrain is assigned. Enable **Use Terrain Bounds** only for a map intended to cover the entire terrain.

Adjacent terrains assign roots through world-space bounds that include the lower edge and exclude the upper edge. This keeps fractional-origin seams from generating duplicate roots or dropping a valid root when normalized texture coordinates round to one.

CPU grid bounds include float32 rounding in candidate coordinates. The shader preserves the written root calculation order so compiler reassociation cannot move a candidate beyond that interval. Upgrading from the earlier optimized calculation can shift existing roots by floating-point rounding amounts, especially at large world coordinates; the world-cell seeds are unchanged.

Sparse tile occupancy uses those same rounded root endpoints plus the capture filter margin. Its world bounds round outward, including when a tile is clipped to the camera grid, so the occupancy check cannot discard a retained boundary root through a different coordinate calculation. Source expansion and its subsequent camera intersection also round outward before tile enumeration; a small filter margin must survive even when subtracting a large center and extent produces a boundary near zero.

A positive area mask means **black = no grass, white = full grass**. Missing masks and empty painted assets produce no grass. Overlapping area density uses the maximum coverage, so overlap does not double the population. Areas whose computed footprint overflows or collapses at the current coordinate precision stop contributing coverage and recover when usable coordinates are restored.

### Painting individual spots

Select a Placement Area and create a persistent density asset in its inspector. Use the Scene brush to paint, hold **Shift** to erase temporarily, or select erase mode. Radius, strength, and hardness control the brush. Each stroke supports Undo/Redo; changes are saved with the asset.

Long cursor segments are clipped to the map plus brush overlap before applying the per-event dab limit. Travel outside a small patch therefore does not spread its painted dabs apart. Undo or Redo ends an active stroke; start a new stroke to continue painting after a history change.

Assign a Terrain or an explicit paint collider to hit the intended surface. The brush uses a horizontal plane only when no surface is assigned. The density asset stores editable bytes and derives a reusable linear R8 texture. Small strokes update occupancy counts and upload a bounded pixel rectangle through a reusable staging texture where regional copies are supported. Unsupported copies and full edits use the authoritative whole-map data.

For external density maps, use a linear 2D texture with a normalized or floating-point red channel. Alpha-only, integer and depth formats contribute no coverage. Clamp mapping to the intended area. The generic core also accepts GPU textures; producers must create and populate their RenderTextures before use. An uncreated or released density texture contributes no grass. Call `MarkCoverageDirty()` after unreported density writes or `MarkGroundColorDirty()` after color writes. `MarkDirty()` remains the complete refresh path. Destroying an explicitly assigned density asset keeps coverage absent until that binding is deliberately cleared or replaced.

Density and ground-color captures require an ordinary 2D texture binding. A RenderTexture with `antiAliasing > 1` and `bindTextureMS` enabled contributes no density, or uses the existing layer/tint fallback for ground color. Disable Bind Texture MS or supply a resolved texture from the producer. Automatically resolved MSAA textures remain eligible. Sampling-configuration changes are observed even when a producer releases and recreates the same object between area updates; the area preserves producer-owned storage and assignments.

`SetDensityAsset(null, ...)` clears a missing assigned asset as well as a live binding. Rebinding or disabling an area removes its old density-change subscription even when the asset's native object has already been destroyed. Configuring an external producer explicitly clears that painted override and invalidates the restored coverage immediately.

### Mesh surfaces and legacy scenes

**Legacy Surface Layer** retains the broad layer-driven workflow. Select the mesh surface layer on the renderer feature. Mesh height capture stores world Y and uses vertex-color red as fractional surface eligibility. Color/mask/slope modifiers remain available.

The included old Sample Scene explicitly selects legacy mode. Existing user scenes should choose legacy mode during migration, or add Placement Areas and select their supporting surfaces. New renderers default to authored areas.

For an authored mesh patch, assign its **Paint Surface** collider with an associated Renderer. That explicit Renderer is captured independently of the layer mask. Patches without an assigned surface use the feature's height-layer fallback.

An explicit mesh binding stops coverage if its collider is destroyed, its object becomes inactive, its scene is unloaded or opened in a preview, or its associated Renderer is removed, disabled or forced off. Mesh support also needs indexed geometry in a submesh with a material slot; the height pass supplies its own material, so a null slot is allowed. Clear the binding deliberately to return to the height-layer fallback. A disabled physics collider can still identify an active visible Renderer on the same object or a parent.

Assigned mesh transforms, bounds, mesh replacement, material-slot count and enable/disable changes invalidate their surface capture. Call `InfiniteGrassRenderer.Instance.RefreshGrassData()` after other cached mesh edits, or disable **Cache Surface Data** for continuously deformed surfaces whose bounds remain unchanged. Authored terrain/mask changes invalidate through the placement components. Refresh after spawning new runtime modifier renderers or changing their materials so the shared capture inventory includes them; editor hierarchy and scene changes refresh discovery automatically.

The shared inventory retains existing inactive modifiers and terrains for later activation. With **Update Modifiers Every Frame** enabled, an activated modifier becomes eligible without another scene search. Terrain activation updates the surface set, and active generated terrains retain their existing support. Legacy cached mesh changes still follow the refresh policy above.

## MicroVerse spline workflow

Install the **MicroVerse Mask Bridge** sample from Package Manager. The sample adds no mandatory MicroVerse assembly dependency and contains no proprietary MicroVerse code.

1. Create a MicroVerse **Spline Area** for the grass region.
2. Use that same spline area and compatible filters/falloff for a **Texture Stamp**, painting the desired grass TerrainLayer.
3. Use the same region for a **positive Mask Stamp**, writing density to a saved **MaskTarget**.
4. Add a **Grass MicroVerse Bridge** and its adjacent Placement Area for each affected Terrain.
5. In the bridge inspector, choose the MaskTarget asset, the explicit generated texture name for that Terrain, and the same TerrainLayer.
6. For mixed grass/dirt edges, enable **Bake Terrain Ground Albedo** and choose an output asset for each Terrain. Bake, refresh the saved mask, and save the scene.
7. Spline edits update through asset changes and optional editor polling. The build preflight checks saved bindings before both clean and incremental player builds and reports stale inputs with the corrective action.

The bridge rebinds uniquely named texture subassets after replacement, requires a linear normalized or floating-point red channel, and clears stale coverage on missing, ambiguous or unsupported outputs. Alpha-only, integer and depth formats are rejected. It never guesses which tile belongs to a terrain.

This first integration consumes **saved mask outputs**. Native MicroVerse generation-complete/cancellation hooks require verification against the installed MicroVerse version and are not implemented. The editor poll is eventually consistent; it cannot identify a native generation transaction. See the [sample guide](Integrations~/MicroVerse/README.md) for refresh, build, and test details.

## Terrain and blade blending

The ground capture samples unlit albedo using the selected TerrainLayer's world tiling, tile offset, and URP diffuse remap scale. Blades blend their lower color and diffuse response toward that ground color. Color capture uses premultiplied coverage, then decodes it before shading to avoid dark fringes along filtered area edges.

For an assigned native terrain, the selected layer shares the first diffuse texture's sampler in its four-layer group, matching URP Terrain/Lit. Wrapping, filtering, mip bias and anisotropy come from that sampler source. Without a matching terrain layer, the selected texture supplies its own sampler. Sampler edits invalidate ground color alone. The layer lookup is cached; call `MarkGroundColorDirty()` after runtime layer reordering that does not emit a terrain texture notification. An uncreated or released **Ground Color Texture** falls back to the selected layer or tint until its producer restores and populates it.

With **Density Shortens Blades** on the renderer, an authored area keeps every blade where its density is above zero and shortens it in proportion instead of thinning the population, so a painted falloff tapers an edge to stubble; the canopy's area follows the same density either way.

**Ground Blend Height** is a fraction of blade height; the root transition remains consistent between geometry LODs. The blade material's **Ground Blend Along Whole Blade** (`_GroundBlendFloor`) defaults to zero, preserving the root-to-tip fade. A value of 0.9 retains at least 90% of the enabled ground blend through the body and tips. The captured map strength and renderer blend strength still control its amount. This same weight blends albedo, the dielectric diffuse-energy response and direct diffuse/specular response; absent ground data or a disabled blend contributes nothing. The optional material setting **Match Ground Normal at Roots** reconstructs a normal from the cached world-height map and lights the whole blade as that ground: the terrain's normal at the root, turned by the blade's own bend from root to that height, with URP Terrain/Lit's own BRDF (diffuse, specular and environment reflection) from the root's splat smoothness and metallic when **Terrain Splat Colour And Detail At Roots** binds a terrain. Slopes then shade, and the grass reflects, as the ground does. It costs extra height samples and is off by default.

Three further material settings, all off by default, tie blades to a painted terrain. **Ground Colour Along Whole Blade** keeps the ground colour from root to tip, so light rather than a tint shades the blade. **Terrain Splat Colour And Detail At Roots** takes that colour, the normal-map detail, the smoothness and the metallic from the bound URP Terrain/Lit splat layers at each root, at the mip the terrain uses there (layer mask maps are not read). **Canopy Occlusion** darkens each point of a blade by the blades above it (Beer-Lambert over the leaf area index from spacing, width and height): the sky it still sees and the light that crosses the canopy, a layer on the ground's slope. The canopy follows the population's density, the authored density and the distance fade the compute thins the blades with, so sparse edges stay lit. Seen from above the canopy shows its shaded ground and blade bases, and along the ground only its lit tops. The grass divides its light by the bare ground's light as the view sees it under the canopy on average, counting the drawn silhouettes the pixel floor widens, so its mean matches the bare terrain beside it at every distance while the depth stays as contrast within the grass. A terrain that applies the same canopy to the ground between the blades, as the Alice sample's footprint terrain does, keeps the patch edge seamless. Only the first terrain surface's density map is bound; blades on a second surface in the same view keep the full canopy.

A single TerrainLayer does not reproduce a mixture of several terrain layers. The editor **Terrain Grass Albedo Baker** captures the native URP Terrain/Lit albedo using the installed URP blend functions. It handles terrain-relative tiling and offsets, diffuse/mask remaps, opacity-as-density, height blending, and additional groups beyond four layers. It samples non-readable source textures on the GPU and saves a linear RGBAHalf texture with mipmaps. Rebaking preserves the output asset's identity and invalidates ground color on its consumers.

Open **Tools > Cone > Grass > Bake Terrain Ground Albedo**, use a Placement Area's context menu, or use the MicroVerse bridge's integrated controls. The [baker guide](Editor/GroundColor/README.md) covers setup. A full-terrain color map uses its own terrain-aligned UVs, independent of a small placement patch. Surviving blades receive the configured ground-match strength even at a feathered low-density spline edge.

The baker requires the native `Universal Render Pipeline/Terrain/Lit` material. Custom terrain shaders and MicroSplat need their own final blended **albedo** output without direct lighting or shadows. Assign it as **Ground Color Texture** and choose terrain-bounds mapping for a full-tile map. This overrides the selected layer. Unsupported native-bake inputs fail with an actionable message.

The MicroVerse Texture Stamp remains visible as distant blades thin out. Keep its boundary and the positive density mask aligned; changing only one stamp's filters or falloff creates a visible mismatch.

Ground color is captured in a shared XZ projection. Vertically overlapping surfaces cannot retain different ground colors at the same XZ coordinates. Use this workflow for terrain tiles and surfaces whose ground-color footprints do not overlap vertically.

## Projected blade stability

Minimum Blade Width is measured in projected pixels, independent of world scale. With MSAA, widening keeps proportional A2C coverage. With AA disabled, a nonzero blade retains the widened opaque silhouette; it is intentionally denser than a subpixel blade rather than clipping the width compensation into holes. Compute population decisions keep their stable world seed. An opaque transition blade fades by scaling its already expanded width, height and curvature by the square root of coverage; this scales the blade silhouette instead of puncturing it into fragment cells, while perspective and row-facing geometry can change the resulting projected area. The final fade can become subpixel and zero density stays absent. Zero physical width remains invisible. The opaque color, contact and motion paths share that clipping policy, while A2C contact depth retains analytic fractional edge coverage.

Physically unresolved opaque blades use a low-frequency cap instead of an ever-narrowing apex. Its span fades continuously to the pointed form as the physical root width moves from the requested pixel minimum to twice that minimum. The extra tip vertex/triangle is shared across LODs; A2C and resolved pointed tips collapse it to the same old silhouette. Previous-frame motion carries the exact previous cap and reconstructs actual trapezoid triangles in material coordinates that remap current and previous cap widths. This adds one vertex and triangle per LOD; its cost is derived and must be measured.

The cap decision uses the physical root width before density scaling. Seeded normal detail and specular strength fade over one to two pixels of the physical tapered width after density scaling and before expansion. Resolved blades retain their detail; widening a tiny blade does not make its lighting detail resolved. This policy does not introduce AA or temporal history and does not guarantee that the remaining binary raster edges and tips cannot crawl. Validate slow camera pans at the actual target resolution with AA disabled, identical density/lighting and a constant-color coverage control. Native GPU cost and temporal measurements remain necessary.

## Collider-driven interaction

Add **Cone > Grass > Collider Interactor** to a character and assign its actual **Actor Collider**. The optional component feeds the existing grass slope capture, so it works with the shared blade deformation used by color, contact depth and optional motion vectors. Keep TAA disabled and motion history Off for the required quality baseline.

1. Assign an upright **CapsuleCollider**, **CharacterController**, or **SphereCollider**. The collider must be active and enabled. An unsupported shape or transform stops new contact rather than using a guessed footprint.
2. Set **Actor Root** to the character hierarchy that support queries must ignore. The component's own transform is the default only when it contains the actor collider. Colliders attached to the same Rigidbody are also excluded.
3. For authored grass, use a Placement Area with its actual supporting Terrain or Paint Surface collider. For a legacy or generic surface, assign **Explicit Support**. A nearer unrelated floor, missing support, or ambiguous overlapping grass support rejects contact.
4. Use **Late Update** for characters moved during Update, or **Fixed Update** for a physics-driven actor. For an external clock, select **Manual** and call `Sample(double)` once per observation. Sampling and recovery run independently of camera movement. Call `ClearHistory()` on gameplay session changes, respawns or a floating-origin shift; call `Configure(actor, support, selfRoot)` when reusing the component for another actor.

| Setting | Initial value | Behavior |
|---|---:|---|
| Bend Strength | 0.85 | Maximum contact influence |
| Attack Duration | 0 s | Response time constant; zero applies contact immediately |
| Recovery Duration | 2 s | Smooth recovery from the last time the footprint covered each sample |
| Grid Spacing | 0.15 m | Fixed world-space contact sample spacing |
| Node Capacity | 1,024 | Retained samples; configurable up to 2,048 |
| Ground Clearance | 0.15 m | Maximum supported distance below the lower collider cap |
| Allowed Penetration | 0.1 m | Ground-query tolerance for shallow contact penetration |
| Teleport Distance | 5 m | Maximum collider-center displacement joined between observations |

Continuous movement sweeps the observed collider footprint across the world grid. Each sample retains one latest-contact time and direction, so a stationary actor does not accumulate overlapping stamp opacity. The same observed linear path and radius changes produce equivalent recovery across update cadences, within floating-point precision and the work limits. Turns or jumps that occur entirely between observations cannot be reconstructed.

The Alice integration can continue using `GrassInteractor` and its public `body`, `interactionShader`, `groundLayers`, `bladeHeight`, `radiusPadding`, `strength`, `attackSeconds` and `recoverySeconds` settings. It uses the same field and lifecycle. Radius Padding expands the influence without inflating the physical grounding cast. Blade Height sets the contact-height range, and Attack Seconds is the exponential response time constant. Recovery Seconds is the complete finite recovery duration. Each sample stores its response at departure, so changing the actor's current contact does not rescale an older trail. `ContactStrength` reports the current actor response; the existing `RetainedStampCount` property reports retained field samples, including the live footprint.

The component uses at most 64 support queries along a joined path, while a field sweep considers at most 4,096 candidate grid nodes. Teleports, reversed time, or observation gaps greater than 0.25 seconds clear the old path, including while airborne. Every valid actor pose participates in this check independently of grounded contact. A nearby jump can last longer than 0.25 seconds and retain its recovering takeoff footprint when observations remain continuous. Exceeding a history or sweep budget clears the trail and attempts the entire current footprint; if that footprint also exceeds the budget, output stays clear and the component reports a diagnostic. Increase Grid Spacing, shorten Recovery Duration, or adjust Node Capacity when the chosen actor and movement require more history.

One reusable mesh contains each occupied grid cell once, with a transparent border. At the maximum node capacity it has at most 32,768 vertices and 49,152 indices. The component owns its mesh and a clone of the retained `InfiniteGrassInteraction` material, releases both on disable, and creates no scene Renderer. Relevant interaction changes invalidate each camera's slope capture, including final expiry, even when **Update Modifiers Every Frame** is disabled. They do not require a full grass-data refresh.

The interaction producer interpolates premultiplied vertex RGBA, including transparent zero at expired nodes. This keeps neighboring bend directions weighted by their current influence and continuous through expiry. An explicitly assigned Interaction Shader must implement that premultiplied vertex-color contract; the included `GrassInteractor.shader` does. Existing generic slope modifiers retain their own material contract. After a custom modifier changes, `RefreshGrassInteraction()` can request slope capture alone without refreshing height or ground color.

Interaction remains a shared world-XZ projection. Vertically overlapping grass supports cannot carry independent bend fields at the same XZ coordinates. The support query rejects ambiguous ownership; ordinary adjacent tiles remain supported. The field grid also cannot recover detail lost in the capture texture: with the default 300 m draw distance, 10 m capture padding and 1,024-pixel capture, each texel spans about 0.61 m. Choose capture resolution and distance appropriate to the actor footprint and verify the result in the target scene.

Automatic ambiguity checks cover authored Placement Area owners and the declared explicit support. Unbound legacy renderers do not supply enough ownership metadata to detect every stacked surface. Support identity, transforms, scenes, geometry metadata and rendered material-slot counts invalidate retained history, including changes that preserve the same bounds. Authored area observations also invalidate affected fields after the actor has left that support. Call `ClearHistory()` after in-place mesh cooking that preserves the observed metadata. A moving support clears the old world positions while a fresh, bounded observation on that same support can continue the live attack response. It records only the new footprint after invalidation. Support loss, explicit resets, teleports and excessive observation gaps restart that response.

The most recently recorded support is revalidated before its interaction draw. Disabling, destroying or moving that support clears stale output even before the next actor sample, including in Manual mode. This draw-time check neither samples the actor nor advances recovery.

The field, mesh, support and component cases are authored in the Editor test assembly. The three collider/support fixtures enter paused Play sessions to create isolated native physics worlds, then restore the previous editor state. Native execution, cleanup, rendered recovery and cost still require the [interaction acceptance checks](Documentation~/Validation.md#collider-interaction-acceptance).

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

The feature requests scene depth and draws a dedicated grass depth/coverage pass with matching deformation. Contact evaluation reads those depths and multiplies the existing camera color attachment through fixed-function blending. It preserves MSAA color samples and alpha, and does not sample the color attachment it writes. Contact depth rejects fragments outside the receiver distance plus ray reach, and the search uses explicit projection/origin mapping for viewport and depth-texture differences. Disabled contacts schedule no contact passes or intermediates.

A ray sampling its receiver's own grass-depth texel cannot count that same recorded surface as another grass caster. The separately stored scene depth remains available at that texel. This check uses the actual grass allocation's scaled and shifted texel coordinates; distinguishable grass casters in other texels remain eligible.

This is a **custom screen-space approximation**. Unity's [pipeline comparison](https://docs.unity3d.com/6000.6/Documentation/Manual/render-pipelines-feature-comparison.html) lists built-in contact shadows for HDRP, not URP. Off-screen and hidden casters are unavailable. Only the nearest grass depth is stored, so fractional/MSAA silhouettes are approximate. The final color multiplication also attenuates ambient/emissive contributions. It supplements normal main-light shadow reception; it does not add grass to the directional light's shadow atlas.

## Reducing aliasing

Evaluate grass quality with **TAA disabled**. New renderer settings use **Motion Vectors > Off**; the baseline uses spatial coverage and stable geometry without frame history:

- Stable world-space seeds for roots, density decisions, and LOD selection.
- Fractional population coverage near density transitions.
- Real geometry LOD with a stable transition band.
- A projected **Minimum Pixel Width**, with proportional coverage compensation so widening thin blades does not simply make the field denser.
- Native triangle-edge coverage under MSAA; solid geometric density fading and an unresolved-tip cap with AA disabled.
- **Alpha to Coverage** enabled only when the actual camera target uses MSAA; no blade-cell fragment discard in the opaque path.
- A distance range that fades narrow specular highlights.
- Shared deformation and per-blade density/width coverage for color and contact depth.

Width compensation uses the original-to-expanded width ratio at every positive world scale. An unexpanded blade retains its full density coverage, and a zero-width blade stays invisible even when the pixel minimum expands its geometry. Scaling blade dimensions and an orthographic viewport together preserves this ratio. The pixel minimum uses the actual finite vertical projection scale, including large orthographic views. Invalid projection data or an unrepresentable expansion disables that optional expansion.

Perspective blades face the viewing ray at each blade row, including roots visible behind the camera in world XZ during steep downward views. Orthographic blades keep parallel facing directions. The shared deformation also keeps contact depth and optional previous-frame motion geometry consistent with this orientation.

Blade color, contact depth and optional motion passes render both sides. Wind can reverse a triangle's projected winding at steep camera angles, especially for the pointed far LOD; both sides retain the same authored blade normals so the reversal does not introduce a lighting switch. Missing or released wind textures use the existing gray fallback until their producer supplies a ready texture.

Wind RenderTextures must supply a single-sample texture binding. An unresolved multisampled source (`antiAliasing > 1` with `bindTextureMS` enabled) uses that same fallback; automatically resolved MSAA sources remain eligible. The renderer leaves source storage and material assignments under producer ownership.

For multisampled color, A2C receives the blade's density and width compensation. The rasterizer covers its tapered geometric silhouette. Applying an analytic edge fade to that output as well would thin the same edge twice, because the [A2C mask is intersected with primitive sample coverage](https://learn.microsoft.com/en-us/windows/win32/direct3d11/d3d10-graphics-programming-guide-blend-state#alpha-to-coverage). The separate single-sample contact depth keeps its analytic silhouette estimate.

Start with a one-pixel minimum width and restrained distant width expansion. Compare the same camera pans, wind motion, grazing angles, painted edges and LOD transitions with MSAA off, then with actual 2x/4x/8x attachments where supported. Keep camera antialiasing at **None**, motion history **Off**, and contacts both off/on during these checks. A requested URP sample count is insufficient evidence: inspect the color/depth attachments used by the grass draw. The generated standalone probe records those attachments and marks hardware fallbacks explicitly. Unity documents that [AlphaToMask requires MSAA](https://docs.unity3d.com/6000.6/Documentation/Manual/writing-shader-alpha-to-mask.html); enabling it on a single-sample target has platform-dependent results.

Keep **Overflow Grass Count** at zero during comparisons. Over-capacity LOD queues drop roots by GPU reservation order, so the surviving subset can change across frames. Increase the affected queue's capacity or reduce its density before evaluating silhouette stability. The single-sample fallback has a fixed blade-space pattern; very small silhouettes can still alias as their pixel coverage changes. Raising pixel coverage or using supported MSAA addresses that sampling limit without temporal accumulation.

### Optional motion consumers

**Motion Vectors** has **Off** (the default), **Auto**, and **Always** modes. Existing explicitly serialized mode selections are retained. Auto enables the indirect motion path for supported URP temporal AA or camera-and-object motion blur. Always requests motion data for custom consumers. Off releases that camera's history. The pass augments URP's existing motion/depth targets and uses exact world-XZ root matching, so append order and LOD queue changes do not assign another blade's history. It reconstructs the actual previous mesh triangles and snapshots wind/interaction maps.

Repeated renders in one `Time.frameCount` retain the same previous snapshot, matching URP's camera-matrix history. Missing or ambiguous roots use camera-only motion. Initial/reset frames and rendering gaps reset grass motion history; incompatible material, spacing, viewport and target-size changes also reset it. Hash lookup is bounded to 128 slots and does not perform CPU position readback.

Motion history is allocated only while requested. For capacity `C`, two root snapshots and one hash table use `32 * C + 4 * NextPowerOfTwo(2 * C)` bytes, plus small count/dispatch buffers. That is **77.04 MiB at 2,000,000 blades**, or **616.28 MiB at 16,000,000**, per active camera. Lower **Max Blade Count** reduces this allocation; the renderer checks individual device buffer limits.

Texture snapshots preserve the sampled deformation values. Matching linear RGBA8 UNorm sources retain that format when supported. Other wind inputs store their two sampled channels in RG32F, with RGBA32F as a fallback; other interaction inputs require RGBA32F. Compressed and sRGB sources are copied in sampled space. Half-float snapshots are avoided because their rounding can make unchanged wind produce nonzero motion. A change in the selected storage format reallocates the affected snapshots and resets history; unavailable render/sample/linear-filter support releases that camera's optional history.

Two interaction and two wind snapshots are additional to the root buffers. At 1024², one pair occupies **8 MiB** in RGBA8, **16 MiB** in RG32F, or **32 MiB** in RGBA32F. For example, 1024² RGBA8 interaction plus 256² RG32F wind requires **9 MiB** of texture storage per camera, compared with 17 MiB for the former half-float snapshots. These figures describe texture storage, excluding driver overhead and other rendering resources.

Keep motion history Off for the baseline. Enable it only for a consumer that needs motion data and validate that integration separately. Its temporal visual behavior remains unverified. FXAA/SMAA can be compared as optional spatial post-processing after the underlying geometry and coverage have passed their checks.

## Rendering and performance changes

Position buffers, per-LOD indirect arguments, counters, and capture targets belong to individual camera resources and persist between frames. Buffer capacity changes rebuild resources deliberately; optional count previews use asynchronous readback.

A position-buffer request above the device's reported individual-buffer limit releases that camera's existing core and motion resources. Repeated rejection does not recreate its cache or repeat the warning. Returning to a supported capacity allows allocation again.

At the default five subdivisions, the new meshes use:

| LOD | Vertices per blade | Triangles per blade |
|---|---:|---:|
| Near | 13 | 11 |
| Middle | 7 | 5 |
| Far | 3 | 1 |

The old near mesh duplicated row vertices and used 23 vertices. Its distance deformation retained all 11 triangles. The new near mesh shares rows; middle/far draws use separate meshes and independent GPU queues.

Generation checks dispatch boundaries, surface/area coverage, distance density, and a conservative frustum radius before writing bounded queues. Each 8x8 workgroup combines accepted roots before reserving up to three global LOD ranges, reducing counter contention. Rounded edge threads still participate in every group barrier. Distance density remains full only to **Full Density Distance**, then fades toward **Draw Distance**. Overflow is safely dropped and available in diagnostics. Tune queue weights/capacity if diagnostics show overflow.

Density, ground color and surface revisions are tracked separately. A paint stroke or replaced MicroVerse mask can refresh the affected density capture while height remains cached. Different supporting surfaces keep separate positive-density maps to prevent coverage leaking between their dispatches. Empty painted blocks and tiles outside rotated box/circle areas skip dispatch work. Tile checks reuse captured placement mappings, and the candidate budget counts actual clipped cells so long, narrow regions do not exceed it merely because they touch many edge tiles. Cropped texture bounds retain the original mask UVs and reject empty corners of rotated crops. Scene inventory is shared across cameras instead of rediscovered on each camera movement or stroke.

When no dispatch remains, the renderer resets counts and indirect arguments, releases motion history, and skips grass color, contact and motion passes. Direct camera output uses the actual attachment metadata for MSAA and format decisions, including URP backbuffers without a texture descriptor. Motion history also releases on unsupported material changes, recreates lost GPU texture storage, and preserves wind sampling when texture-quality settings reduce the active mip level.

Removing a required material or compute shader, or assigning a material without a `GrassForward` pass, releases camera resources immediately; a camera becoming unsupported releases its own cache. Capture passes declare all exposed material texture properties and their renderer property-block overrides, including per-material overrides. Custom inputs supplied only through global shader textures need an explicit capture dependency. Modifier lookup compares native shader-tag IDs instead of converting tag names to managed strings during fallback searches. Optional motion snapshots recover invalid GPU buffers and reuse per-pass layout arrays while retaining independent metadata for pending draws. These source paths have regression coverage; steady-state allocation measurements still require Unity execution.

Deactivating the renderer feature releases its camera resources at the next render-context boundary, even though URP no longer calls its pass-enqueue method. An active feature in an unused renderer asset continues to evict idle camera caches. Re-enabling the feature creates camera resources as needed; disabling or destroying the feature object tears down its owned helpers and callback.

Unused terrain density maps and camera resources are evicted after bounded idle periods. Position buffers shrink after a major capacity reduction. Moving modifiers can refresh separately. **Texture Update Threshold** controls capture recentering, and **Capture Resolution** trades small boundary detail against capture memory/work. Increase **Culling Padding** to cover unusually tall, wide, or strongly bent grass.

Dispatch plans release their temporary terrain-group references after resolving execution inputs, including rejected and interrupted plans. Reusing a larger plan pool therefore does not retain an evicted group's terrain and occupancy collections.

The configured height range is normalized before both matrix construction and cache comparison. Reversed endpoints and nonfinite values that resolve to the same finite defaults share one cache identity.

These are structural reductions, **not measured frame-rate claims**. The richer fragment lighting, extra ground samples, and enabled contact shadows also cost GPU time. Rendering both blade sides keeps the same geometry and draw count but can shade bent portions that backface culling previously discarded. Profile the target scene at equal visual coverage. GPU Resident Drawer does not automatically optimize these custom indirect draws.

## Migration notes

- Package baseline changes from the inconsistent Unity 2021.3/URP 17.1 declaration to Unity 6.6/URP 17.6.
- Required Terrain, Terrain Physics, Physics and IMGUI modules are explicit package dependencies.
- Object identity uses Unity 6.6 `EntityId`; full identities are retained as cache/group keys instead of truncating them to integer hashes.
- This is a `2.0.0-preview.2` API preview.
- New components use **Authored Areas**. Set **Legacy Surface Layer** explicitly for previous layer-based scenes.
- `_GrassPositions.w` stores **coverage**, not camera distance. Custom blade shaders must compute distance from the world pivot.
- `ArgsBuffer` and the synchronous debug `Buffer` fields are removed. The renderer feature owns per-camera resources. Use `VisibleGrassCount` and `OverflowGrassCount` for asynchronous diagnostics.
- `GetGrassMeshCache()` remains a near-mesh compatibility accessor; `GetLodMeshes()` returns the three shared meshes.
- Custom capture shaders should use `_GrassCaptureVP` and put their capture `LightMode` tag and name on the actual pass. Expose sampled textures as material texture properties so the renderer can declare their RenderGraph dependencies, including renderer-wide and per-material property-block overrides. Textures supplied solely through custom globals require explicit dependency integration.
- Custom blade shaders need the `GrassForward` pass and `_GrassInstanceOffset`; contact support additionally needs the `GrassContactDepth` contract.
- Indirect motion support needs the `GrassMotionVectors` pass and the package history/deformation contracts. Updating a custom color pass alone does not provide temporal motion.
- Main-light and scene ambient-probe SH lighting are the default; the draw explicitly supplies its SH coefficients and does not sample local light probes per grass root. Optional additional-light shading targets Forward+ clustered lighting; ordinary Forward per-object light indices are unavailable to this indirect draw.
- The intended initial camera target is single-view Game/Scene base cameras. Overlay, reflection, preview, and XR cameras are excluded. Deferred, camera stacking variants, TAA, and additional graphics APIs require explicit validation.

## Executable validation

Run the license-independent checks from the repository root:

```sh
python3 Tools~/validate_source.py .
python3 -B -m unittest discover -s Tools~/tests -v
python3 -B -m unittest discover -s Tools~/ShaderChecks -p 'test_*.py' -v
python3 -B Tools~/ShaderChecks/check.py
dotnet run --project Tools~/SourceChecks -- .
```

The last command needs .NET 8 and restores a pinned Roslyn package. It parses six C# 9 configurations: Editor, Release player and Checked player branches for 6.6 and 6.7, including the optional sample. Editor and Checked configurations include Unity's diagnostic defines; Release excludes them. It does not bind Unity APIs or compile shaders.

The separate shader check runs the official Microsoft DXC compiler against pinned, unmodified Unity Graphics headers. It compiles 53 entry-point/keyword configurations to each of DXIL and SPIR-V and writes binaries, diagnostics and source provenance to `artifacts/shaders`. All 106 compiler invocations passed locally. This catches HLSL compilation errors; Unity import, variant stripping, runtime bindings, target shader-model support and rendered correctness still need Editor/player validation. See the [shader compiler guide](Tools~/ShaderChecks/README.md).

Create a disposable project with the package tests, optional bridge sample and native URP setup:

```sh
python3 Tools~/create_validation_project.py --unity 6000.6.0f1 --urp 17.6.0
```

Open the generated project with that installed Unity version and run EditMode tests. A graphics device is required for compute, shader-pass and albedo tests. `GrassValidationBuild.BuildCurrent` creates a reproducible real-grass scene with a saved synthetic mask and makes both clean and incremental standalone builds with Managed Code Variant set to **Checked**, restoring the previous setting afterward. Unity 6.6 requires this explicit variant for RenderGraph checks and instrumentation; the Development Build flag alone does not enable them. The build also enables the pipeline's separate validity setting. Each build preserves an executable snapshot and records its size and hash for the build-results reporter.

The built player has an opt-in `-grassSmoke` mode that records functional observations and screenshots. Its required baseline keeps TAA disabled and verifies visible grass, wind movement, contact darkening and clearing of empty sources. The player reporter recomputes these comparisons from the captures and checks the actual render attachments. Build success and rendered acceptance are reported separately; see the complete [validation instructions](Documentation~/Validation.md).

The GitHub workflow runs source and shader checks independently of licensing, then Editor tests and the player builds when Unity licensing credentials are configured. It saves compiler output, logs, results and the standalone player, and explicitly reports skipped GPU cases. See [validation](Documentation~/Validation.md) for CLI commands, licensing configuration, the 6.7 target and the remaining rendered acceptance cases.

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
| [#46](https://github.com/coen22/com.cone.grass/issues/46) | Collider interaction, bounded recovery and lifecycle |
| [#50](https://github.com/coen22/com.cone.grass/issues/50) | Native Editor test import, discovery and execution |

Issues stay open until their Unity acceptance checks pass. See [validation](Documentation~/Validation.md) and the [MicroVerse guide](Integrations~/MicroVerse/README.md).

## Original demo

The [original preview video](https://youtu.be/NwVtPIxUuCY) shows the earlier renderer and interaction effects. It is not a benchmark or visual validation of this preview branch.

Original package author: Youssef Afella. See [LICENSE](LICENSE).
