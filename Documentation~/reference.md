# Reference

Every inspector setting, with its default and what it does. Names are as the inspector shows them. All types are in the `Cone.Grass` namespace.

- [Infinite Grass Renderer](#infinite-grass-renderer)
- [Grass Data Renderer Feature](#grass-data-renderer-feature)
- [Grass Placement Area](#grass-placement-area)
- [Grass Density Asset](#grass-density-asset)
- [Grass Variant Profile](#grass-variant-profile)
- [Grass Collider Interactor](#grass-collider-interactor)
- [Grass Interactor](#grass-interactor)
- [Grass Blade material](#grass-blade-material)
- [Scripting](#scripting)

## Infinite Grass Renderer

`InfiniteGrassRenderer`, one per scene. Add it from **Add Component**, search "Infinite Grass Renderer".

### Rendering

| Setting | Default | What it does |
|---|---|---|
| Grass Material | none | The blade material. Use **Grass Blade** or a copy. Required unless **Draw Blades** is off. |
| Placement Mode | Authored Areas | **Authored Areas**: grass grows only inside Placement Areas. **Legacy Surface Layer**: grass grows on every surface on the feature's Height Map Layer. |
| Render In Scene View | on | Also draws in the Scene view. |
| Draw Blades | on | Off draws no grass but keeps the interaction capture running, so effects that read the [interaction field](interaction.md#shared-interaction-field) still see contacts. Needs no material or compute support. |

### Population

| Setting | Default | What it does |
|---|---|---|
| Spacing | 0.1 m | Distance between candidate roots. Each root is jittered inside its cell. Halving it gives four times the blades. Range 0.01 to 10. |
| Draw Distance | 300 m | No grass beyond this distance from the camera. Also sets the capture window. |
| Full Density Distance | 30 m | Every root grows up to this distance; beyond it the population thins toward Draw Distance. |
| Density Falloff Exponent | 4 | Shape of that thinning. Higher thins sooner. Range 0.1 to 16. |
| Density Transition | 0.05 | Width of the soft band where a thinning blade fades in coverage instead of popping. 0 to 0.25. |
| Density Shortens Blades | off | Authored density shortens blades instead of thinning them. A painted falloff then tapers to stubble. |

### Water Line

Keeps grass and flowers out of water. See [Water line](concepts.md#water-line).

| Setting | Default | What it does |
|---|---|---|
| Enabled | off | Grow no grass at or below **Level**. A level source (such as the [Crest bridge](integrations.md#crest-5-water-level)) turns the water line on too. |
| Level | 0 m | World-space water height (Y). A level source overrides it while it reports a level. |
| Shore Fade | 0.5 m | Height of the shoreline band above the water. Density rises linearly from zero at the water to full at this height, so the grass doesn't stop in a hard line. 0 is a hard line. |

From a script, `InfiniteGrassRenderer.WaterLine.Source` takes any `IGrassWaterLevelSource`; it isn't serialized. A destroyed Unity object counts as no source.

### Plant Kinds

| Setting | Default | What it does |
|---|---|---|
| Variants | none | A [Grass Variant Profile](#grass-variant-profile). Empty grows the material's one blade. |
| Max Head Count | 0.25 (million) | Capacity of each flower-head queue. Heads beyond it are dropped and counted as overflow. 0.001 to 4. |

### Geometry LOD

| Setting | Default | What it does |
|---|---|---|
| Grass Mesh Subdivision | 5 | Segments in the near blade mesh, 0 to 8. The middle mesh uses at most 2; the far mesh none. |
| Near Lod Distance | 30 m | End of the near LOD. |
| Subdivision Distance | 100 m | Start of the far, single-segment LOD. |
| Lod Transition Width | 10 m | Width of the band where two LODs mix at each threshold. |
| Subdivision Height Boost | 0 | Raises blade height around Subdivision Distance, to hide the change to the far mesh. 0 is off; -0.95 to 10. |
| Subdivision Bump Width | 20 m | Width of that height bump. |
| Culling Padding | 2 m | Radius around each root used for frustum culling. Raise it for tall, wide or strongly bent grass that pops at the screen edge. |

### Data Capture

| Setting | Default | What it does |
|---|---|---|
| Capture Resolution | 1024 | Size of the top-down captures, a power of two from 128 to 2048. Higher keeps small edges and interaction detail at more memory and capture cost. |
| Texture Update Threshold | 10 m | Grid step the capture window snaps to, and the padding around Draw Distance. Larger recaptures less often. |
| Capture Height Range | (-100, 1000) | World Y range of the top-down mesh capture. Must enclose every grass surface. Terrains extend it automatically. |
| Cache Surface Data | on | Keeps captured mesh heights until something changes. Turn off only for surfaces that deform every frame. |
| Update Modifiers Every Frame | on | Re-collects modifier renderers every frame, for moving masks, tints and pushers. Static scenes can turn it off and call `RefreshGrassData()` after edits. |

### GPU Capacity

| Setting | Default | What it does |
|---|---|---|
| Max Buffer Count | 2 (million) | Total blades across the three LOD queues. 0.01 to 16. |
| Lod Capacity Weights | (0.35, 0.45, 0.2) | How Max Buffer Count is split between the near, middle and far queues. |

### Ground Blending

| Setting | Default | What it does |
|---|---|---|
| Ground Blend Strength | 0.8 | How strongly blades take the ground colour. Overrides the material's Ground Color Blend. |
| Ground Blend Height | 0.25 | Fraction of the blade height, from the root, that blends toward the ground. Overrides the material's value. |

### Aliasing

| Setting | Default | What it does |
|---|---|---|
| Alpha To Coverage | on | Uses alpha-to-coverage for soft blade edges, only when the camera's actual colour target uses MSAA. |
| Minimum Pixel Width | 1 | Blades are widened to at least this many pixels, with their coverage reduced to match, so thin far grass does not break up. 0 to 3. Overrides the material's value. |
| Specular Fade Range | (30, 100) m | Specular highlights fade out between these distances, so far grass does not sparkle. Overrides the material's values. |
| Motion Vectors > Mode | Off | See [motion vectors](#motion-vectors). |

### Optional Contact Shadows

| Setting | Default | What it does |
|---|---|---|
| Enabled | off | Adds a grass depth pass and a screen-space contact pass that darkens where grass meets visible opaque surfaces, using the main directional light. |
| Strength | 0.35 | Amount of darkening, 0 to 1. |
| Ray Length | 0.75 m | Longest search from a pixel toward the light. |
| Steps | 8 | Depth samples along the ray, 4 to 32. Cost grows with it. |
| Bias | 0.03 m | Offset that avoids self-shadowing. |
| Thickness | 0.15 m | How thick a sampled depth counts as an occluder. |
| Max Distance | 50 m | No contact shadows beyond this distance. |

Contact shadows are an approximation in screen space. Casters off screen or hidden behind other geometry do not shadow. The darkening also applies to ambient light. They add to, and do not replace, normal main-light shadows.

### Motion vectors

| Mode | What it does |
|---|---|
| Off (default) | No grass motion history. Grass writes camera-only motion. |
| Auto | Keeps per-blade history when URP needs motion: temporal anti-aliasing, or camera and object motion blur, on single-sample cameras. |
| Always | Keeps history for every camera, for custom motion consumers. |

Motion history matches each blade to its previous position by world root, so wind, interaction and LOD changes produce correct per-blade motion, including flower heads. It costs memory per camera; see [Performance](performance.md#motion-vectors).

### Diagnostics

| Setting | Default | What it does |
|---|---|---|
| Preview Visible Grass Count | off | Shows "Grass: N drawn; M over capacity" in the Game view. Counts are read asynchronously and lag a few frames. |

## Grass Data Renderer Feature

`GrassDataRendererFeature`, on the Universal Renderer Data asset. See [Getting started](getting-started.md#2-add-the-renderer-feature).

| Setting | Default | What it does |
|---|---|---|
| Height Map Layer | Nothing | Layers whose terrains and meshes are grass surfaces in **Legacy Surface Layer** mode, and the fallback for authored areas that have no Terrain or Paint Surface. |
| Height Map Mat | `GrassHeightMapMat` (filled in automatically) | Material that captures mesh heights. |
| Compute Shader | `GrassPositionsCompute` (filled in automatically) | The generation compute shader. |

The pass runs before transparents, only for Universal Renderers and Base cameras.

## Grass Placement Area

`GrassPlacementArea`. **Add Component > Cone > Grass > Placement Area**. The shape is projected straight down along world Y.

### Coverage

| Setting | Default | What it does |
|---|---|---|
| Shape | Circle | **Circle**, **Box**, or **Texture** (a density map or texture). |
| Density | 1 | Overall density, 0 to 1. 0 turns the area off. |
| Edge Falloff | 0.15 | Fraction of the shape's radius that softens the edge. Use 0 for a mask that has its own falloff. |
| Size | (10, 10) m | Width and length before the object's X/Z scale. Rotation around Y is applied. |

### Surface and mapping

| Setting | Default | What it does |
|---|---|---|
| Terrain | none | The supporting terrain. The area samples its height map and holes. Assigning it does not grow grass outside the area. |
| Use Terrain Bounds | off | Maps the texture over the terrain's whole X/Z extent and ignores the object's transform. For whole-terrain density maps. |
| Paint Surface | none | A mesh collider (or any collider) as the supporting surface and brush target. Its Renderer, on the same object or a parent, is captured for height and needs at least one material slot. |

### Texture coverage

| Setting | Default | What it does |
|---|---|---|
| Density Asset | none | A paintable [Grass Density Asset](#grass-density-asset). Takes precedence over Density Texture. |
| Density Texture | none | A linear texture with density in red: black is no grass, white is full. Needs no CPU read access. Turn off sRGB. |
| Texture Coverage Bounds | (0, 0, 1, 1) | Crops the drawn region to these UVs of the texture without stretching it. |

Density textures need a normalised or floating-point red channel (R8, RGBA8 and so on). Alpha-only, integer and depth formats give no grass. A RenderTexture must be created, and must not use **Bind Texture MS**.

### Ground color matching

| Setting | Default | What it does |
|---|---|---|
| Ground Layer | none | The TerrainLayer painted under the area. Its diffuse texture, tiling, offset and remap are used. |
| Ground Color Texture | none | A colour map that overrides the layer, for example a [baked terrain albedo](integrations.md#terrain-ground-albedo-baker). |
| Ground Color Uses Terrain Bounds | off | Maps Ground Color Texture over the whole terrain rather than the area. |
| Ground Tint | white | Multiplies the ground colour. With no layer or texture, the tint is the ground colour. |
| Ground Color Strength | 0 | How much ground colour this area supplies. **Raise it to see any ground matching.** |

### Plant kinds

| Setting | Default | What it does |
|---|---|---|
| Override Variant Weights | off | Reweights the renderer's plant kinds inside this area. Off grows the profile's own mix. |
| Variant Weights | eight 1s | Per kind, in the profile's order: a multiplier of its weight from 0 (none here) to 1. The inspector lists the kind names. |

See [per-area weights](plant-kinds.md#per-area-weights) for how overlapping areas combine.

### Scene Brush

| Control | Default | What it does |
|---|---|---|
| Create paintable density map | | Saves a new, empty Grass Density Asset and assigns it. The area switches to the Texture shape. |
| Paint density in Scene view | off | Turns the brush on. Drag to paint; Shift erases; Alt orbits. |
| Add / Erase | Add | Brush mode. |
| Radius (metres) | 3 | Brush radius. |
| Strength | 0.25 | Amount added per dab. |
| Hardness | 0.5 | 0 is a soft falloff, 1 a hard edge. |
| Clear coverage | | Empties the map. |

The brush follows the Terrain (through its Terrain Collider) or the Paint Surface. With neither, it projects onto the area's horizontal plane.

The component's context menu also has **Bake Terrain Ground Albedo**.

## Grass Density Asset

`GrassDensityAsset`. Create with the area's **Create paintable density map**, or **Assets > Create > Cone > Grass > Density Map**.

| Setting | Default | What it does |
|---|---|---|
| Width, Height | 512 × 512 | Map resolution, 16 to 4096. **Resize and preserve coverage** applies a change. |
| Clear | | Fills with 0 (no grass). |
| Fill | | Fills with 1 (full density). |

The asset stores linear 8-bit values and builds a texture from them. A map created from the asset menu starts empty.

## Grass Variant Profile

`GrassVariantProfile`. **Assets > Create > Cone > Grass > Variant Profile**. Assign it to the renderer's **Variants**. A new profile starts as the meadow preset; **Reset** in the asset's context menu restores it. See [Plant kinds](plant-kinds.md).

The defaults below are the code defaults of the fields, used by a profile or kind created from a script. The meadow preset's values are listed in [the meadow preset](plant-kinds.md#the-meadow-preset).

### Profile

| Setting | Default | What it does |
|---|---|---|
| Seed | 1 | Re-deals every kind decision and drift without moving a root. |
| Clump Size | 12 m | Diameter of a drift and of a colour clump. |
| Cluster Size | 0.7 m | Diameter of a cluster of plants inside a drift. |
| Habitat Size | 40 m | Scale of the moisture field the kinds' habitats follow. |
| Bud Fraction | 0.12 | Fraction of heads that are closed buds. 0 to 0.5. |
| Seed Fraction | 0.08 | Fraction of heads gone to seed. 0 to 0.5. |
| Head Draw Distance | 60 m | Heads are drawn to this distance. Beyond it a flower's blade tip carries its colour. |
| Head Fade | 0.3 | Fraction of Head Draw Distance over which heads shrink away. |
| Distant Tip Colour | 0.8 | How strongly a far flower's tip takes its mean petal colour once its head has gone. |
| Head Mesh | none | Mesh for kinds whose head is **Mesh**: about one unit across, pivot at the stem, +Y up. |
| Kinds | (preset) | Up to eight kinds. Extra entries are ignored. |

### Kind

| Setting | Default | What it does |
|---|---|---|
| Name | Grass | Label, shown in Placement Area weight lists. |
| **Mix** | | |
| Weight | 1 | Share of the roots, relative to the other kinds. 0 grows none. |
| Clumping | 0 | 0 sprinkles the kind evenly; 1 gathers all of it into clusters inside drifts. Its share stays the same. |
| Drift Area | 0.25 | Fraction of the ground the kind's drifts cover. 0.01 to 0.99. |
| Cluster Area | 1 | Fraction of a drift its clusters cover. 1 fills the whole drift. |
| **Habitat** | | |
| Moisture | 0 | -1 prefers dry ground, 1 wet ground, 0 anywhere. |
| Slope | 0 | -1 prefers flat ground, 1 slopes, 0 anywhere. |
| **Blade** | | |
| Height Scale | 1 | Blade height relative to the material's Grass Height. |
| Width Scale | 1 | Blade width relative to the material's Grass Width. |
| Curving Scale | 1 | Curvature relative to the material's Grass Curving. |
| Stem Width | 0 | 0 tapers to a point; 1 keeps the full width to the tip. About 0.5 makes a stem for a head. |
| Height Randomness | -1 | Replaces the material's Grass Height Randomness for this kind. Below 0 keeps the material's. |
| **Blade colour** | | |
| Tint | white | Multiplies the blade colour. |
| Variation Tint | (1.15, 1.05, 0.7) | The colour variation moves toward this tint. |
| Instance Variation | 0.25 | How far each plant's own random number moves it toward Variation Tint. |
| Clump Variation | 0.25 | How far each clump's smooth noise moves it, so neighbours drift together. |
| **Head** | | |
| Head | None | None, Daisy, Cup, Umbel, Spike, Clover, Mesh or Puff. |
| Head Size | 0.04 m | Head diameter. |
| Head Size Randomness | 0.3 | Each head's size varies by up to this fraction either way. |
| Sideways | 0.25 | Fraction of heads that nod sideways instead of facing the sun. |
| Palette | white | Petal colours; each head picks one. Up to four are used. |
| Palette Clumping | 0.6 | 0 picks each head's colour independently; 1 gives a whole drift one colour. |
| Centre | (0.95, 0.75, 0.15) | Colour of the disc or florets. |

## Grass Collider Interactor

`GrassColliderInteractor`. **Add Component > Cone > Grass > Collider Interactor**. See [Interaction](interaction.md).

### Character and support

| Setting | Default | What it does |
|---|---|---|
| Actor Collider | the object's only collider | The upright CapsuleCollider, CharacterController or SphereCollider that touches the grass. |
| Actor Root | this transform, if it contains the collider | Root of the character. Colliders under it are ignored when looking for the ground. |
| Explicit Support | none | A supporting collider for legacy or generic surfaces that have no Placement Area. |
| Capture Shader | none (package material) | A custom interaction shader with a `GrassSlope` pass that writes premultiplied vertex RGBA. |
| Accepted Support Layers | Everything | Layers a supporting surface may be on. |
| Uncovered Support Layers | Nothing | Floors on these layers carry contacts where no grass area reaches, for effects that read the interaction field. |
| Update Mode | Late Update | **Late Update** for characters moved in Update, **Fixed Update** for physics-driven characters, **Manual** to call `Sample(double)` yourself. |
| Ground Clearance | 0.15 m | Largest gap below the collider that still counts as touching the ground. |
| Allowed Penetration | 0.1 m | Tolerance for a collider sunk slightly into the ground. |
| Attenuate With Clearance | off | Weakens the bend as the collider rises through Ground Clearance. |

### Interaction and recovery

| Setting | Default | What it does |
|---|---|---|
| Bend Strength | 0.85 | Largest bend, 0 to 1. |
| Attack Duration | 0 s | Time constant of the response to new contact. 0 bends at once. |
| Recovery Duration | 2 s | Time for a sample to recover fully after the collider leaves it. |
| Footprint Padding | 0 m | Extra horizontal reach of the bend. The ground query still uses the real collider. |
| Grid Spacing | 0.15 m | Spacing of the world-space grid that stores the trail. Smaller needs more nodes. |
| Node Capacity | 1024 | Trail samples kept, 16 to 2048. |
| Teleport Distance | 5 m | A jump farther than this between samples clears the trail instead of joining it. |

## Grass Interactor

`GrassInteractor`. **Add Component > Cone > Grass > Interactor**. The interaction component from earlier versions, kept for existing scenes and scripts. It is a Grass Collider Interactor whose settings come from these public fields; it also shows the inherited Actor Root, Explicit Support, Uncovered Support Layers, Update Mode, Allowed Penetration, Grid Spacing, Node Capacity and Teleport Distance.

| Setting | Default | What it does |
|---|---|---|
| Body | the object's only supported collider | The actor collider. |
| Interaction Shader | none (package material) | As Capture Shader. |
| Ground Layers | Everything | As Accepted Support Layers. |
| Radius Padding | 0.15 m | As Footprint Padding. |
| Blade Height | 0.5 m | Ground clearance, with the bend weakening as the collider rises through it. |
| Strength | 0.9 | As Bend Strength. |
| Attack Seconds | 0.06 s | As Attack Duration. |
| Recovery Seconds | 0.55 s | As Recovery Duration. |

## Grass Blade material

The **Grass Blade** material uses the shader `InfiniteGrass/GrassBladeShader`. "Material" is the value in the package's Grass Blade material; "Shader" is the shader's default for a new material.

### Colour and shape

| Property | Material | Shader | What it does |
|---|---|---|---|
| Color | (0.23, 0.69, 0.31) | black | Blade colour. |
| AO Color | (0.20, 0.47, 0.15) | grey | Colour toward the root, before ground blending. |
| Grass Width | 0.25 | 1 | Blade width. The blade's root is half this value wide, in metres. |
| Grass Height | 1.5 | 1 | Blade height in metres. Plant kinds scale it. |
| Grass Width Randomness | 0.25 | 0.25 | Each blade is up to this fraction narrower. |
| Grass Height Randomness | 0.5 | 0.5 | Each blade is up to this fraction shorter. |
| Grass Curving | 0.1 | 0.1 | How far the blade bends over. |
| Expand Distant Grass Width | 0.1 | 1 | Extra width added to distant blades. |
| Expand Distant Grass Range | (10, 300) m | (50, 200) m | Distances over which that width is added. |

### Wind

| Property | Material | What it does |
|---|---|---|
| Wind Texture | package wind texture, tiling 0.02 | Scrolling texture that pushes the blades. |
| Wind Scroll | (-0.15, -0.15) | Scroll speed and direction in X and Z. |
| Wind Strength | 0.7 | Push strength. |

### Ground blending

| Property | Default | What it does |
|---|---|---|
| Ground Color Blend, Ground Blend Height | | Set by the renderer's Ground Blend Strength and Height each frame. |
| Ground Blend Along Whole Blade | 0 | Keeps at least this fraction of the ground blend through the body and tip. 0 keeps the root-to-tip fade. |
| Ground Colour Along Whole Blade | off | Keeps the ground colour from root to tip, so light rather than a tint shades the blade. |
| Terrain Splat Colour And Detail At Roots | off | Takes colour, normal detail, smoothness and metallic from the bound URP Terrain/Lit splat layers at each root. Reads the terrain's first control map and first four layers; layer mask maps are not read. |
| Canopy Occlusion | off | Darkens each point of a blade by the blades above it, so dense grass has depth, while its average still matches the bare terrain beside it. |
| Match Ground Normal at Roots | off | Lights the blade with the terrain's normal at its root, turned by the blade's bend, so slopes shade like the ground. Costs extra height samples. |

### Lighting and antialiasing

| Property | Material | What it does |
|---|---|---|
| Random Normal | 0.229 | Random tilt of each blade's normal, for variation in lighting. |
| Forward+ Additional Lights | off | Lets blades receive point and spot lights. Works with the Forward+ rendering path only. |
| Specular Fade Start, End | | Set by the renderer's Specular Fade Range. |
| Minimum Blade Width (Pixels) | | Set by the renderer's Minimum Pixel Width. |

Custom blade shaders need a `GrassForward` pass; contact shadows also need `GrassContactDepth`, and motion vectors a `MotionVectors` pass following the package's history contract. Start from a copy of `GrassBladeShader`.

## Scripting

Add `using Cone.Grass;`. The main entry points:

| Member | What it does |
|---|---|
| `InfiniteGrassRenderer.Instance` | The active renderer, or null. |
| `InfiniteGrassRenderer.RefreshGrassData()` / `MarkDirty()` | Recaptures everything: after spawning modifier renderers, changing their materials, or editing cached meshes. |
| `InfiniteGrassRenderer.RefreshGrassInteraction()` | Recaptures the interaction field only. |
| `InfiniteGrassRenderer.VisibleGrassCount`, `OverflowGrassCount` | Asynchronous counts, updated while Preview Visible Grass Count is on. |
| `InfiniteGrassRenderer.DrawsFor(Camera)` | Whether a camera draws the grass. |
| `GrassPlacementArea.SetDensityAsset(asset, fitTerrain)` | Assigns a density map and switches to the Texture shape. |
| `GrassPlacementArea.ConfigureTexture(terrain, texture, layer, colour)` | Binds a whole-terrain generated mask, as the MicroVerse bridge does. |
| `GrassPlacementArea.ConfigureGroundLayer(layer, strength)`, `SetGroundColor(tint, strength)`, `SetGroundColorTexture(texture, terrainAligned, strength)` | Ground colour matching. |
| `GrassPlacementArea.SetVariantWeights(params float[])`, `ClearVariantWeights()` | Turns the area's kind weight override on with these weights, or off. |
| `GrassPlacementArea.Paint(worldPosition, radius, strength, hardness, erase)` | Paints the area's density asset at a world position, also at runtime. Needs the Texture shape with a Density Asset; returns false otherwise. |
| `GrassPlacementArea.MarkCoverageDirty()`, `MarkGroundColorDirty()`, `MarkSurfaceDirty()`, `MarkDirty()` | Report changes the area cannot see, such as GPU writes into a density or colour RenderTexture. |
| `GrassColliderInteractor.Configure(actor, support, selfRoot)` | Reuses the component for another actor; clears the trail. |
| `GrassColliderInteractor.ClearHistory()` | Clears the trail at once. Call after teleports, respawns and floating-origin shifts. |
| `GrassColliderInteractor.Sample(double time)` | One observation, in Manual update mode. |
| `GrassColliderInteractor.State`, `Diagnostic` | What the interactor is doing, and a hint when it is not bending grass. |
| `GrassVariantProfile.ResetToMeadow()`, `MarkDirty()` | Restores the meadow preset; reports script edits to the kinds. |
