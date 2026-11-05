# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **Grass in planar reflections.** Reflection cameras such as Crest's water reflections draw the grass through a
  cheaper path: `InfiniteGrassRenderer.Reflections` sets which reflection cameras (Planar by default; reflection-probe
  captures are left out), a spacing multiplier (2, a quarter of the blades) and a draw distance multiplier (0.5).
  Reflections capture no interactor bend and skip contact shadows, motion vectors and diagnostics (#98).
- **Draw Order.** `InfiniteGrassRenderer.DrawOrder = BeforeOpaqueCopies` draws the grass after the opaques and before
  URP's depth and opaque copies, so water refraction and Crest's underwater effect include it. Contact shadows and
  grass motion vectors are skipped in that order (#98).
- **Water line.** `InfiniteGrassRenderer.WaterLine` keeps grass and flowers above a world-space water level: no root
  grows at or below it, and density thins in over a **Shore Fade** band above it (0.5 m by default), so the grass
  doesn't stop in a hard line. Off by default; placement is unchanged until it is enabled or given a level source
  (`IGrassWaterLevelSource`).
- **Crest 5 water level.** `Cone.Grass.Crest.GrassCrestWaterLevel` drives the water line from Crest's sea level or
  its queried surface height. The `Cone.Grass.Crest` assembly compiles only when `com.waveharmonic.crest` is installed.

### Changed
- Planar reflection cameras (`CameraType.Reflection`) now draw grass by default; set Reflections > Cameras to None
  for the previous behaviour.

## [2.0.0] - 2026-11-01

The first stable release of the 2.x line, for Unity 6000.6 and URP 17.6 with RenderGraph.

### Added
- **Plant kinds.** A Grass Variant Profile mixes up to eight kinds of blade, herb and flower per renderer, with
  weights, a seed, clumping into drifts, moisture and slope habitats, per-kind tint and colour variation. Flowers and
  herbs carry heads (daisy, cup, umbel, spike, puff, clover or your own mesh) that bend with their blade, fade out with
  distance and appear in contact shadows and motion vectors. Placement Areas can reweight the kinds.
- **Meadow sample**: a terrain meadow with flowers at grass height and a character whose collider bends the grass.
- **Authored placement.** Placement Areas (circle, box or texture) on Terrain or mesh surfaces, painted density assets
  with a Scene-view brush and Undo, and terrain ground-colour matching (including a baker for URP Terrain/Lit blends).
- **Collider interaction.** Grass Collider Interactor bends grass under a CharacterController, capsule or sphere
  collider and lets it recover over time; the interaction capture is also published as a shared field for other effects.
- **Geometric LOD** with near, middle and far blade meshes, distance density fade and per-LOD GPU queues.
- **Optional contact shadows** between grass and the visible scene, and optional per-blade motion vectors.
- An inspector button that adds the Grass Data Renderer Feature to the active URP renderer, and default assets for
  the feature that are filled in before every player build.
- Optional MicroVerse mask bridge sample.
- EditMode and PlayMode test suites.

### Changed
- Requires Unity 6000.6 and URP 17.6; renders through RenderGraph only.
- All types are in the `Cone.Grass` namespace and the assemblies are named `Cone.Grass`, `Cone.Grass.Editor` and so
  on. Add `using Cone.Grass;` to scripts that use the package, and update assembly definition references from
  `com.cone.grass` to `Cone.Grass`.
- Settings that were public fields are now PascalCase properties (`renderer.spacing` is now `renderer.Spacing`).
  Serialized data is kept; editor `FindProperty` paths and animation curves on old field names need updating.
- Implementation types such as `GrassVariantSelection`, `GrassInteractionField` and `GrassDispatchMath` are internal.
- New renderers default to **Authored Areas** placement. A 1.x scene loads in that mode too and shows no grass until
  you set **Placement Mode** to **Legacy Surface Layer** or add Placement Areas (see the upgrade guide).
- GPU buffers persist per camera and are rebuilt only when capacity changes; all compute dispatches are bounded.

### Fixed
- A Grass Collider Interactor or Grass Interactor in a saved scene bends grass in the Editor; an empty capture
  shader or Body field no longer stops it.
- Capture passes no longer declare textures a capture material kept from a previous shader.

### Removed
- The original ball-and-effects demo scene, replaced by the Meadow sample.
- The opt-in batch test reload guard.

## [1.0.0] - 2025-06-15

- First package release of Infinite Grass (originally by Youssef Afella) as a Unity Package Manager package for URP 17
  with a RenderGraph renderer feature.
