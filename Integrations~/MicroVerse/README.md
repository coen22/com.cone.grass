# MicroVerse mask bridge

This optional sample connects MicroVerse's **saved MaskTarget textures** to Infinite Grass Placement Areas. One Spline Area can then drive both the ground texture and the grass. The sample contains no MicroVerse code and has no compile-time dependency on MicroVerse assemblies.

The bridge reads saved mask outputs through Unity's asset APIs. It does not hook into MicroVerse's generation events, so it cannot tell when a generation is still running (see [Editing and refresh](#editing-and-refresh)).

## Install

Import **MicroVerse Mask Bridge** from the **Samples** tab of **Infinite Grass** in Package Manager. Unity does not compile the sample while it stays inside the package.

It contains three assemblies, all in the `Cone.Grass.MicroVerse` namespace:

| Assembly | Contents |
|---|---|
| `Cone.Grass.MicroVerse` | The `GrassMicroVerseBridge` component. |
| `Cone.Grass.MicroVerse.Editor` | Inspector, refresh watcher and build checks. |
| `Cone.Grass.MicroVerse.Tests` | EditMode tests (need the Unity Test Framework). |

Keep both assembly definitions and the `Editor` folder.

The authoring workflow needs MicroVerse Core, Splines and Masks, and Unity Splines. The MicroVerse Vegetation module is not needed. Install them through their normal channels.

## One spline for ground texture and grass

1. Create a closed Unity spline around a grass patch and add MicroVerse's **Spline Area** component.
2. Add a MicroVerse **Texture Stamp** beneath the area. Select the grass `TerrainLayer`, choose **Spline Area** falloff, and assign the area.
3. Create a **MaskTarget** asset for grass density. Use **R8**, clear to black, at a resolution fine enough for the smallest grass edge you need. Linear RGBA also works if red holds the density. Do not use an SDF target.
4. Add a **Mask Stamp** that writes white to the red channel of that target within the same Spline Area. Match the Texture Stamp's falloff, filters and noise, so the grass and the ground texture cover the same region.
5. Let MicroVerse finish, then save the scene. Expand the MaskTarget in the Project window to see its per-terrain textures.
6. Add **Infinite Grass > MicroVerse Mask Bridge** to each affected Terrain GameObject, or to another GameObject with that Terrain assigned. The required Placement Area is added automatically.
7. Assign **Mask Target** and **Terrain**, then choose that terrain's texture in **Generated Density Texture**. The bridge never guesses from partial names or texture order.
8. Set **Grass Terrain Layer** to the TerrainLayer used by the Texture Stamp. For mixed grass and dirt edges, turn on **Bake Terrain Ground Albedo**, choose a unique output `.asset` for this terrain, and click **Bake Terrain Albedo and Refresh**.
9. Make sure the Infinite Grass Renderer uses **Authored Areas**, save the scene, and check the edge in the Scene and Game views.

Give every terrain and mask a unique name. A spline that crosses terrains needs one bridge per terrain, each with that terrain's own texture.

## Density and colour

| Input | Meaning |
| --- | --- |
| Density red = 0 | No grass |
| Density red = 1 | Full density, before the distance fade |
| Density red between 0 and 1 | Grass thins smoothly |
| Grass Terrain Layer | Ground colour from that layer's diffuse texture, with the terrain's tiling and remap |
| Bake Terrain Ground Albedo | Bakes the blend of all terrain layers into a terrain-aligned map |
| Ground Color Override | A terrain-aligned colour map, used when baking is off. Takes precedence over the layer. |
| Ground Tint, Ground Color Strength | The ground colour's tint and amount |

Density must be linear data in a normalised or floating-point red channel. The bridge rejects sRGB density textures, alpha-only formats (such as Alpha8), and integer and depth formats. Compressed formats with a linear red channel work. A missing, unsupported or ambiguous texture grows no grass until fixed.

The baker supports only the native `Universal Render Pipeline/Terrain/Lit` material. It produces linear RGBAHalf with mipmaps and updates an existing output asset in place, keeping its GUID. Use one output per terrain; the default resolution is 1024. For custom terrain materials, Shader Graph terrains or MicroSplat, turn off **Bake Terrain Ground Albedo** and assign that material's own final-albedo map as **Ground Color Override**. Unsupported materials report a clear error.

The bridge owns its Placement Area's terrain, shape, density texture, edge falloff and ground colour bindings: change them on the bridge. The mask already holds the spline's falloff, so the area's **Edge Falloff** is set to 0. The area's other settings stay yours.

## Editing and refresh

With **Auto Refresh** on, saved asset changes, scene opening and saving, inspector edits, terrain texture changes and Undo/Redo schedule a refresh after a short delay. **Poll Saved Outputs** also checks the saved textures every **Poll Interval** (1 s by default). An unchanged poll does no texture reads and no grass capture work, and polling has no cost in the player.

Because the bridge cannot see MicroVerse's generation state, coverage can lag by the refresh delay or show an intermediate output during editing. When generation finishes, click **Refresh Saved Mask**. **Bake Terrain Albedo and Refresh** also forces a new ground bake. Both keep the cached terrain height.

Changing mask format or resolution can replace the generated texture. The bridge finds it again by its unique full name. If the name changes, select the new texture. If two textures share a name, rename one; the bridge never picks for you.

Changing the bridge's Terrain or Mask Target clears the selected texture. Changing the Terrain also clears the bake path, so one tile cannot overwrite another's bake. Disabling the bridge stops automatic updates; the Placement Area keeps the last resolved inputs. Disable the Placement Area to hide its grass.

A bake made from unsaved source changes previews in the editor but is marked not ready for a build. Save the sources and the bridge bakes once more. Bridges that share one terrain's output must use the same resolution.

## Saving and player builds

Before building: let MicroVerse finish, save its assets, refresh the bridge, bake if required, and save the scene. The Placement Area stores the resolved texture and terrain in the scene, so the player needs no MicroVerse lookup. Enabled bridges refresh before the scene is saved.

Every player build, clean or incremental, checks the saved scenes first. It fails with the scene, the object and the fix if a binding or a bake is stale. It never saves open scenes or repairs a build copy. When you use a Grass Terrain Layer without a baked or override map, save the layer's diffuse texture as well as the TerrainLayer asset.

The build removes the bridge component from the built scene; the Placement Area stays. It never generates terrain or bakes during a build. Custom content builds can call `GrassMicroVerseBridgeBuildProcessor.ValidateSavedScenes(scenePaths)` before building their scenes. Prefabs and Addressables outside that scene workflow need their placement inputs prepared and saved by their own pipeline.

## Tests

With the Unity Test Framework installed, run `GrassMicroVerseBridgeTests` and `GrassMicroVerseGroundBakeTests` in the Test Runner (EditMode). They use synthetic saved textures, so they need no MicroVerse install. The ground-bake tests need a graphics device and URP.

## Checking a setup

- Start with a black mask and confirm no grass grows. Add one spline area and check that grass appears only inside its white and grey coverage.
- Compare the Texture Stamp and Mask Stamp falloff and filters, especially on slopes and at the edge.
- Move, resize and delete the spline, let generation finish, then refresh. Grass should follow, including through Undo and Redo.
- For a spline across two terrains, check that each bridge points at its own terrain's texture.
- Make a build and compare the grass, ground colour and edges with the editor.

## References

- [MicroVerse main manual](https://docs.google.com/document/d/1R4Ru7GKdVLLNVmfVwcX7RPRPrvkN0l36LNqm9LIMtno/edit): Spline Areas, Texture Stamps.
- [MicroVerse Masks manual](https://docs.google.com/document/d/1mFY_H88cyvHL8rtae7f0fWhdW7o7oabjzE7G0Cnuw9w/edit): per-terrain generated textures, formats and replacement.
