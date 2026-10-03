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
8. Assign the **same TerrainLayer** used by the Texture Stamp to **Grass Terrain Layer**. For mixed grass/dirt boundaries, enable **Bake Terrain Ground Albedo**, choose a unique output `.asset` for this terrain, and click **Bake Terrain Albedo and Refresh**.
9. Set the grass renderer to authored placement, refresh, save the scene, and check the boundary in Scene and Game views. The placement area receives the positive density map, terrain frame, and matching ground color inputs.

Use a unique name for every terrain and mask. A spline crossing terrain boundaries needs one bridge per terrain and the matching output texture on each. Each terrain's own size and origin determine the mask coordinates; one tile's mask must not be stretched over its neighbors.

## Density and color

| Input | Meaning |
| --- | --- |
| Density red = 0 | No blades |
| Density red = 1 | Full authored density, before distance/quality reduction |
| Density red between 0 and 1 | Continuous thinning of stable world-space blade candidates |
| Grass Terrain Layer | Samples the same diffuse texture, terrain tiling/offset, and diffuse remap used by the selected layer |
| Bake Terrain Ground Albedo | Saves the native URP Terrain/Lit blend of all terrain layers as a terrain-aligned albedo map |
| Ground Color Override | Optional external color map covering this entire terrain, used when automatic terrain baking is disabled; takes precedence over the layer diffuse texture |
| Ground Tint / Strength | Controls the color contribution sent to the grass renderer |

Density must be linear data. The bridge rejects sRGB density textures so a painted density of 0.5 is not silently changed by color conversion. Missing or ambiguous outputs produce zero grass until they are resolved.

The renderer's root-to-tip blend makes the blade bases inherit the ground color while retaining blade variation farther up. Set the grass material's ground blend strength and root blend height to match the scene. Ground coloring and contact shadows are separate controls: color establishes the match; the shadow effect adds local darkening around contact.

A single TerrainLayer's diffuse color does not represent a blend of several terrain layers. **Bake Terrain Ground Albedo** uses the package's editor baker to capture the native URP Terrain/Lit albedo, including diffuse tiling, remapping, opacity-as-density, height blending, and terrain layers beyond the first four. It calls the installed URP blend functions. The output contains no lighting or contact-shadow darkening, so lighting is applied once when drawing the blades.

The bake uses GPU sampling, so source terrain textures can remain unreadable. It creates a linear RGBAHalf texture with mipmaps and updates an existing output asset in place to preserve its GUID and scene references. Use one output asset per Terrain. The default is 1024 x 1024; choose the lowest resolution that preserves the ground-color transitions visible beneath the blades.

Only the native `Universal Render Pipeline/Terrain/Lit` material is supported by this baker. Custom terrain materials, including procedural tint, snow, Shader Graph terrain and MicroSplat, need a final-albedo output from their own material workflow. Disable **Bake Terrain Ground Albedo** and supply that terrain-aligned map through **Ground Color Override**. Unsupported material configurations produce a clear error instead of a misleading partial bake.

The bridge owns its placement area's terrain, texture shape, density texture, edge falloff, and ground color bindings. Set these through the bridge. The generated mask already contains the spline falloff, so the bridge sets the placement area's additional edge falloff to zero. Blade density multipliers and other independent appearance controls remain available on the core components.

## Editing and regeneration

With **Auto Refresh** enabled, saved asset changes, scene opening/saving, inspector values, terrain texture changes and Undo/Redo schedule a refresh after a short debounce. **Poll Saved Outputs** checks once per second by default. The interval is adjustable, and polling can be disabled while keeping event-based refresh.

Mask discovery is cached per asset. Ordinary polling checks object identity, dimensions, graphics format, update count, editor dirty count and Unity's cached asset dependency hash. A steady poll does not reload every subasset, read pixels, change scene dirtiness or schedule a grass capture. A changed mask invalidates density and the associated ground-color coverage, while terrain height remains cached. A changed ground-color map invalidates only color. Terrain height edits use the core placement surface invalidation path.

The optional ground bake also uses a source signature, including terrain texture notifications, layer/material properties and asset changes. It reuses a current saved output across editor reloads and rebakes when its inputs change. GPU capture and readback occur during a required bake, not on each poll.

A bake of currently unsaved source changes can be previewed in the editor, but is marked unready for a player build. After saving those sources, the bridge bakes once more before recording a valid saved result. Bridges sharing one Terrain's output must also use the same resolution.

Automatic refresh does not know whether MicroVerse is generating, has completed, or has canceled an update. During editing, coverage can lag by the debounce/poll interval and can observe an intermediate producer output. A GPU producer is also allowed to write a texture without incrementing `Texture.updateCount` or raising an asset event. Such unreported writes cannot be detected by cheap polling. After generation finishes, **Refresh Saved Mask** explicitly invalidates density even if counters did not change; **Bake Terrain Albedo and Refresh** also forces a ground bake. Both preserve cached terrain height.

Changes to mask format or resolution can replace the generated texture subasset. The bridge resolves its selected **unique full texture name** again, so it can replace an obsolete reference when the replacement retains that name. If the name changes, select the new output explicitly. If multiple subassets have the same name, resolve the naming conflict before proceeding. The bridge never silently chooses one.

Changing the Terrain or MaskTarget in the inspector clears the selected density output and requires a new explicit mapping. Changing the Terrain also clears the ground-bake path so a different tile cannot overwrite the previous tile's bake accidentally. Moving an existing bake asset preserves its reference and updates its saved path. Disabling the bridge stops automatic authoring updates; the adjacent placement area retains the last resolved runtime inputs. Disable the placement area to stop drawing its grass.

