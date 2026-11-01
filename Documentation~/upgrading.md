# Upgrading

This page covers moving to 2.0.0 from 1.x and from the 2.0 previews. Back up the project, or commit it, before you upgrade.

## Requirements

2.0.0 needs Unity 6000.6 and URP 17.6, and renders through RenderGraph only. Projects on older Unity or URP versions must upgrade those first. The Terrain, Terrain Physics, Physics and IMGUI modules are now package dependencies.

## Install the release

Change the package's git URL in Package Manager (or in `Packages/manifest.json`) to:

```
https://github.com/coen22/com.cone.grass.git#v2.0.0
```

## Namespace

All types are now in the `Cone.Grass` namespace; before, they were in the global namespace. Add the using directive to every script that uses the package:

```csharp
using Cone.Grass;
```

Editor types, such as `TerrainGrassAlbedoBaker` and `GrassRendererSetup`, are in `Cone.Grass.Editor`. The MicroVerse bridge is in `Cone.Grass.MicroVerse`.

Scenes, prefabs and renderer assets keep their references: Unity stores components by script, not by namespace.

## Assembly definitions

The assemblies are renamed. Update the **Assembly Definition References** of your own `.asmdef` files:

| Before | Now |
|---|---|
| `com.cone.grass` | `Cone.Grass` |
| `com.cone.grass.placement.editor` | `Cone.Grass.Editor` |
| `com.cone.grass.ground-color.editor` | `Cone.Grass.Editor` |
| `com.cone.grass.microverse.sample` | `Cone.Grass.MicroVerse` |
| `com.cone.grass.microverse.sample.editor` | `Cone.Grass.MicroVerse.Editor` |
| `com.cone.grass.microverse.sample.tests` | `Cone.Grass.MicroVerse.Tests` |

References by GUID keep working; references by name must be changed. If you imported the MicroVerse bridge sample before, delete the old copy and import it again.

## The old sample is removed

The original sample scene (a ball, effect materials and a terrain mesh) is gone. The **Meadow** sample replaces it. If you still use assets from the old sample, they stay in your `Assets/Samples` folder; they are no longer updated.

## Placement mode

New Infinite Grass Renderers default to **Authored Areas**: grass grows only inside Placement Areas.

A 1.x scene grew grass on every surface on the feature's Height Map Layer. To keep that behaviour, set **Placement Mode** to **Legacy Surface Layer** on its Infinite Grass Renderer. Mask, colour and push modifiers keep working in both modes.

To move a scene to authored areas:

1. Add Placement Areas where grass should grow, with their **Terrain** or **Paint Surface**.
2. Set **Placement Mode** to **Authored Areas**.
3. Set **Ground Layer** and raise **Ground Color Strength** on each area to match the ground.

## Renderer feature

The feature now fills in its compute shader and height material automatically, and the Infinite Grass Renderer inspector offers a button to add the feature. Existing renderer assets keep their settings.

Buffers now persist per camera. The old `ArgsBuffer` and debug `Buffer` fields are removed; use `VisibleGrassCount` and `OverflowGrassCount` with **Preview Visible Grass Count** on.

## Per-area kind weights (from 2.0 previews)

In earlier previews every Placement Area took part in the plant-kind weights. Now:

- Only areas with **Override Variant Weights** turned on affect the kinds.
- An area without an override does not take part. Inside it the profile's mix grows, except where it overlaps an area with an override, whose weights then apply. (Before, such an area counted as weight 1 for every kind and undid a lawn's zeros where they overlapped.)
- Where overrides overlap, each kind takes the larger weight.
- An area whose weights are all 0 grows only the first kind. (Before, it grew the profile's mix.)

Check areas that were meant to reweight kinds, and turn on **Override Variant Weights** on them.

## Interaction

`GrassInteractor` still works with its fields (**Body**, **Ground Layers**, **Radius Padding**, **Blade Height**, **Strength**, **Attack Seconds**, **Recovery Seconds**). New scenes should use **Grass Collider Interactor**. Interaction now needs a supporting surface: the Placement Area's Terrain (with a Terrain Collider) or Paint Surface, or an **Explicit Support** for legacy surfaces. See [Interaction](interaction.md#support-which-ground-counts).

## Custom shaders

- Custom blade shaders need a `GrassForward` pass and `_GrassInstanceOffset`. Contact shadows also need a `GrassContactDepth` pass, and motion vectors a `MotionVectors` pass.
- `_GrassPositions.w` stores the blade's **coverage**, not its camera distance. Compute the distance from the world position. With a variant profile, read it with `GrassUnpackCoverage` and `GrassUnpackKind` from `GrassVariants.hlsl`.
- Custom capture shaders should use `_GrassCaptureVP`, and put their capture `LightMode` tag on the pass itself. Expose sampled textures as material properties so the feature can track them.
- `GetGrassMeshCache()` returns the near mesh; `GetLodMeshes()` returns all three.
