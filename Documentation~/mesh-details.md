# Mesh details

![Mesh details disabled (Unity's terrain detail renderer) and enabled (Cone Grass mesh kinds) at the same camera](images/mesh-details-compare.png)

**Grass Mesh Details** draws a terrain's detail meshes (ferns, heather, bushes, shrubs) on the GPU, from the terrain's own detail density layers. It replaces Unity's terrain detail renderer for mesh prototypes:

- **GPU scatter.** A compute shader places every instance each frame around the camera, from the painted detail layers, with Unity's density rules (instance-count and coverage scatter modes, prototype density, the terrain's Detail Density).
- **Culling.** Each instance is tested against the camera frustum and against last frame's depth (a Hi-Z pyramid), so plants hidden behind terrain, rocks and trees are not drawn. Shadow casters are culled per sun cascade, up to a shadow distance of their own.
- **Distance LOD.** Up to four mesh LODs per kind. Across a band before each LOD's end the two LODs cross-fade with URP's fixed screen dither: the two dithers cover each pixel exactly once, nothing moves from frame to frame, and no TAA is needed. After the last LOD the plants dither out.
- **Variation.** Per-instance width and height from a noise field (Unity's Noise Spread and Seed), random rotation, alignment to the terrain normal, jitter, and Healthy/Dry colour.
- **Wind.** Plants lean downwind in proportion to their height, with gusts rolling across them and a per-plant flutter. The wind is com.cone.render-features' shared **ConeWind** (the scene's WindZone) when it is installed, otherwise the component's own wind settings.
- **Water line.** Nothing grows at or below the water, and plants thin in over the shore band: the same water line as the grass blades.
- **Any URP material.** The instances are drawn by a BatchRendererGroup through URP's own passes (depth prepass, depth-normals, shadow cascades, forward and deferred lighting), so URP Lit, URP Shader Graph and Cone/Foliage (com.cone.render-features, with transmission) materials render unchanged.

## Set up

1. Paint detail meshes on your terrains as usual (Terrain > Paint Details), or use terrains that already have them.
2. Run **Tools > Cone > Grass > Convert Terrain Details to Mesh Kinds** with the terrains selected (or none selected, for every active terrain).
   - It creates a **Grass Mesh Details** object with one **mesh kind** per distinct detail prototype. Terrains that share prototypes share kinds.
   - It turns Unity's own detail drawing off for those terrains (Detail Distance 0) and records the old distance. The terrain data is not changed.
   - It adds the **Grass Mesh Detail Renderer Feature** to the active URP renderer.
3. Enter Play Mode or look in the Scene view: the plants are drawn by the mesh kinds.

To go back, use **Restore Unity Details** (`GrassMeshDetailConverter.RestoreUnityDetails`): it puts each terrain's Detail Distance back.

Terrains that also paint **grass texture** (billboard) layers keep Unity's detail drawing on, with a warning, because the converter does not convert textures; their mesh layers would then draw twice. Convert those textures to grass blades (the Infinite Grass Renderer) or remove them.

### Materials

The material's shader must have the DOTS instancing variant (`DOTS_INSTANCING_ON`), which BatchRendererGroup draws use. URP Lit, every URP Shader Graph and Cone/Foliage have it. A material without it is skipped with a warning (for example a legacy or HDRP shader).

For a player build, set **Project Settings > Graphics > BatchRendererGroup Variants** to **Keep All**, so the DOTS instancing variants are not stripped.

Healthy/Dry colour multiplies the material's `_BaseColor` per instance where the shader reads `_BaseColor` per instance (URP Lit does). The converter turns it on for prototypes that are not GPU-instanced, as Unity's own detail shaders tint only those.

## Reference

### Grass Mesh Details

| Setting | Default | What it does |
|---|---|---|
| Kinds | | The mesh kinds, indexed by the terrains' layer maps. |
| Terrains | | Each terrain, and for each of its detail layers the kind it scatters (-1 for none). |
| Density Scale | 1 | Multiplies every kind's density. |
| Distance Scale | 1 | Multiplies every LOD, cross-fade and shadow distance. |
| Occlusion Culling | on | Skip instances hidden behind last frame's depth. Needs the camera depth texture, which the feature asks for. A camera cut (a jump of more than 5 % of the reach, or a turn of more than 12°) draws one frame without it. |
| Cast Shadows | on | Kinds that cast shadows draw into the sun's cascades. |
| Draw In Reflections | on | Reflection cameras (Crest 5 planar reflections, realtime reflection probes) draw the kinds at **Reflection Distance Scale** (0.5). |
| Wind | Auto | See [Wind](#wind). |
| Use Grass Water Line | on | Use the scene's Infinite Grass Renderer water line when it applies; otherwise the component's own **Water Line**. |
| Initial List Capacity | 8192 | Visible instances each draw list starts with. Lists that overflow grow on their own a few frames later. |
| Max Instances | 4 (million) | The most instance slots per camera. |

### Mesh kind

| Setting | Default | From Unity's detail prototype |
|---|---|---|
| Lods | | The prefab's mesh, or the meshes of its LOD Group. Each LOD has materials (one per submesh) and an **End Distance**. A LOD Group's screen heights become distances: LOD *i* ends at `detail distance × h_last / h_i`. |
| Cross Fade Width | 6 m | The band before each LOD's end over which it cross-fades. |
| Density | 1 | `density` (instances per square metre at full coverage, coverage mode). |
| Use Density Scaling | on | `useDensityScaling`: follow the terrain's Detail Density. |
| Min/Max Width, Min/Max Height | 1 | `minWidth`, `maxWidth`, `minHeight`, `maxHeight`. |
| Noise Spread, Noise Seed | 0.1, 0 | `noiseSpread`, `noiseSeed`: the noise that picks size and colour. |
| Align To Ground | 0 | `alignToGround` / 100. |
| Position Jitter | 1 | `positionJitter` / 100. |
| Color Variation, Healthy Color, Dry Color | None | `healthyColor`, `dryColor` (Healthy/Dry for non-instanced prototypes). |
| Shadow Casting, Shadow Distance | On, 40 m | The prefab renderer's shadow mode; casters up to this distance. |
| Wind Bend | 0.35 | How far the top leans per unit of wind strength, as a share of the height. 0 for materials with their own vertex wind. |
| Wind Flutter | 0.5 | How much the wind's turbulence makes each plant flutter. |

### Density rules

Per detail cell, the expected number of instances is:

- **Instance Count** scatter mode: the layer value (a count), times the terrain's Detail Density when the kind follows it.
- **Coverage** scatter mode: `value / 255 × Density × cell area`, times the terrain's Detail Density when the kind follows it.

The fractional part is placed with a per-cell random draw, so the mean matches. The tests check the instances per square metre against Unity's own placement (`TerrainData.ComputeDetailInstanceTransforms`) within 5 %.

### Wind

| Source | Wind |
|---|---|
| Auto (default) | ConeWind when com.cone.render-features with ConeWind is installed, otherwise Own. |
| Scene | ConeWind only: still air without it or without a directional WindZone. |
| Own | The component's settings: Direction (degrees around Y, towards which it blows), Main, Turbulence, Pulse Magnitude and Pulse Frequency, as a WindZone's. |
| None | No wind. |

ConeWind is read through the global shader vectors it publishes (`_ConeWindDirection`, `_ConeWindParams`, `_ConeWindOffset`), so the gusts travel with the same air as Cone Fog and the clouds. Grass references no render-features type; the `CONE_GRASS_RENDER_FEATURES` version define tells it the package is installed.

The lean is a shear of the instance's transform, so it works with any material. A material that has its own vertex wind (a Shader Graph with wind nodes) can set the kind's **Wind Bend** to 0.

## How it draws

The **Grass Mesh Detail Renderer Feature** adds two passes per camera:

1. **Grass Mesh Details Cull**, before the shadow maps: resets the lists, scatters each terrain's cells within reach, culls and writes the instances and the visible lists, and finalises the indirect arguments.
2. **Grass Mesh Details Hi-Z**, after the opaques: reduces the camera depth to a farthest-depth pyramid for the next frame's occlusion test.

The meshes themselves are drawn by URP: the BatchRendererGroup hands URP indirect draw commands for the camera (solid and cross-fade lists per LOD) and for each sun cascade. com.cone.render-features has a depth pyramid of its own, but it is internal to that package; grass builds its own (0.04 ms at 1080p).

Cameras: Game and Scene view cameras, and reflection cameras when **Draw In Reflections** is on. Overlay, preview and XR cameras do not draw mesh kinds.

## Performance

See [Performance](performance.md#mesh-details).
