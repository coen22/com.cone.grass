# Performance

Grass cost scales with the number of blades drawn and the pixels they cover. This page lists the settings that matter, in rough order of impact, and what each one allocates. Always profile your own scene at your target resolution: the GPU Profiler and the Frame Debugger show the grass passes by name.

## Blade count

The number of candidate roots is about `π × Draw Distance² / Spacing²`, thinned by density and the distance fade. Most of the cost sits within **Full Density Distance**, where every root grows.

| Setting | Effect on cost |
|---|---|
| Spacing | The strongest lever. Doubling it gives a quarter of the blades. Make blades wider (**Grass Width**) to keep the look. |
| Full Density Distance | Everything inside it is full density. Keep it as short as the view allows. |
| Density Falloff Exponent | Higher thins the far grass sooner. |
| Draw Distance | Sets the outer edge, and the size of the capture window. |
| Placement Areas | Grass grows only inside them. Unused ground costs nothing. |

Turn on **Preview Visible Grass Count** to see how many blades are drawn.

## Capacity

**Max Buffer Count** (default 2 million) is the total over the three LOD queues, split by **Lod Capacity Weights** (0.35, 0.45, 0.2). When a queue fills, extra roots are dropped and counted as overflow. Dropped roots change from frame to frame, which can flicker.

- Check **Preview Visible Grass Count**: "over capacity" should be 0.
- If it is not, raise Max Buffer Count, move weight to the overflowing queue, or reduce density.
- Do not set capacity far above what you draw: the buffer is allocated in full.

Each blade takes 16 bytes. Head queues add **Max Head Count** (default 0.25 million) each, when a kind uses them.

| Allocation per camera | Size |
|---|---|
| Positions at the default 2 million blades | 32 MB (30.5 MiB) |
| One head queue at the default 0.25 million | 4 MB |

## Geometry LOD

| Setting | Effect |
|---|---|
| Grass Mesh Subdivision | Segments of the near mesh (default 5). Lower is cheaper near the camera; bends look more angular. |
| Near Lod Distance | Beyond it, blades use the 2-segment mesh. |
| Subdivision Distance | Beyond it, blades use the single-segment mesh. |

Pull both distances in if vertex cost dominates. **Subdivision Height Boost** can hide the step to the far mesh.

## Capture resolution

The captures are redrawn when the camera crosses a **Texture Update Threshold** grid line or a source changes, not every frame. They cost memory per camera:

| Capture Resolution | Capture textures per camera |
|---|---|
| 512 | 8.5 MiB |
| 1024 (default) | 34 MiB |
| 2048 | 136 MiB |

These are the seven capture textures (height, capture depth, density, exclusion, colour, interaction, ground colour: 34 bytes per pixel). Plant-kind weights add two small maps at a quarter of the resolution when a profile mixes kinds. Each supporting terrain's density map is extra.

Lower resolution blurs small density edges, ground colour and interaction trails (one pixel is about 0.6 m at 1024 with the default distances). A shorter **Draw Distance** sharpens them at no extra memory.

## Pixel cost

| Setting | Effect |
|---|---|
| Minimum Pixel Width | Widens thin far blades to this many pixels. Higher is more stable and covers more pixels. |
| Expand Distant Grass Width | Wider far blades cover more pixels with fewer blades. |
| Specular Fade Range | No visual cost beyond it, and less sparkle. |
| Material options | **Match Ground Normal at Roots**, **Terrain Splat Colour And Detail At Roots**, **Canopy Occlusion** and **Forward+ Additional Lights** each add shading work. Turn on only what the scene needs. |

Blades draw both sides, so bent blades seen from behind still shade.

## Contact shadows

Off by default. When on, they add a grass depth pass and a full-screen contact pass. **Steps** sets the cost of the contact pass; **Max Distance** limits where it runs.

## Motion vectors

Off by default. Use **Auto** only with TAA or motion blur. History is allocated per camera only while motion is requested. For a capacity of `C` blades, it uses `32 × C + 4 × NextPowerOfTwo(2 × C)` bytes:

| Max Buffer Count | Motion history per camera |
|---|---|
| 2 million | 77 MiB |
| 16 million | 616 MiB |

Wind and interaction snapshots add a few more megabytes at the default sizes.

## Cameras

Each camera that draws grass has its own captures, buffers and generation. A second camera doubles the cost. Utility cameras that should not draw grass need only exclude the Infinite Grass Renderer's layer from their culling mask: they then cost nothing.

## Measured

<!-- MEASURED-NUMBERS -->
