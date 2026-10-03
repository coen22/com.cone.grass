# Terrain ground-albedo baking

Use **Tools → Cone → Grass → Bake Terrain Ground Albedo**, assign a Terrain, choose a Texture2D `.asset` path below `Assets/`, and bake. The same command is available on a Grass Placement Area's component context menu. Assigning an area in the window binds the result with terrain coordinates, so a small painted density patch still samples the correct location in the full-terrain color map.

The baker supports the native **Universal Render Pipeline/Terrain/Lit** shader. It reads every TerrainLayer and control-map group on the GPU, including non-readable diffuse and mask textures. The output contains unlit albedo and is suitable for the placement area's ground-color override.

## Matching the native terrain

The editor shader directly includes the installed URP package's `TerrainLitInput.hlsl` and `TerrainLitPasses.hlsl`. It calls `ComputeMasks`, `HeightBasedSplatModify` and `SplatmapMix`, preserving:

- Terrain-relative diffuse tiling and offsets.
- Control-map texel-center sampling and contributions from all four-layer groups.
- Diffuse remap scale, including the opacity-as-density switch stored in its alpha component.
- Remapped mask-blue height blending and the native rule that disables this blend when the terrain uses more than four layers.
- The material's active `_TERRAIN_BLEND_HEIGHT` keyword. Changing `_EnableHeightBlend` from a script also requires updating the native material's keyword, as it does for the terrain itself.
- Original control-group weight after the native height/density normalization, followed by additive composition of groups.

The result is the material's blended albedo. Lighting, shadowing, terrain normal maps, decals, and custom shader effects are outside this capture. Custom terrain shaders, including Shader Graph terrain variants and MicroSplat, are rejected explicitly; use their final-albedo output as a ground-color map instead.

The implementation follows the native forward albedo path rather than copying the basemap generator's different opacity-normalization shortcut. Source references:

- [TerrainLitPasses.hlsl](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Shaders/Terrain/TerrainLitPasses.hlsl)
- [TerrainLitInput.hlsl](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Shaders/Terrain/TerrainLitInput.hlsl)
- [TerrainLitShaderGUI.cs](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Editor/ShaderGUI/TerrainLitShaderGUI.cs)
- [TerrainLitBasemapGen.shader](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Shaders/Terrain/TerrainLitBasemapGen.shader)

## Integration API

The public editor assembly is `com.cone.grass.ground-color.editor`.

```csharp
if (TerrainGrassAlbedoBaker.TryBake(terrain, 1024, "Assets/TerrainGrassAlbedo.asset",
    out Texture2D albedo, out string error))
{
    area.SetGroundColorTexture(albedo, true);
}
```

Record Undo and prefab overrides when an editor integration changes the area's serialized binding. `TryBake` preserves an existing output Texture2D asset's identity and emits `Baked(Terrain, Texture2D)` after saving. Reusing the same asset therefore still allows consumers to invalidate their cached ground-color capture. An observer exception is logged while the remaining observers still receive that saved output. The bake never changes the terrain or its source textures.

The output remains owned by the active bake until save callbacks and observers finish. A nested `TryBake` targeting that same asset returns `false` with an explanatory error, including when an observer changes the path's casing, uses Windows separators, or moves the asset. Queue a later bake instead of requesting the same output synchronously from its notification. `IsBakingOutput(path)` lets an integration recognize this temporary state and preserve its committed binding while deferring work. Separate output assets remain independent.

`TryGetSourceSignature(Terrain, int, out Hash128, out string)` provides a cheap signature for polling within an editor session. It includes source properties, texture update counts, independent U/V addressing, filtering, anisotropy, relevant source-import revisions, both the native terrain and albedo capture shaders, terrain texture callbacks and Undo/Redo. It does not require an output path or pixel readback. Both addressing axes are tracked because [Unity's `Texture.wrapMode` getter returns only the U axis](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Texture-wrapMode.html). `Invalidate(Terrain)` handles GPU changes that do not emit a Terrain callback. `SourceChanged` reports terrain texture callbacks and explicit invalidation; a failing subscriber is logged without interrupting the producer or preventing other observers from receiving the change. Polling still detects layer, material and source-import changes.

Only terrains queried for a signature retain source state. Removed dependency paths are released, and a periodic editor cleanup drops destroyed terrains and entries unused for ten minutes. A new session generation makes a recreated entry differ from an old cached signature; actively polled terrains keep their signatures stable.

The session signature includes Unity entity identifiers and is unsuitable for persisted build validation. An integration can persist its own source asset and native/capture shader dependency hashes plus the resolution and public `BakerVersion`, while using the session signature for live updates. The capture shader is identified by the public `BakeShaderName`. The MicroVerse sample uses this distinction, so a shader update requires a replacement bake even when the terrain inputs are unchanged.

## Output and validation

The saved map is linear RGBAHalf with mipmaps and clamp addressing. Half precision preserves remapped values without clamping them to an 8-bit range. At 1024², the GPU image with its mip chain occupies approximately 10.7 MiB; the readable asset also retains a CPU copy. Choose resolution for the ground-color detail you need. Baking performs one synchronous GPU readback after composing all layers and is intended for authoring, not the game loop.

The accompanying Editor tests exercise actual GPU capture and asset persistence: non-readable textures, multiple control groups, height/opacity behavior, diffuse remapping, UV orientation/tiling/offset, stable asset identity, signature changes, unsupported materials, and source-overwrite rejection. Run them in a URP Editor test project with a graphics device. Unity compilation and these GPU tests were not available in the implementation workspace; they remain required before release.