## Saving and player builds

Finish MicroVerse generation and save its source assets. Refresh the bridge, bake if required, and save the scene before building. The runtime `GrassPlacementArea` serializes the resolved texture and Terrain references. Enabled bridges refresh before scene serialization so those runtime fields are part of the saved scene. An asset replacement performed later by another producer callback can still require a final refresh and save.

The player build preflight runs for **every build**, including incremental builds. It inspects the requested saved scenes in preview scenes and checks explicit mask resolution, saved assets, current core bindings, and the persisted source/output hashes of an enabled ground bake. It fails with the scene, object and corrective action if the saved bindings or bake are stale. This check neither saves open scenes nor repairs only a temporary build copy; an unsaved correction cannot silently pass the cached build path.

The versioned scene processor validates those same inputs and removes the authoring bridge component from the built scene copy. It never generates terrain or bakes textures during a build. Disabled bridges retain their last serialized placement data.

Unity 6.6's `BuildPipelineContext.DependOnAsset` tracking applies to `BuildContentDirectory`, not `BuildPlayer`; a scene-processing callback alone therefore cannot guarantee incremental player consistency. The sample declares its content dependencies and uses the unconditional player preflight for that reason. Custom content build scripts can call `GrassMicroVerseBridgeBuildProcessor.ValidateSavedScenes(scenePaths)` before building their scenes. Prefabs and addressable content outside that scene workflow still need their placement inputs prepared and saved by their own pipeline.

There is no MicroVerse lookup or polling in the player. Keep the generated texture assets as dependencies of the resolved placement components. The optional runtime marker contains no authoring fields in player compilation, even if an external content pipeline does not remove it.

## Validation in the target project

After importing the sample with Unity Test Framework available, run `GrassMicroVerseBridgeTests` in Test Runner. The tests cover explicit tile selection, empty configuration, texture replacement, duplicate names, sRGB rejection, TerrainLayer ownership, no-op polling, narrow density/color invalidation, cached discovery and read-only player preflight over saved scenes. The core editor baker has separate rendering tests. These tests exercise generic Unity subassets; they do not replace validation against the installed MicroVerse version.

- Start with a black mask and confirm no grass appears. Add one spline area and check that blades appear only within its white/gray coverage.
- Check the same spline falloff and filters on the Texture Stamp and Mask Stamp. Inspect both density preview and terrain texture weights, especially on slopes and near the edge.
- Move, resize, and delete the spline with a stationary camera. Let generation finish, then refresh. Confirm grass disappears from the previous region, appears in the current region, and follows Undo/Redo.
- Change mask resolution and format, regenerate, and refresh. Verify the resolved texture reference changes and the region remains correct.
- Test a spline crossing two differently positioned or sized terrains. Confirm each bridge points to that tile's generated texture.
- Check the transition with the selected TerrainLayer and then with a blended ground color override if several terrain textures meet there.
- Make both a clean and an incremental standalone build and confirm coverage, ground tint, and contact shadow settings match the editor. Replace a generated texture without saving corrected scene bindings and verify that both paths reject the stale configuration before building.

## Native integration follow-up

Public publisher manuals describe update and cancellation behavior but do not publish the event signatures or MaskTarget getter needed for a native adapter. The public [MicroVerse demo](https://github.com/JDB-Tech-INC/MicroVerse-Demo) requires separately installed MicroVerse modules and does not contain those native implementations. Its public source tree was checked on 2026-10-03. Native completion/cancellation integration remains pending.

The concrete inputs needed from a licensed installation are:

- The `MicroVerse` class source containing update-start, completion and cancellation declarations **and the call sites** that establish their order.
- `MaskTarget` and the supported per-Terrain getter, including replacement behavior. The Masks manual points to `AmbientArea.GetFalloff` as its consumer example.
- Core, Splines and Masks package manifests/assembly definitions so a version-specific adapter can target the correct modules.
- Any generation/save/build callbacks that affect those outputs or strip authoring objects.

With that verified API, a native adapter can suspend observation of partial generation, refresh affected terrains on a successful completion, and preserve the previous committed state after cancellation. The generic `Refresh`/baker path is available for the final commit step. No guessed event names, private reflection or proprietary source are included in this sample.

## References

- [MicroVerse main manual](https://docs.google.com/document/d/1R4Ru7GKdVLLNVmfVwcX7RPRPrvkN0l36LNqm9LIMtno/edit): Spline Areas, Texture Stamps, scripting updates.
- [MicroVerse Masks manual](https://docs.google.com/document/d/1mFY_H88cyvHL8rtae7f0fWhdW7o7oabjzE7G0Cnuw9w/edit): per-terrain generated subassets, formats, and replacement behavior.
- [Unity 6.6: AssetDatabase.LoadAllAssetsAtPath](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/AssetDatabase.LoadAllAssetsAtPath.html).
- [Unity 6.6: AssetPostprocessor.OnPostprocessAllAssets](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/AssetPostprocessor.OnPostprocessAllAssets.html).
- [Unity 6.6: Texture.updateCount](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Texture-updateCount.html): GPU producers must advance this counter themselves.
- [Unity 6.6: IProcessSceneWithReport](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Build.IProcessSceneWithReport.html): scene processing and incremental build behavior.
- [Unity 6.6: BuildPipelineContext](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Build.BuildPipelineContext.html): limits of explicit scene dependency tracking.
- [Unity 6.6: BuildPlayerProcessor](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Build.BuildPlayerProcessor.html): unconditional player build preparation.
- [Unity 6.6: OpenPreviewScene](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/SceneManagement.EditorSceneManager.OpenPreviewScene.html): isolated inspection of saved scene objects.
