# Infinite Grass manual

Infinite Grass draws dense procedural grass and meadow flowers on URP terrain. The GPU decides where every blade grows, each frame, around the camera. Nothing is baked into the scene: no GameObjects per blade, no detail meshes, no terrain detail layers.

You decide *where* grass grows with Placement Areas and painted density maps. You decide *what* grows with the blade material and, optionally, a mix of plant kinds with flower heads. Characters bend the grass as they walk through it, and it recovers over time.

## Features

- GPU generation in a compute shader, drawn with indirect instanced draws through a URP RenderGraph renderer feature.
- Placement Areas (circle, box or texture) on Terrain or mesh surfaces, with a Scene-view density brush and Undo.
- Up to eight plant kinds per renderer: grasses, herbs and flowers with procedural heads, clumped into drifts.
- Three geometry LODs and a distance density fade.
- Ground colour matching: blade roots take the colour of the terrain beneath them.
- Collider-driven bending with timed recovery, published as a shared field other effects can read.
- Optional screen-space contact shadows and optional per-blade motion vectors.
- Optional MicroVerse mask bridge and a terrain ground-albedo baker.

## Requirements

| Requirement | Version or capability |
|---|---|
| Unity | 6000.6 |
| Universal Render Pipeline | 17.6, with RenderGraph (the default in Unity 6) |
| Renderer | Universal Renderer (not the 2D Renderer); Forward and Forward+ (Deferred is untested) |
| GPU | Compute shaders, GPU instancing and indirect argument buffers |
| Graphics APIs | Desktop DirectX 11, DirectX 12, Vulkan and Metal are the intended targets |

Tested on Direct3D 12 on Windows (Unity 6000.6.7f1). The other desktop APIs are expected to work because the package uses only standard compute and indirect-draw features, but they have not been tested for this release. Mobile, WebGL and consoles are not supported targets.

The package depends on the Terrain, Terrain Physics, Physics and IMGUI built-in modules; Package Manager enables them for you.

### Cameras

Grass draws for Game view cameras and, when **Render In Scene View** is on, for the Scene view. A camera draws grass only when it is a URP **Base** camera and its culling mask includes the layer of the GameObject that holds the Infinite Grass Renderer. Overlay cameras, reflection and preview cameras, and XR (stereo) cameras do not draw grass.

## Contents

1. [Getting started](getting-started.md): install, set up a scene, import the sample.
2. [Concepts](concepts.md): how the renderer, the captures and the generation fit together.
3. [Reference](reference.md): every component, asset and material setting with its default.
4. [Plant kinds and flowers](plant-kinds.md): mixing grasses, herbs and flowers.
5. [Interaction](interaction.md): bending grass with colliders.
6. [Performance](performance.md): budgets and what each setting costs.
7. [Troubleshooting](troubleshooting.md): common problems and their fixes.
8. [Integrations](integrations.md): MicroVerse, the terrain albedo baker and the shared interaction field.
9. [Upgrading](upgrading.md): moving from 1.x or a 2.0 preview.
10. [Changelog](../CHANGELOG.md): what changed in each release.

## Known limits

- One Infinite Grass Renderer is active per scene set. A second enabled one disables itself with a warning.
- Ground colour and interaction are captured from above, in world X and Z. Two surfaces stacked vertically (a bridge over a lawn) cannot hold different ground colours or different bend fields at the same X/Z position.
- Grass receives main-light shadows but does not cast into the shadow map. Contact shadows are a screen-space approximation.
- Blades can receive additional lights only with Forward+ and the material's **Forward+ Additional Lights** option. Flower heads are lit by the main light and ambient light only.
- GPU Resident Drawer does not apply to these custom indirect draws.
