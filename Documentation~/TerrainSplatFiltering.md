# Terrain texture filtering and sampling cost

## Reference and affected settings

The public [Stix Games visual guide](https://docs.stixgames.com/guides/improve-visuals)
for the [DirectX 11 Grass Shader](https://assetstore.unity.com/packages/vfx/shaders/directx-11/directx-11-grass-shader-36335)
recommends matching distant grass to the ground texture. Its
[performance guide](https://docs.stixgames.com/guides/performance-optimization)
also emphasizes per-blade cost and paying only for features that contribute.
This patch applies those ideas to the existing URP terrain-matching path; no
paid shader source or textures are copied.

The changes apply when **Terrain Splat Colour And Detail At Roots** is enabled
(`_TerrainSplatAtRoots`). Normal-map filtering matters when **Match Ground
Normal at Roots** (`_GRASS_GROUND_NORMAL`) consumes the terrain normal. Materials
with terrain splat sampling disabled retain their existing path. The existing
whole-blade ground-color option also benefits from the corrected albedo input.

## Quality corrections

Previously, every layer selected a mip using only the diffuse texture's width
and the X tiling rate, then reused that mip for its normal map. This ignored
rectangular texture height, unequal X/Z tile sizes, and different diffuse/normal
resolutions. For example, a 128x1024 map tiled over 8x8 metres with a 0.125-metre
pixel footprint selected mip 1 instead of mip 4 for its more detailed axis.
A smaller normal map could instead be sampled too coarsely by reusing the
larger diffuse map's level.

The binder now calculates, separately for each actual bound texture:

```text
textureLodBias = log2(max(texture.width / tileSize.x,
                         texture.height / tileSize.y))
pixelLod = log2(max(worldUnitsPerPixel, smallestNormalFloat))
mip = max(0, pixelLod + textureLodBias)
```

The camera term is shared across layers. The texture term is computed at binding
time, including fallback white/flat-normal textures, without reading pixels.
The new expression retains fractional mip levels and the prior result for
square maps with equal tiling, apart from floating-point rounding. Zero or tiny
valid footprints select mip zero rather than taking the logarithm of zero.

This is a conservative **isotropic vertex footprint**, not an exact replacement
for TerrainLit's fragment derivatives or anisotropic filtering. Grazing slopes,
texture streaming/mip limits, and sampler differences still need native checks.
The patch does not add full mixed terrain shading, height blending, or support
for all terrain control groups.

An all-zero first control group now returns to the existing captured ground
color instead of reporting successful black albedo. This can occur where later
terrain layers supply all the paint. Mixed first/later-group behavior is not
otherwise changed; the complete baked-albedo route is still needed when this
four-layer approximation cannot reproduce the terrain material.

## Performance scope

A layer with an exactly zero sampled weight returns before its UV, texture and
surface work. Every positive weight is retained; no new near-zero threshold
cuts into painted transitions. Existing native half precision is unchanged.

For a root using one layer, the source path calls for one diffuse and one normal
sample instead of sampling both maps for all four layers: **two rather than eight
layer samples**, excluding the control-map sample. An albedo-only variant may
already have its normal samples removed by the compiler. Four-way blends still
need all contributing textures, and divergent vertices may reduce branch gains.
An empty control group avoids all layer samples and the camera logarithm.

The four repeated per-layer GPU logarithms are replaced by one shared camera
logarithm and additions to CPU-precomputed texture scales. This adds eight scalar
texture-scale logarithms per terrain binding, not per vertex. A reused four-entry
vector array replaces the old width vector: 64 rather than 16 bytes of renderer
constants. No new textures, graphics buffers, shader keywords or render passes
are added. The material constant-buffer layout is unchanged.

These are source-level cost changes, not measured GPU savings. Density, geometry,
draw distance, LOD population, coverage, wind, shadows and motion reconstruction
are unchanged; no performance claim is obtained by rendering less grass.

## Regression checks

`Tools~/ShaderChecks/test_terrain_splat.py` adds sixteen source/math tests. They
read the actual C#/HLSL helper expressions, evaluate scalar binary32 arithmetic,
and compare 1,024 varied footprints with an independent reference. Other checks
cover square-map compatibility, rectangular maps, unequal tiling, independent
normal resolution, axis/world-scale invariance, fractional and zero-footprint
mips, all sparse-layer masks, zero-only guards, binding names, shared logarithms,
and the complete pre-change material-buffer layout.

```sh
python -B -m unittest discover -s Tools~/ShaderChecks -p 'test_*.py' -v
```

`GrassTerrainSplatFilteringTests` adds eight native Editor cases for real texture
dimensions, independent scales, non-readable textures and missing-terrain binding
reset. They use owned temporary textures without changing scenes or assets.
They are authored native tests, not evidence of execution in this environment.
No existing native assertion, test identity, or reload-guard admission is changed;
use fresh discovery when running the expanded suite.

CPU expression tests do not execute HLSL, sample images, emulate native half
precision, bind Unity APIs or measure GPU time. Existing DXIL/SPIR-V compilation
and licensed Unity validation remain separate checks.

## Native A/B acceptance

Compare the base and changed renderer at identical visible coverage, camera,
resolution, exposure, light setup, terrain, wind time, density and LOD settings.
Keep TAA off for the baseline and test single-sample/MSAA separately. Include
square and rectangular maps; unequal tile sizes; different normal resolutions;
one-, two- and four-layer paint; soft transitions; empty first control groups;
missing maps; and terrain sampling/ground-normal matching off as controls.
Use mip-colored textures for level selection, then actual terrain textures for
root seams, distance transitions and motion shimmer.

Warm up both versions and retain median/tail GPU timings for the grass forward
pass and total frame, plus visible counts and CPU binding cost. Profile both
coherent single-layer regions and heavily mixed paint. Native image and timing
acceptance remains open under #28, #33 and #34; this source patch does not qualify
unrelated native failures in #66.
