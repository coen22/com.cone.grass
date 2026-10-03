# Validation for the Unity 6.6 grass preview

This document separates implemented source changes from results that require a Unity project and GPU. Keep issues #27–#34 open until their acceptance criteria have been exercised.

## Current evidence

| Check | Status |
|---|---|
| Repository source and shader contracts reviewed | Performed |
| Package and assembly-definition JSON | Checked locally |
| New source metadata and whitespace | Checked locally |
| Density, grid boundaries, Terrain decoding, LOD geometry, premultiplied edge math, projection/depth round trips | Mathematical/source checks performed |
| Core EditMode test cases | 15 authored; not executed |
| Optional MicroVerse bridge EditMode test cases | 6 authored; not executed |
| Unity 6.6 shader/C# import | Not run |
| Unity 6.6 standalone build | Not run |
| Unity 6.7 import/build | Not run |
| GPU timings, allocations in Unity, RenderGraph viewer, rendered comparisons | Not run |
| Installed MicroVerse Core/Splines/Masks generation cycle | Not run |

The implementation environment has no Unity Editor, .NET compiler, DXC, or glslang. Source checks are not compilation or GPU validation. Some shader helper definitions were checked against Unity's public Graphics source; that checkout was not a locally installed, pinned URP 17.6 package.

## Reproducible test setup

1. Create a clean Unity 6.6 project with URP 17.6 and record the complete editor/package versions and graphics API.
2. Install the implementation branch. Ensure RenderGraph is enabled.
3. Install Unity Test Framework through Package Manager. Add `"com.cone.grass"` to the project's `testables` array if package tests are not discovered.
4. Run the core EditMode tests. Import the MicroVerse Mask Bridge sample to discover its six additional tests. These binding tests use a small synthetic saved-output asset and do not require proprietary MicroVerse assemblies.
5. Run a clean player build after saving the scene and any generated density bindings.

Example batch invocation, using the actual Unity executable and project path:

```sh
Unity -batchmode -projectPath /path/to/project -runTests -testPlatform EditMode -testResults /path/to/results/editmode.xml -logFile /path/to/results/editmode.log
```

Run rendering tests on a machine with the target graphics API and GPU available. Do not infer graphics support from a headless import alone.

## Scene and camera matrix

| Dimension | Required cases |
|---|---|
| Pipeline | URP Forward and Forward+ |
| Camera | Game and Scene views; perspective and orthographic |
| Rendering | MSAA off, 2x, 4x, 8x where supported; render scale below/at/above one |
| Camera movement | Stationary rotation, slow pan, crossing capture snaps, teleport, negative X/Z, steep pitch |
| Lifetime | Enable/disable renderer and feature, change capacity/LOD quality, remove a camera, change active renderer asset |
| Editor | Domain reload on/off, scene reload on/off, opening Prefab Stage, multiple Scene views |
| Platform | Each intended D3D11, D3D12, Vulkan, Metal target |
| Lighting | Main light absent, one cascade, multiple cascades, hard/soft shadows, screen-space shadow feature |

Overlay, reflection, preview, and XR cameras are deliberately excluded by the current renderer. Deferred and alternative content/build pipelines require separate validation before being advertised.

## Placement and captures

- New empty painted asset: no grass. Fill, paint, erase, Undo, and Redo restore the expected world coverage.
- Circle/Box: move, rotate around Y, apply nonuniform X/Z scale, and soften edges. Verify all four map corners and that a small spot stays small when assigned a Terrain.
- External map: linear red-channel density, unreadable texture, missing texture, wrong texture dimension, replaced asset, and resolution change.
- Terrain: changed height, holes, different origin/size, negative coordinates, adjacent borders/corners, and multiple tiles.
- Mesh surface: assigned paint surface, legacy height layer, partial vertex-color red, missing capture material, and a moved surface followed by Refresh Grass Data.
- Rotate a stationary camera to reveal previously off-screen parts of the same capture window.
- Moving color, mask, and slope modifiers work while the camera is stationary. After spawning new modifier renderers or replacing an inventory entry, refresh cached grass data.
- Check data-map orientation on each graphics API. World +X and +Z must move in the same direction in generation, density, color, slope, and ground capture.
- Inspect a top-down height map at several world altitudes. Stored R is world Y; G is validity. Camera altitude must not change the decoded world height.

The projection models one captured mesh height at each XZ position. Stacked mesh layers and arbitrary vertically overlapping terrain/mesh authoring need dedicated validation.

