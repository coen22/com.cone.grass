# MicroVerse grass mask bridge

This optional sample connects MicroVerse's **saved MaskTarget texture subassets** to `GrassPlacementArea`. A Spline Area can drive both the grass ground texture and the rendered blades. The sample has no dependency on MicroVerse assemblies or proprietary source code.

The implemented bridge uses Unity's asset APIs and explicit terrain mapping. Native MicroVerse completion and cancellation event integration still needs validation against the installed MicroVerse version. This sample does not claim version-specific native API compatibility.

## Install

Import **MicroVerse Mask Bridge** from this package's Samples section in Package Manager, or copy this entire directory into your project's `Assets/InfiniteGrass/Integrations/MicroVerse` folder. Keep both assembly definitions and the `Editor` directory. Unity does not compile this directory while it remains under the package's `Integrations~` path.

The authoring workflow requires MicroVerse Core, Splines, and Masks, plus Unity Splines. The MicroVerse Vegetation module is not required for this mask-based renderer. Install those products through their normal distribution channels; this sample includes none of their source or example assets.

## One spline for ground texture and blades

1. Create a closed Unity spline around a grass patch and add MicroVerse's **Spline Area** component.
2. Add a MicroVerse **Texture Stamp** beneath the area. Select the desired grass `TerrainLayer`, choose **Spline Area** falloff, and assign the area.
3. Create a **MaskTarget** asset for grass density. Use **R8**, clear to black, and select a resolution suitable for the smallest grass boundary you need. Linear RGBA also works if the red channel contains density. Do not use an SDF target for this input.
4. Add a **Mask Stamp**, point it at that target, and write white positive coverage to the red channel within the same Spline Area. Match the Texture Stamp's falloff, filters, and relevant noise settings so the visible grass and textured ground cover the same region.
5. Allow MicroVerse to finish, then save the scene. Expand the MaskTarget asset in the Project window to inspect its generated per-terrain textures.
6. Add **Infinite Grass > MicroVerse Mask Bridge** to each affected Terrain GameObject, or to a separate source GameObject with that Terrain explicitly assigned. The required `GrassPlacementArea` is added automatically.
7. Assign the MaskTarget and Terrain, then choose that terrain's output in **Generated Density Texture**. The mapping is deliberately explicit: the bridge never guesses from partial terrain names or texture order.
8. Assign the **same TerrainLayer** used by the Texture Stamp to **Grass Terrain Layer**, then click **Refresh Saved Mask**. The placement area gets the positive density map, terrain frame, and matching ground color inputs.
9. Set the grass renderer to authored placement, save the scene, and check the boundary in Scene and Game views.

Use a unique name for every terrain and mask. A spline crossing terrain boundaries needs one bridge per terrain and the matching output texture on each. Each terrain's own size and origin determine the mask coordinates; one tile's mask must not be stretched over its neighbors.

## Density and color

| Input | Meaning |
| --- | --- |
| Density red = 0 | No blades |
| Density red = 1 | Full authored density, before distance/quality reduction |
| Density red between 0 and 1 | Continuous thinning of stable world-space blade candidates |
| Grass Terrain Layer | Samples the same diffuse texture, terrain tiling/offset, and diffuse remap used by the selected layer |
| Ground Color Override | Optional color map covering this entire terrain; takes precedence over the layer diffuse texture |
| Ground Tint / Strength | Controls the color contribution sent to the grass renderer |

Density must be linear data. The bridge rejects sRGB density textures so a painted density of 0.5 is not silently changed by color conversion. Missing or ambiguous outputs produce zero grass until they are resolved.

The renderer's root-to-tip blend makes the blade bases inherit the ground color while retaining blade variation farther up. Set the grass material's ground blend strength and root blend height to match the scene. Ground coloring and contact shadows are separate controls: color establishes the match; the shadow effect adds local darkening around contact.

A single TerrainLayer's diffuse color does not represent a blend of several terrain layers. For a transition where dirt and grass mix, provide a saved, terrain-aligned color map of the blended ground in **Ground Color Override**. The override should contain albedo, without baked lighting or contact shadow darkening. This sample does not bake the full terrain material automatically. Material-specific effects such as procedural terrain tint, snow, or custom terrain shader blending also need a matching color map or a dedicated provider.

The bridge owns its placement area's terrain, texture shape, density texture, edge falloff, and ground color bindings. Set these through the bridge. The generated mask already contains the spline falloff, so the bridge sets the placement area's additional edge falloff to zero. Blade density multipliers and other independent appearance controls remain available on the core components.

## Editing and regeneration

With **Auto Refresh** enabled, changes to saved assets, scene opening/saving, inspector values, and Undo/Redo schedule a refresh after a short debounce. **Poll Saved Outputs** also refreshes once per second by default. It handles producer updates to an existing texture even when Unity receives no asset import event. The interval is adjustable, and polling can be disabled while keeping event-based refresh.

