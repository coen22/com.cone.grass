# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses [Semantic Versioning](https://semver.org/).

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
- An inspector button that adds the Grass Data Renderer Feature to the active URP renderer.
- Optional MicroVerse mask bridge sample.
- EditMode and PlayMode test suites.

### Changed
- Requires Unity 6000.6 and URP 17.6; renders through RenderGraph only.
- All types are in the `Cone.Grass` namespace and the assemblies are named `Cone.Grass`, `Cone.Grass.Editor` and so
  on. Add `using Cone.Grass;` to scripts that use the package, and update assembly definition references from
  `com.cone.grass` to `Cone.Grass`.
- New renderers default to **Authored Areas** placement. Scenes from 1.x keep working in **Legacy Surface Layer** mode.
- GPU buffers persist per camera and are rebuilt only when capacity changes; all compute dispatches are bounded.

### Removed
- The original ball-and-effects demo scene, replaced by the Meadow sample.

## [1.0.0] - 2025-06-15

- First package release of Infinite Grass (originally by Youssef Afella) as a Unity Package Manager package for URP 17
  with a RenderGraph renderer feature.
