# Meadow sample

A terrain meadow: a short, lush lawn with small flowers standing at the height of the grass tips, and a character whose collider bends the grass as it walks. Use it to see the package working and as a starting point for your own scenes.

## Open it

1. Import **Meadow** from the package's **Samples** tab in Package Manager. It lands in `Assets/Samples/Infinite Grass/2.0.0/Meadow`.
2. Open the Meadow scene in that folder and press **Play**.

**No grass?** Select **Meadow Grass** in the Hierarchy and click **Add Grass Data Renderer Feature** in its inspector. The grass draws only through that renderer feature, and a fresh project's URP renderer does not have it yet. If the button does not appear, check that a URP asset is active in **Project Settings > Graphics**.

## Controls

| Input | Action |
|---|---|
| W A S D or arrow keys | Walk, relative to the camera |
| Left Shift | Run |
| Right mouse button + move | Orbit the camera |
| Gamepad sticks | Walk and orbit (with the Input System package) |

Leave the keyboard alone for a few seconds and the character walks a figure of eight on its own, so you can watch the trail form and recover. The sample works with the Input System package, the legacy Input Manager, or both.

## What to look at

**Meadow Grass** (Infinite Grass Renderer)

- **Grass Material** and **Spacing** set the lawn's blade and density.
- **Plant Kinds > Variants** is the **Meadow Plant Kinds** profile. Open it to see the kinds: lawn grass, a darker undercoat, a broad herb, clover, and three flowers (puff, daisy, buttercup). The flowers share the lawn's height, so their heads sit at the tips of the grass; they are about 6 cm across and sparse.
- Turn on **Preview Visible Grass Count** to see how many blades are drawn.

**The Placement Area** on Meadow Grass covers the terrain with a white coverage map; it decides where grass grows and matches the root colour to the terrain through **Ground Layer** and **Ground Color Strength**.

**The character** has a CharacterController, a **Grass Collider Interactor** that bends the grass with that collider, and **Meadow Walker** (movement and autopilot). Try **Bend Strength** and **Recovery Duration** in Play mode.

**The camera** has **Meadow Camera**, a third-person orbit camera that swings back behind the character when you stop orbiting.

## Reuse it

- **The plant kinds.** Assign the Meadow Plant Kinds profile to your own renderer, or duplicate it and change the palettes and weights. A new Variant Profile starts from the same preset.
- **The interactor.** Add **Cone > Grass > Collider Interactor** next to your own character's CharacterController, and compare its settings with the sample character's.
- **The scripts.** `MeadowWalker`, `MeadowCamera` and `MeadowInput` are in the `Cone.Grass.Samples.Meadow` assembly and namespace. They are sample code: copy them into your project if you want to change them, rather than editing the imported sample.

See the [manual](https://github.com/coen22/com.cone.grass/blob/v2.0.0/Documentation~/index.md) for every setting.
