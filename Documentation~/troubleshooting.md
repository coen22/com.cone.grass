# Troubleshooting

## No grass appears

Work down this list. The Infinite Grass Renderer's inspector reports the first two problems itself.

| Check | Fix |
|---|---|
| **URP is not active.** The inspector says "Infinite Grass needs the Universal Render Pipeline." | Assign a URP asset in **Project Settings > Graphics**, and in **Quality** if you override it per level. |
| **The renderer feature is missing or disabled.** The inspector says "The active URP renderer has no Grass Data Renderer Feature". | Click **Add Grass Data Renderer Feature**, or **Tools > Cone > Grass > Add Renderer Feature to Active Renderer**. If your camera uses a renderer other than the default, add the feature to that renderer by hand. |
| **No grass material.** | Assign **Grass Material** on the Infinite Grass Renderer, for example **Grass Blade**. A custom material's shader needs a `GrassForward` pass. |
| **Draw Blades is off.** | Turn it on. |
| **No Placement Area.** In **Authored Areas** mode, grass grows only inside areas. | Add a Placement Area, or switch an old scene to **Legacy Surface Layer**. |
| **The area is empty.** Density 0, a Texture shape without a map, or an empty density map ("Empty — no grass"). | Raise **Density**, assign a map, or paint/**Fill** it. A new paintable map starts empty. |
| **The density texture is unusable.** The area inspector warns about the format, sRGB or a RenderTexture. | Use a linear texture with density in red (R8 or RGBA). Turn off sRGB. A RenderTexture must be created and not use Bind Texture MS. |
| **The area has no surface.** No Terrain or Paint Surface, and nothing on the feature's Height Map Layer under it. | Assign the area's **Terrain** or **Paint Surface**. |
| **The area is far from the camera.** | Grass ends at **Draw Distance** and thins after **Full Density Distance**. |
| **The camera does not draw grass.** | The camera must be a Base camera, not XR, and its culling mask must include the Infinite Grass Renderer's layer. In the Scene view, **Render In Scene View** must be on. |
| **A second Infinite Grass Renderer disabled itself.** Console: "Only one enabled Infinite Grass Renderer can own the scene settings." | Keep one renderer enabled across loaded scenes. |
| **Compute is unsupported.** | The GPU and graphics API need compute shaders, instancing and indirect arguments. WebGL and some mobile devices do not qualify. |
| **The surface is outside the capture height range** (mesh surfaces). | Widen **Capture Height Range** to enclose the mesh. |

## Grass is over capacity

Turn on **Preview Visible Grass Count**. If "over capacity" is above 0, a GPU queue is full and roots are being dropped, often in a pattern that changes every frame.

- Raise **Max Buffer Count**, or for flower heads **Max Head Count**.
- Or change **Lod Capacity Weights** toward the queue that overflows. Near grass usually needs the most.
- Or reduce the blade count: larger **Spacing**, shorter **Full Density Distance**.

From a script, read `InfiniteGrassRenderer.Instance.OverflowGrassCount` while the preview is on.

## Flickering, shimmering or aliasing

- **Evaluate without TAA first.** Set the camera's anti-aliasing to None and **Motion Vectors** to Off, then add TAA once the grass is stable.
- **Use MSAA** (2× or 4× on the URP asset). With **Alpha To Coverage** on, blade edges become smooth coverage. Alpha to coverage is used only when the camera's actual target is multisampled.
- **Keep Minimum Pixel Width at about 1.** It widens thin far blades and lowers their coverage to match, so they stay solid without making the field denser. 0 lets far blades break into dots.
- **Lower Expand Distant Grass Width** if far grass looks too dense, or raise it if far grass looks sparse.
- **Pull Specular Fade Range closer** if distant grass sparkles.
- **Check overflow** (above): dropped roots change every frame.
- **Grass pops at the screen edge**: raise **Culling Padding** for tall or strongly bent grass.
- **Flower heads sparkle at range**: shorten **Head Draw Distance**; heads already fade out below about three pixels.

## Grass grows through floors or objects

Authored areas on a Terrain follow the terrain's height. Anything on top, such as a rock, a path mesh or a building floor, does not stop the grass by itself.

- Paint the density out under the object, or shape the area around it.
- Or place a mesh with a material using **InfiniteGrass/Modifiers/GrassMaskShader** over the spot. It removes grass under it, from above, and is invisible to the camera. Call `RefreshGrassData()` after spawning one from a script.
- Terrain holes remove grass automatically.

Captures look straight down. Grass on a lower surface cannot coexist with a different surface directly above it at the same X/Z (a lawn under a bridge, a cave below a field): the upper surface's data wins.

## Bending does not work

Read the interactor's `State` and `Diagnostic` from a script or the debugger:

| State | Diagnostic | Fix |
|---|---|---|
| Disabled | | Enable the component and its GameObject. |
| MissingCollider | "Assign the character's actual collider." | Assign **Actor Collider**. |
| UnsupportedCollider | "Use a supported upright collider and an explicit self root that contains it." | Use a CharacterController, an upright CapsuleCollider or a SphereCollider. Set **Actor Root** to a transform that contains it. |
| NoGroundContact | "The collider has no unique, usable grass support within its grounding clearance." | See below. |
| PathReset | "The discontinuous or over-budget path was cleared; only the current footprint was retained." | Normal after a teleport or a long frame. If constant, see the trail budget. |
| TrailBudgetExceeded | "The current footprint exceeds the interaction budget. Increase grid spacing or reduce the collider footprint." | Raise **Grid Spacing** or **Node Capacity**. |
| MissingCaptureMaterial | "The retained InfiniteGrassInteraction resource material is missing or has no GrassSlope pass." or "The assigned interaction shader must provide a GrassSlope pass using premultiplied vertex RGBA." | Clear **Capture Shader**, or fix the custom shader. |
| InvalidTime | "The interaction clock must be finite and monotonic between observations." | In Manual mode, pass a time that never decreases. |
| Active / Recovering | | Working: bending now, or grass still recovering. |

For **NoGroundContact**, check that:

- the area's **Terrain** has a **Terrain Collider** using the same TerrainData, or the area's **Paint Surface** is the collider the character stands on;
- legacy and generic surfaces have **Explicit Support** set;
- the surface is on **Accepted Support Layers**;
- the character is within **Ground Clearance** (0.15 m) of the ground;
- no other floor sits between the character and the grass surface, and two grass areas on different surfaces do not overlap under it;
- the character's own colliders are under **Actor Root**.

If the state is Active but nothing visibly bends, check that **Bend Strength** is above 0 and that the footprint is not smaller than a capture pixel (about 0.6 m at the defaults): raise **Capture Resolution** or lower **Draw Distance**.

## Grass looks wrong after moving the camera far

Positions are 32-bit floats, so precision drops far from the world origin. For very large worlds use a floating origin, and call `ClearHistory()` on interactors when you shift it.

## MicroVerse

- **No grass from a MicroVerse mask.** Let MicroVerse finish, save the scene, then click **Refresh Saved Mask** on the bridge. Check that **Generated Density Texture** names the texture for *this* terrain.
- **Grass and ground texture do not line up.** The Texture Stamp and the Mask Stamp must use the same Spline Area, falloff and filters.
- **The build fails with a stale bridge message.** Refresh the bridge, bake if needed, save the scene and build again.

See [Integrations](integrations.md#microverse-mask-bridge).

## Run the tests

See [Getting started](getting-started.md#run-the-tests).
