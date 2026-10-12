# Shared blade-row evaluation and end-on facing

## Design reference

The public [Stix Games DirectX 11 Grass Shader](https://assetstore.unity.com/packages/vfx/shaders/directx-11/directx-11-grass-shader-36335)
and its [performance guide](https://docs.stixgames.com/guides/performance-optimization)
emphasize GPU-generated detail and controlling per-blade cost. Its
[visual guide](https://docs.stixgames.com/guides/improve-visuals) encourages
convincing blades in motion. Those goals motivated a change to this
project's own URP geometry path; no paid source code, shaders, or textures
were copied.

## Geometry problem

`EvaluateGrassRow` computed a direction at the current blade height,
then called `GrassCenterAtHeight`, which evaluated the same direction again.
Each call normalized the wind-bent support direction. The row's position
and camera-facing width depend on exactly the same height and direction.
A shared `GrassCenterFromDirection` helper now consumes that direction
without re-normalizing it. The old two-argument `GrassCenterAtHeight`
entry remains for other callers.

The color and contact-depth passes share `BuildGrassVertex`; the motion
pass reconstructs both current and previous geometry through the same row
function. `BuildGrassVertex` and the motion vertex stage also now reuse
one snapshot of the current view rather than asking for it twice. Motion
reconstruction creates one previous-view snapshot and supplies it to both
bounding rows.

These are source-level reductions. The GPU compiler might already have
combined duplicated normalizations or matrix reads; measured instruction
and GPU-time savings must be established separately.

## End-on fallback

A width tangent normally follows `cross(bladeDirection, viewRay)`. When
a camera ray almost parallels the blade direction, the cross product is
nearly zero. The old fallback used `cameraRight` directly. For an
off-axis perspective ray and a bent/sloped blade, camera right may not be
perpendicular to the blade. This can skew the width or shorten its
projected silhouette in this rare end-on orientation.

The fallback now projects camera right into the plane perpendicular to
the blade:

```
projectedRight = cameraRight - bladeDirection * dot(cameraRight, bladeDirection)
```

Only if that vector is also nearly zero does it cross the blade with
one of two world axes selected to avoid degeneracy. The ordinary,
resolved facing vector is unchanged. There are no additional texture
fetches, render passes, material properties, keywords, buffers, or
per-instance CPU allocations.

## Preserved behavior and checks

The blade center line, physical width, taper, wind/curvature, density,
LOD meshes, shadow/contact-depth, motion coverage, and draw distance
are unchanged. The fallback only changes near-parallel view directions.
No sampling, density, brightness, or shadow threshold is loosened.

`Tools~/ShaderChecks/test_blade_facing.py` adds ten source/math checks
for orthogonal/unit-width fallbacks, nondegenerate compatibility, random
near-parallel rays, unchanged center-line positions, a single direction
per row, reused view snapshots, and the shared geometry and coverage
paths. The tests read the shipped HLSL to check wiring and use a
separate double-precision geometric reference for the affected cases.

Run the existing validation command:

```sh
python -B -m unittest discover -s Tools~/ShaderChecks -p 'test_*.py' -v
```

These checks do not execute HLSL, emulate GPU floating-point contractions,
validate Unity shader import, or measure rendering. The existing DXC
matrix compiles the actual forward, contact-depth, and motion shader
variants to DXIL/SPIR-V; licensed Unity execution remains separate.

## Native A/B qualification

Use identical Unity/URP versions, graphics backends, GPU, resolution,
camera lens and transforms, blade mesh, root seed, wind time, slope,
LOD configuration, density, lighting and exposure. Start with TAA
disabled, then check the same scene under actual MSAA and, separately,
temporal motion history if enabled.

Compare the baseline and updated shader on flat, sloped and strongly
bent surfaces with both perspective and orthographic cameras.
Inspect ordinary facing, near-end-on (including off-axis image corners),
exact parallel direction, strong wind and camera motion. Capture
silhouette-width, depth/contact and motion-pixel pairs with fixed
camera and wind for deterministic image comparisons. Inspect LOD
transitions and previous-frame motion reconstruction.

Profile after warmup at identical visible blade counts and screen
coverage: GPU vertex work, total grass pass, frame median/tail, and
per-frame allocations. A source-level normalization removal or a
corrected edge case is not a measured FPS or visual acceptance claim.

Native graphics/performance criteria remain open under #28, #33 and
#34. No unrelated native issue or interaction route is closed.
