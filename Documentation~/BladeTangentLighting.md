# Blade tangent lighting

## Reference and scope

The public [Stix Games visual guide](https://docs.stixgames.com/guides/improve-visuals)
for the [DirectX 11 Grass Shader](https://assetstore.unity.com/packages/vfx/shaders/directx-11/directx-11-grass-shader-36335)
emphasizes convincing wind and dense near-camera detail. Its
[performance guide](https://docs.stixgames.com/guides/performance-optimization)
also calls out per-blade geometry cost and choosing appropriate lighting work.
Those goals inform this change; no paid source, texture, or wind implementation
is copied.

This package already deforms its blades with wind and curvature. The change
improves the **lighting tangent of that same curve**, rather than adding another
wind texture sample, reducing grass density, or changing its silhouette.
It affects the terrain-matched lighting path selected by
`Match Ground Normal at Roots` (`_GRASS_GROUND_NORMAL`). That path turns the
terrain normal by the blade's bend from its root to the current height.
The ordinary stylized lighting path does not consume these tangents and may
already have them removed by the compiler; this is not a claimed optimization
of every material variant.

## Why the old tangent was unstable

The previous helper subtracted two absolute world-space center positions at
height fractions `h - 0.01` and `h + 0.01`, clamped to the blade endpoints.
It then normalized the difference, falling back to the radial blade direction
when its squared length was at most `1e-12`.

This introduced three avoidable sources of lighting error. Large world pivots
could round away the small difference before normalization. Root and tip
samples used one-sided chords rather than the endpoint derivatives. Finally,
the fixed squared-length threshold depended on world-space blade size, so a
uniformly scaled-down blade could use a different lighting direction despite
having the same shape.

These are numerical properties of the old expression, not a claim that every
backend compiles it identically or that a rendered artifact has been measured.

## Analytic local derivative

Let `h` be the blade-height fraction, `H` its height, `s` its support direction,
`w` its horizontal wind vector, and `c` its horizontal curvature vector.
The existing center line is:

```text
C(h) = pivot + H h normalize(s + h w) + h^2 c
q = s + h w
u = q / |q|
u' = (w - u dot(u, w)) / |q|
C'(h) = H (u + h u') + 2 h c
```

`GrassTangentAtHeight` now normalizes this derivative directly. The world pivot
is not read. Height and curvature are divided by a common positive shape scale
before forming the derivative, preserving its direction without a world-size
threshold. A zero-size or locally stationary curve retains the finite radial
fallback rather than normalizing a zero tangent.

At `h = 0`, the tangent follows the already normalized support direction.
`BuildGrassVertex` reuses that value instead of calculating a second tangent.
The old two tangent calls each sampled two centers; none of those four
source-level center evaluations remains in tangent calculation. Actual backend
instruction counts and frame time still depend on compiler optimization and
hardware.

## Preserved behavior and limits

The center line, row positions, facing, topology, geometry LOD, minimum pixel
width, density and coverage calculations are unchanged. Forward, contact-depth,
and previous-frame motion reconstruction retain their existing geometry paths.
No material property, keyword, pass, buffer, texture, draw distance, or native
test identity is changed. The earlier canopy-lighting repair remains intact.

This does not solve precision loss in world-space vertex positioning, capture
coordinates, or world-seeded randomness. Translating a whole scene can change
those inputs independently of its tangent calculation. It also does not change
normal interpolation across a coarse LOD or remove the existing output half
precision. The derivative assumes the valid finite root data produced by the
shared blade builder; it is not a general sanitizer for arbitrary invalid
material values.

## Automated regression checks

Run the existing shader-tool discovery command:

```sh
python -B -m unittest discover -s Tools~/ShaderChecks -p 'test_*.py' -v
```

`test_blade_tangent.py` adds fourteen tests. A restricted interpreter reads the
actual helper expressions from the shader and evaluates scalar/vector binary32
operations. Independent double-precision five-point differences of the original
center curve provide 1,280 varied shape/height comparisons. Further controls
cover straight blades, root/tip derivatives, translated pivots, uniform scale,
zero size, curvature-only shapes, endpoint clamping, root-direction reuse and
unchanged vertex-position calls. Mutations omitting the wind derivative or the
curvature factor must disagree with the oracle. A retained old-expression
control reproduces world-position cancellation.

This CPU interpreter does not execute HLSL, emulate driver contraction or native
half precision, render images, or measure GPU speed. The separate existing DXC
job compiles production shader variants against pinned Unity headers to DXIL
and SPIR-V. Neither replaces licensed Unity import or graphics execution.

## Native A/B qualification

Compare the base and changed shader with identical Unity/URP versions, GPU,
resolution, camera path, root data, seed, density, draw distance, geometry LOD,
lights and exposure. Start with TAA disabled and motion history Off. Compare
actual supported single-sample and MSAA targets separately.

Enable ground-normal matching for the positive cases, with straight, curved,
wind-bent and interaction-bent blades on flat and sloped terrain. Test the roots,
tips, LOD boundaries, low-height density transitions and canopy on/off. Freeze
wind for matched stills, then run continuous wind and camera motion. Keep the
ordinary stylized mode as an unchanged-path control.

For translation/scale controls, preserve resolved blade parameters and account
for changed world seeds, capture precision and projection independently; a
whole-scene image difference is not automatically tangent error. Warm the
renderer before measuring the grass forward pass and total frame, retain
median/tail GPU timings and visible counts, and compare equal coverage. Do not
convert fewer source evaluations into an FPS claim.

Native image and performance acceptance remains under #28, #33 and #34. This
change does not close those issues or qualify the separate failures in #66.
