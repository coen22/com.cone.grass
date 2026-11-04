# Integrations

- [Terrain ground-albedo baker](#terrain-ground-albedo-baker): match grass roots to a blend of terrain layers.
- [MicroVerse mask bridge](#microverse-mask-bridge): drive grass placement from MicroVerse splines.
- [Crest 5 water level](#crest-5-water-level): keep grass above Crest water.
- [Reading the interaction field](#reading-the-interaction-field): use character contacts in your own shaders.

## Terrain ground-albedo baker

A Placement Area's **Ground Layer** matches one TerrainLayer. Where the ground under the grass blends several layers (grass into dirt, say), bake the terrain's blended colour into a map instead.

### Bake

1. Open **Tools > Cone > Grass > Bake Terrain Ground Albedo**, or use **Bake Terrain Ground Albedo** in a Placement Area's component context menu.
2. Assign **Terrain**.
3. Optionally assign **Assign to area**: a Placement Area that uses the same Terrain. The baked map is then assigned to it as its **Ground Color Texture**, mapped over the whole terrain.
4. Set **Resolution** (default 1024, 16 to 4096) and the output **Texture asset** path under `Assets/` (default `Assets/TerrainGrassAlbedo.asset`).
5. Click **Bake ground albedo**.

Rebake after repainting the terrain. Baking again into the same asset keeps its identity, so scene references stay intact and the grass picks up the new colours.

A full-terrain map uses terrain-aligned coordinates, so a small painted grass patch still reads the right part of the map.

### What it captures

The baker supports the native **Universal Render Pipeline/Terrain/Lit** shader. It uses URP's own terrain blend functions on the GPU, so it matches what the terrain draws:

- every TerrainLayer and every control map, including more than four layers;
- terrain-relative tiling and offsets;
- diffuse remapping, including opacity as density;
- height-based blending, when the terrain material enables it;
- non-readable source textures.

The output is unlit albedo: no lighting, shadows, normal maps or decals. Lighting is applied once when the blades are drawn. The map is a linear RGBAHalf texture with mipmaps and clamp addressing. At 1024² it uses about 10.7 MiB of GPU memory, plus a CPU copy in the asset. Baking reads the GPU result back once; it is for authoring, not for use at runtime.

### Custom terrain shaders

Custom terrain shaders, Shader Graph terrains and MicroSplat are not supported by the baker. Produce a final albedo map from that shader's own tools, assign it as the area's **Ground Color Texture** and turn on **Ground Color Uses Terrain Bounds**.

A custom shader that keeps URP Terrain/Lit's painted-albedo calculation unchanged can opt in with the SubShader tag `"GrassAlbedo" = "TerrainLit"`. It must provide `_HeightTransition`.

### From a script

The baker is in the `Cone.Grass.Editor` assembly.

```csharp
using Cone.Grass;
using Cone.Grass.Editor;
using UnityEngine;

if (TerrainGrassAlbedoBaker.TryBake(terrain, 1024, "Assets/TerrainGrassAlbedo.asset",
        out Texture2D albedo, out string error))
    area.SetGroundColorTexture(albedo, true);
else
    Debug.LogError(error);
```

`TryBake` raises the `Baked(Terrain, Texture2D)` event after saving. Record Undo on the area yourself when you change its binding from an editor tool.

## MicroVerse mask bridge

The **MicroVerse Mask Bridge** sample connects MicroVerse's saved **MaskTarget** textures to Placement Areas, so one Spline Area can paint both the ground texture and the grass. It contains no MicroVerse code and has no compile-time dependency on MicroVerse.

Requirements: MicroVerse Core, Splines and Masks, and Unity Splines. The Vegetation module is not needed.

### Install

In Package Manager, select **Infinite Grass**, open **Samples** and import **MicroVerse Mask Bridge**. It adds the assemblies `Cone.Grass.MicroVerse` and `Cone.Grass.MicroVerse.Editor`, with tests in `Cone.Grass.MicroVerse.Tests`.

### One spline for ground and grass

1. Create a closed spline round the grass patch and add MicroVerse's **Spline Area**.
2. Under it, add a **Texture Stamp** with the grass TerrainLayer and **Spline Area** falloff.
3. Create a **MaskTarget** asset for grass density: **R8**, cleared to black. Do not use an SDF target.
4. Add a **Mask Stamp** that writes white to the red channel of that target, in the same Spline Area. Match the Texture Stamp's falloff and filters, so the grass and the ground texture cover the same region.
5. Let MicroVerse finish and save the scene.
6. Add **Infinite Grass > MicroVerse Mask Bridge** to each affected Terrain GameObject. It adds the Placement Area it needs.
7. Assign **Mask Target** and **Terrain**, then pick this terrain's texture in **Generated Density Texture**. The bridge never guesses which texture belongs to which terrain.
8. Set **Grass Terrain Layer** to the Texture Stamp's TerrainLayer. For mixed grass and dirt edges, turn on **Bake Terrain Ground Albedo**, choose a unique output asset per terrain, and click **Bake Terrain Albedo and Refresh**.
9. Save the scene.

A spline that crosses terrains needs one bridge per terrain, each with its own texture.

### Bridge settings

| Setting | Default | What it does |
|---|---|---|
| Mask Target | none | The MicroVerse MaskTarget asset. |
| Terrain | none | The terrain this bridge serves. |
| Generated Density Texture | none | This terrain's texture inside the MaskTarget, by its unique name. |
| Grass Terrain Layer | none | The ground layer to match, as on a Placement Area. |
| Bake Terrain Ground Albedo | off | Bakes the terrain's blended albedo for ground matching. |
| Ground Bake Resolution | 1024 | Bake resolution, 64 to 4096. |
| Ground Albedo Asset Path | empty | Output asset; one per terrain. |
| Ground Color Override | none | A terrain-aligned colour map, used when baking is off. Takes precedence over the layer. |
| Ground Tint, Ground Color Strength | white, 1 | As on a Placement Area. |
| Auto Refresh | on | Refreshes after saved asset changes, scene saves, inspector edits, terrain texture changes and Undo. |
| Poll Saved Outputs | on | Also checks the saved textures periodically. Costs nothing when unchanged; nothing in the player. |
| Poll Interval | 1 s | Polling interval, 0.25 to 10 s. |

The bridge owns its Placement Area's terrain, shape, density texture, edge falloff and ground colour. Change them on the bridge, not on the area. The mask already holds the spline's falloff, so the area's **Edge Falloff** is set to 0.

Density must be linear data in a normalised or floating-point red channel. sRGB, alpha-only, integer and depth formats are rejected. A missing, unsupported or ambiguous texture grows no grass until fixed.

### Editing and refresh

The bridge reads *saved* mask outputs. It cannot tell when MicroVerse is still generating, so coverage can lag by the refresh delay or show an intermediate result. When generation finishes, click **Refresh Saved Mask**. **Bake Terrain Albedo and Refresh** also forces a new ground bake.

Changing mask format or resolution can replace the texture inside the MaskTarget. The bridge finds it again by name. If the name changes, pick the new texture. If two textures share a name, rename one: the bridge never picks for you.

Changing the bridge's Terrain or Mask Target clears the selected texture, and changing the Terrain also clears the bake path, so one tile cannot overwrite another's bake.

### Builds

Before building: let MicroVerse finish, save its assets, refresh the bridge, bake if needed, and save the scene. The Placement Area stores the resolved texture and terrain in the scene, so the player needs no MicroVerse code.

Every player build, including incremental builds, checks the saved scenes. A stale binding or bake fails the build with the scene, the object and the fix. The build removes the bridge component from the built scene; the Placement Area stays. Custom content pipelines can call `GrassMicroVerseBridgeBuildProcessor.ValidateSavedScenes(scenePaths)` themselves.

## Crest 5 water level

**Grass Crest Water Level** (`Cone.Grass.Crest.GrassCrestWaterLevel`) feeds the Crest 5 water height into the renderer's [Water Line](reference.md#water-line), so no grass grows below Crest water and the shore thins in over the **Shore Fade** band. It lives in the `Cone.Grass.Crest` assembly, which compiles only when the Crest package (`com.waveharmonic.crest` 5.0 or later) is installed (a `versionDefines` entry sets `CONE_GRASS_CREST`). Without Crest the package compiles and runs as before.

Add the component anywhere in the scene, near the water you care about. It attaches itself to the active Infinite Grass Renderer and turns its water line on; disabling it detaches it again.

| Setting | Default | What it does |
|---|---|---|
| Renderer | none | The grass renderer to drive. Empty uses the active Infinite Grass Renderer. |
| Mode | Surface Height | **Sea Level** reads the Water Renderer's height. **Surface Height** asks Crest's collision query for the water surface at the sample point, which includes waves and level inputs such as a raised lake; it falls back to the sea level while the query has no result yet. |
| Sample Point | none | Where Surface Height is queried. Empty uses the component's own position. |
| Offset | 0 m | Added to the Crest height. A few centimetres keep blades out of wave troughs and the meniscus. |
| Minimum Wave Length | 1 m | Smallest wave length the surface query resolves. Larger values ignore ripples. |

The water line is one height per frame, not a per-blade surface lookup: on a rough sea, add the wave amplitude to **Offset** or widen **Shore Fade**. The component queries Crest once per frame however many cameras render.

## Reading the interaction field

The renderer feature publishes the grass interaction capture as global shader properties after each camera's capture. See [Interaction](interaction.md#shared-interaction-field) for their layout. A URP shader reads them like this:

```hlsl
TEXTURE2D(_ConeInteractionField);
SAMPLER(sampler_ConeInteractionField);
float4 _ConeInteractionFieldST;
float4 _ConeInteractionFieldParams;

// Contact strength (0 to 1) and push direction in world XZ at a world position.
float ConeInteraction(float3 positionWS, out float2 direction)
{
    direction = 0;
    if (_ConeInteractionFieldParams.x < 0.5)
        return 0;                                    // No field published for this camera.
    float2 uv = positionWS.xz * _ConeInteractionFieldST.xy + _ConeInteractionFieldST.zw;
    if (any(uv < 0) || any(uv > 1))
        return 0;                                    // Outside the capture window.
    float4 field = SAMPLE_TEXTURE2D_LOD(_ConeInteractionField, sampler_ConeInteractionField, uv, 0);
    if (field.a > 1e-4)
        direction = field.rg / field.a * 2 - 1;      // Undo the premultiplication.
    return field.a;
}
```

Your pass must run after the grass feature's pass, which runs before transparents. A transparent material, or a renderer feature at **After Rendering Transparents** or later, sees the current frame's field.

The field covers the same window as the grass captures: **Draw Distance** plus **Texture Update Threshold** around the camera, at **Capture Resolution**. If your scene has no grass but needs the field, turn off **Draw Blades**; see [Interaction](interaction.md#shared-interaction-field).