## RenderGraph and GPU safety

- Position and argument resources are imported once per camera graph; capture inputs and draw/compute dependencies are visible in the graph.
- Verify no capture read/write hazard, unbound compute texture, undeclared material texture, or indirect argument validation warning.
- Verify integer grid, source flags, LOD offsets, and cascade count are uploaded as integers.
- Test zero dispatches after previously drawing grass: counts and all three indirect instance counts become zero.
- Force a small buffer capacity. Writes remain in bounds; counts are clamped and overflow is reported asynchronously.
- A source/camera grid above the 67,108,864-candidate budget (64 × 1024²) must skip generation and issue the explicit warning. It must not silently choose a moving subset or wrap counters.
- Confirm argument layout/stride on every target API. The CPU probes the platform-provided typed layout instead of assuming the instance-count byte offset.
- Confirm no repeated large GPU allocations at steady state. GPU buffers may retain peak capacity until their camera resources are released.
- Confirm normal camera matrices remain unchanged after top-down capture passes.

## Blending and aliasing comparisons

Use the same scene, camera path, wind animation, resolution, and population settings for each comparison. Save short camera pans as well as still images.

- Match a single TerrainLayer at several tile sizes, offsets, and diffuse remap settings.
- Test a soft MicroVerse spline edge over a contrasting neighboring terrain layer. Use an explicit blended ground-albedo texture when a single layer cannot represent the terrain mixture.
- Inspect root normals on slopes with Use Ground Normal off/on.
- Verify filtered color, slope, and ground map boundaries do not acquire dark fringes or a default bend.
- Compare minimum width zero/one pixel with specular fade disabled/enabled.
- Compare MSAA off/4x with Alpha to Coverage disabled/enabled. A2C must stay off on single-sample targets.
- Check density and both geometry LOD boundaries during slow and fast movement. Near/middle/far should use different indirect mesh draws.
- Check very distant grazing angles and blade tips. A deterministic fallback can still show a pattern; it is not a substitute for sufficient pixel coverage.
- Do not mark TAA support complete: previous-frame deformation and indirect motion vectors are still absent.

## Contact-shadow comparisons

Check contacts enabled/disabled at the same exposure and lighting:

- Grass over flat and sloped terrain.
- An opaque object touching grass, grass beside an object, and isolated scene objects away from grass.
- Main light at high and low elevations, near/far distance fade, orthographic projection, reversed/normal Z.
- MSAA silhouettes and transparent objects rendered after the contact pass.
- Render scale changes, screen boundaries, and objects entering/leaving the frame.

The overlay must leave unrelated scene-to-scene shading unchanged and preserve color alpha/MSAA samples. Increasing bias/thickness/length must have bounded, explainable effects. It has one nearest grass depth layer and no off-screen data; fractional silhouettes remain approximate. Its color multiplication attenuates all existing lighting, including ambient and emissive contributions.

## MicroVerse project acceptance

Use the actual installed versions of MicroVerse Core, Splines, and Masks:

1. One Spline Area drives a Texture Stamp and positive Mask Stamp with the same intended filters and falloff.
2. Bind each affected Terrain to the explicit corresponding saved texture name.
3. Edit spline shape, change mask resolution, replace output subassets, undo an edit, and cancel a generation.
4. Verify optional polling eventually refreshes completed saved results and never leaves old coverage after a missing/ambiguous output.
5. Refresh and save, close/reopen the scene, then make a clean standalone build.

The bridge does not know the native generation transaction state. Verified completion/cancellation callbacks are follow-up work. Unity may reuse cached scene build data without invoking the scene processor; refresh and save first. Prefab/addressable or other content builders must validate their own binding inputs.

## Performance capture

Record GPU/CPU frame times, grass population, draw calls, primitive counts, native/managed allocations, capture texture memory, position-buffer memory, and overflow. Include:

- Dense, sparse painted, and MicroVerse multi-terrain scenes.
- Static and continuously moving cameras.
- Static and moving modifiers.
- Contacts off/on with 4, 8, 16, and 32 steps.
- Main/SH lighting and optional Forward+ additional-light variants.

At five near subdivisions, geometry is 13/7/3 vertices and 11/5/1 triangles for near/middle/far. That reduction can be verified in the frame debugger. It does not establish a net GPU timing improvement: capture resolution, fragment lighting, map sampling, overdraw, contact passes, and visible density also affect cost.

Keep the issue checklists open until these results, including exact Unity/URP/MicroVerse versions and target GPUs, are recorded.