Automatic refresh is an editor convenience. It samples the current generated output and invalidates grass coverage. It does not know whether MicroVerse is currently generating, has completed, or has canceled an update. While dragging a spline, coverage may lag by the polling interval. After a completed edit, the next poll refreshes both the previous and current coverage because the placement input is invalidated as a whole. Use **Refresh Saved Mask** after generation for a deterministic manual refresh.

Changes to mask format or resolution can replace the generated texture subasset. The bridge resolves its selected **unique full texture name** again, so it can replace an obsolete reference when the replacement retains that name. If the name changes, select the new output explicitly. If multiple subassets have the same name, resolve the naming conflict before proceeding. The bridge never silently chooses one.

Changing the Terrain or MaskTarget in the inspector clears the selected output and requires a new explicit mapping. Disabling the bridge stops automatic authoring updates; the adjacent placement area retains the last resolved runtime inputs. Disable the placement area to stop drawing its grass.

## Saving and player builds

Finish MicroVerse generation, refresh the bridge, and save the scene before building. The runtime `GrassPlacementArea` serializes the resolved generated texture and Terrain references. MaskTarget references, polling, and asset lookups exist only in editor code.

The included scene build processor refreshes enabled bridges in each processed scene, fails with a specific error if an output cannot be resolved, and removes the authoring bridge component from the built scene copy. It consumes existing outputs and never triggers MicroVerse terrain generation. Disabled bridges keep their last resolved placement data.

Unity can reuse unchanged scene build data without invoking scene processors. Save refreshed scenes after output replacement, and use a clean build when validating a resolution/format migration. Prefabs or addressable content built outside the scene build pipeline must have their core placement inputs refreshed and saved before their own build process; the scene callback does not process every external content pipeline.

There is no MicroVerse lookup or polling in the player. Keep the generated texture assets as dependencies of the resolved placement components. The optional runtime marker contains no authoring fields in player compilation, even if an external content pipeline does not remove it.

## Validation in the target project

After importing the sample with Unity Test Framework available, its EditMode tests cover explicit tile selection, empty configuration, texture replacement, duplicate names, sRGB rejection, and TerrainLayer ownership. Run `GrassMicroVerseBridgeTests` in Test Runner. These tests exercise generic Unity subassets; they do not replace validation against the installed MicroVerse version.

- Start with a black mask and confirm no grass appears. Add one spline area and check that blades appear only within its white/gray coverage.
- Check the same spline falloff and filters on the Texture Stamp and Mask Stamp. Inspect both density preview and terrain texture weights, especially on slopes and near the edge.
- Move, resize, and delete the spline with a stationary camera. Let generation finish, then refresh. Confirm grass disappears from the previous region, appears in the current region, and follows Undo/Redo.
- Change mask resolution and format, regenerate, and refresh. Verify the resolved texture reference changes and the region remains correct.
- Test a spline crossing two differently positioned or sized terrains. Confirm each bridge points to that tile's generated texture.
- Check the transition with the selected TerrainLayer and then with a blended ground color override if several terrain textures meet there.
- Make a clean standalone build and confirm coverage, ground tint, and contact shadow settings match the editor. Confirm the build contains the resolved textures and core placement components.

## Native integration follow-up

Before adding a version-specific MicroVerse adapter, inspect the installed implementation and verify:

- The supported MaskTarget getter and its mapping to a specific Terrain.
- Completion and cancellation event signatures and update ordering.
- How generated texture replacement is reported.
- Scene/build callbacks and any stripping of authoring objects.
- The installed assembly names and module/version constraints.

Then replace polling with a completed-generation notification, handle canceled work explicitly, and invalidate the union of affected old and new terrain bounds. Do not infer those API signatures from documentation prose or bind to private members by reflection.

## References

- [MicroVerse main manual](https://docs.google.com/document/d/1R4Ru7GKdVLLNVmfVwcX7RPRPrvkN0l36LNqm9LIMtno/edit): Spline Areas, Texture Stamps, scripting updates.
- [MicroVerse Masks manual](https://docs.google.com/document/d/1mFY_H88cyvHL8rtae7f0fWhdW7o7oabjzE7G0Cnuw9w/edit): per-terrain generated subassets, formats, and replacement behavior.
- [Unity 6.6: AssetDatabase.LoadAllAssetsAtPath](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/AssetDatabase.LoadAllAssetsAtPath.html).
- [Unity 6.6: AssetPostprocessor.OnPostprocessAllAssets](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/AssetPostprocessor.OnPostprocessAllAssets.html).
- [Unity 6.6: Texture.updateCount](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Texture-updateCount.html): GPU producers must advance this counter themselves.
- [Unity 6.6: IProcessSceneWithReport](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Build.IProcessSceneWithReport.html): scene processing and incremental build behavior.
