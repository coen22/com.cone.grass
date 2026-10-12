# Grass lighting quality and cost

## Reference and implementation choice

The [Stix Games DirectX 11 Grass Shader](https://assetstore.unity.com/packages/vfx/shaders/directx-11/directx-11-grass-shader-36335)
was reviewed as a design reference, using its public documentation rather than
its paid source or textures. Its [performance guide](https://docs.stixgames.com/guides/performance-optimization)
recommends spending geometry near the viewer, matching distant ground coverage,
and enabling only the lighting work an effect needs. Its
[visual guide](https://docs.stixgames.com/guides/improve-visuals) discusses dense
near grass, distance falloff, and wind with both waves and smaller ripples.

This package already has separate geometry LOD meshes, distance population
falloff, ground-color matching, wind deformation, and optional additional lights.
The first change here removes redundant work from those existing lighting
paths without making the grass sparser. It does not port the reference's
geometry-shader implementation or add its other features.

## Correct neutral lighting

Previously, every fragment evaluated the bare ground's lighting and the mean
lighting seen under the canopy, even with `Canopy Occlusion` disabled. The final
color was always multiplied by:

```hlsl
bareLight / max(seenLight, 1e-4)
```

With no canopy, `bareLight == seenLight` should produce a scale of one. Instead,
a reference channel of `0.00005` produced a scale of `0.5`, and a black channel
produced zero. A bent blade or its wrapped lighting can receive light even when
the corresponding bare-ground reference does not, so this could erase useful
lighting or change color in dim and strongly colored scenes.

Disabled and empty canopies now bypass compensation entirely. An enabled,
nonempty canopy uses equal numerator and denominator floors:

```hlsl
max(bareLight, 1e-4) / max(seenLight, 1e-4)
```

This preserves a neutral channel from black through bright light, remains finite
for zero reference channels, and retains the original ratio when both reference
values are above the threshold. It is a numerical safeguard, not an additional
light source: multiplying a black blade result still returns black. The ratio
is evaluated as `float3`; the renderer's existing lighting accumulation precision
and HDR output limits are not otherwise changed.

## Work avoided

The existing serialized `Canopy Occlusion` toggle controls explicit branches;
no new keyword, material migration, render pass, texture, or buffer is required.
With the toggle disabled, the vertex stage avoids canopy density lookup and
canopy view calculations. With no positive canopy area, it also avoids the
view integration. The fragment stage avoids:

- A second bare-ground ambient/environment evaluation, including the terrain
  BRDF/probe path when ground-normal matching is enabled.
- A second bare-ground direct-light evaluation for the main light and each
  enabled Forward+ additional light.
- Canopy sky/transmission/view integrations and the final compensation divide.

Ordinary blade ambient and direct lighting, shadows, terrain-root matching,
fog, coverage, antialiasing, geometry, wind, LOD population, and draw distances
are retained. When canopy shading is enabled with positive area, its original
sky and per-light attenuation and ground matching remain active. No performance
claim relies on fewer blades or a shorter draw distance.

These are source-level work reductions. Backend scheduling, branch behavior,
and actual frame-time savings require profiling on the target GPU. The enabled
path must also be profiled; adding explicit branches is not automatically a win
for every material or graphics backend.

## Regression checks

`Tools~/ShaderChecks/test_canopy_lighting.py` adds ten source/math tests. The
numeric controls interpret the shipped compensation expression componentwise,
including black, dim, colored, and HDR-valued references, zero transmission,
monotonic compensation, and preservation of the previous above-threshold ratio.
A control reproduces the old zero/half-intensity failure. Structural controls
verify the optional branch boundaries while retaining main and both additional
light loops, ground matching, fog, coverage, and the same three shader passes.

Run them with:

```sh
python -B -m unittest discover -s Tools~/ShaderChecks -p 'test_*.py' -v
```

The existing CI shader-tool jobs discover the new file on Linux and Windows.
The separate DXC job compiles the shipped shader variants to DXIL and SPIR-V.
Source/math checks do not execute HLSL, emulate GPU precision, validate Unity
shader import, or establish image/performance acceptance. No native test identity,
assertion, skip rule, or batch reload-guard inventory was changed.

## Native A/B qualification

Use the same Unity/URP version, GPU, resolution, camera path, grass seed,
material, density, draw distance, LOD settings, wind time, exposure, and light
setup for the base and changed shader. Record the actual material toggle and
keywords, not just their inspector defaults. Keep TAA off for the baseline;
repeat single-sample and MSAA separately, without treating MSAA coverage as an
identical single-sample image.

Check canopy off/on, sparse/dense areas, ground-normal matching off/on, terrain
splat matching, main-light-only and Forward+ additional directional/point/spot
lights, and shadow variants. Include daylight, a low-intensity light sweep
through the numerical floor, colored lights with zero channels, dark ambient,
and grazing views. Freeze exposure and wind for pixel comparisons, then inspect
motion separately. Verify that the disabled path retains bent-blade lighting,
and that enabled daytime canopy contrast and terrain-root matching remain.

Warm up before timing. Compare repeated equal-coverage GPU captures of the grass
forward pass and total frame, retaining median and tail timings, visible blade
counts, resolution, and the compiled backend. Do not report source operation
counts as FPS improvement. Native rendering and performance acceptance remain
tracked in #28, #33, and #34; this change does not close those broader issues or
qualify the separate interaction route in #46.
