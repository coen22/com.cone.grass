# Getting started

This page takes a clean URP project to a terrain with grass, then shows you the Meadow sample. Allow about ten minutes.

## 1. Install the package

1. Open **Window > Package Manager**.
2. Click **+** and choose **Install package from git URL...**.
3. Enter:

   ```
   https://github.com/coen22/com.cone.grass.git#v2.0.0
   ```

Package Manager installs **Infinite Grass** and its dependencies, including URP 17.6.

The project must use URP. In **Project Settings > Graphics**, the **Default Render Pipeline** must be a URP asset. If you also set render pipelines per quality level, check those too: the grass uses whichever pipeline is active.

## 2. Add the renderer feature

The grass draws only through the **Grass Data Renderer Feature** on your URP renderer. There are three ways to add it:

- **The inspector button.** Add an Infinite Grass Renderer (step 3). Its inspector warns that the feature is missing and shows **Add Grass Data Renderer Feature**. Click it.
- **The menu.** Choose **Tools > Cone > Grass > Add Renderer Feature to Active Renderer**. The item is greyed out when URP is not active or the feature is already installed.
- **By hand.** Select your Universal Renderer Data asset, click **Add Renderer Feature** and choose **Grass Data Renderer Feature**.

The button and the menu add the feature to the *default* renderer of the active URP asset, or enable it if it is there but turned off, and save the renderer asset. If your cameras use a different renderer from the list, add the feature to that renderer by hand.

A new feature fills in its **Compute Shader** (`GrassPositionsCompute`) and **Height Map Mat** (`GrassHeightMapMat`) from the package automatically. You do not need to assign them.

## 3. Add the Infinite Grass Renderer

1. Create an empty GameObject, for example `Grass`.
2. Add the **Infinite Grass Renderer** component.
3. Assign **Grass Material**. Use the package's **Grass Blade** material (`Packages/com.cone.grass/Runtime/Materials/Grass Blade.mat`; in the Project window, **Packages > Infinite Grass > Runtime > Materials**), or better, a copy of it in your `Assets` folder so you can edit it.

Leave **Placement Mode** at **Authored Areas**. In this mode grass grows only inside Placement Areas.

Keep the GameObject on a layer that your camera renders (the Default layer is fine). Cameras skip the grass when their culling mask excludes the renderer's layer.

## 4. Add a Placement Area on a Terrain

1. Create a Terrain, or use an existing one. It needs a **Terrain Collider** if you want the Scene brush or character interaction to follow its surface; new terrains have one.
2. Create another empty GameObject, for example `Lawn`, and add **Add Component > Cone > Grass > Placement Area**.
3. Assign **Terrain** to your terrain.
4. Choose **Shape**: **Circle** or **Box**. Set **Size** (metres, before the object's X/Z scale) and move the object to where the grass should be. Rotation around Y is supported.
5. Leave **Density** at 1 and **Edge Falloff** at 0.15 for a soft edge.

Grass appears in the Scene view and the Game view. An area stays inside its own footprint even with a Terrain assigned. To cover the whole terrain, see [painting density](#paint-density-optional) below and **Use Terrain Bounds**.

The area samples the terrain's height and holes directly. You do not need terrain detail layers or a special terrain layer for authored areas.

## 5. Match the ground colour

Blade roots blend toward the ground colour so the grass sits in the terrain rather than on it.

1. On the Placement Area, set **Ground Layer** to the TerrainLayer painted under the grass.
2. Raise **Ground Color Strength** from 0 to 1.

The area reads the layer's diffuse texture with the terrain's own tiling and remapping. The Infinite Grass Renderer's **Ground Blend Strength** and **Ground Blend Height** control how much and how far up the blade the ground colour reaches.

If the ground under the grass blends several terrain layers, bake them into one map instead: see [the terrain ground-albedo baker](integrations.md#terrain-ground-albedo-baker).

## Paint density (optional)

1. On the Placement Area, under **Scene Brush**, click **Create paintable density map** and save the asset. The area switches to the **Texture** shape and the new map starts empty: no grass.
2. Click **Paint density in Scene view**, then drag with the left mouse button over the area. Hold **Shift** to erase, or choose **Erase** in the toolbar. **Alt** orbits the view.
3. Adjust **Radius (metres)**, **Strength** and **Hardness**. Every stroke supports Undo and Redo, and changes save with the asset.

For a map that covers a whole terrain, turn on **Use Terrain Bounds**: the map then spans the terrain's full X/Z extent and ignores the object's transform.

## 6. Import the Meadow sample

1. In Package Manager, select **Infinite Grass** and open the **Samples** tab.
2. Click **Import** next to **Meadow**.
3. Open the Meadow scene from `Assets/Samples/Infinite Grass/2.0.0/Meadow` and press Play.

The sample shows a terrain meadow with flowers at grass height and a character that bends the grass. **WASD** walks, **Shift** runs, and holding the **right mouse button** orbits the camera. Left alone, the character walks a figure of eight by itself.

If no grass shows, select the **Meadow Grass** object and click **Add Grass Data Renderer Feature** in its inspector. See the [sample's README](../Samples~/Meadow/README.md) for what to look at.

## Run the tests

The package ships EditMode and PlayMode tests. To run them in your project:

1. Open `Packages/manifest.json` and add the package to `testables`:

   ```json
   "testables": ["com.cone.grass"]
   ```

2. Open **Window > General > Test Runner**, choose **EditMode** or **PlayMode**, and click **Run All**.

The tests are in the assemblies `Cone.Grass.Tests.Editor` and `Cone.Grass.Tests.Runtime`. Rendering and compute tests need a graphics device and an active URP asset; run them in the Editor, not with `-nographics`.

## Next steps

- Add flowers and herbs: [Plant kinds](plant-kinds.md).
- Let a character bend the grass: [Interaction](interaction.md).
- Tune cost and quality: [Performance](performance.md).
- Nothing showing? [Troubleshooting](troubleshooting.md).
