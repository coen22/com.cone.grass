# Validation for the Unity 6.6 grass preview

This document separates implemented source changes from results that require a Unity project and GPU. Keep issues #27–#34 open until their acceptance criteria have been exercised.

**Quality baseline:** TAA disabled, camera antialiasing **None**, grass **Motion Vectors > Off**, and zero queue overflow. Test single-sample output plus actual 2x/4x/8x MSAA attachments where supported. Temporal consumers are optional follow-up integrations and cannot satisfy the baseline silhouette, contact or LOD acceptance checks.

## Current evidence

| Check | Status |
|---|---|
| Repository source and shader contracts reviewed | Performed |
| Package and assembly-definition JSON, shader source links | Checked locally and in GitHub Actions |
| C# 9 syntax, 6.6/6.7 Editor, Release player and Checked player branches | Six configurations automated in GitHub Actions; consult the PR checks for the exact commit |
| New source metadata and whitespace | Checked locally |
| HLSL compilation against pinned public URP 17.6 headers | 102/102 DXC invocations passed locally: DXIL and SPIR-V; separate CI job added |
| Density, grid boundaries, Terrain decoding, LOD geometry, premultiplied edge math, projection/depth round trips | Mathematical/source checks performed |
| Core placement, settings, mesh and dispatch EditMode cases | Authored; not executed in Unity |
| Shipped compute kernels, contact shader, motion history and albedo GPU cases | Authored; not executed in Unity |
| Optional MicroVerse bridge EditMode cases | Authored; not executed in Unity |
| Unity 6.6 shader/C# import | Actual Editor CLI attempted; licensing rejected startup before import/tests |
| Unity 6.6 standalone build | Not run |
| Unity 6.7 import/build | Not run |
| GPU timings, allocations in Unity, RenderGraph viewer, rendered comparisons | Not run |
| Installed MicroVerse Core/Splines/Masks generation cycle | Not run |

GitHub Actions supplies .NET 8 for the pinned Roslyn syntax checker. The separate offline shader check downloads the official Microsoft DXC release and uses unmodified Unity Graphics headers at an exact source commit whose URP manifest declares 17.6.0. Its 102 configurations compile successfully to DXIL and SPIR-V without a Unity license. This does not establish Unity ShaderLab import, C# API binding, runtime resource binding, original shader-model compatibility, or rendered correctness. See the [compiler scope and provenance](../Tools~/ShaderChecks/README.md).

The first [GitHub workflow run](https://github.com/coen22/com.cone.grass/actions/runs/37128243485) passed source checks for commit `8280ce1ab2950b86a63e97d295cf4744feaf8b33`. Its Unity job was **skipped**, because the repository had no configured Unity licensing credentials. This records a checkpoint; it does not certify subsequent commits or any Unity acceptance case below.

The [PR checks](https://github.com/coen22/com.cone.grass/pull/35/checks) and [package validation workflow](https://github.com/coen22/com.cone.grass/actions/workflows/validation.yml) identify subsequent checked commits. The API review also replaced Unity 6.6's obsolete `GetInstanceID()` calls with `GetEntityId()`, retaining full identity for dictionary keys. Roslyn now rejects that known obsolete invocation as a regression guard; it remains a narrow source check rather than Unity API binding.

### Actual Unity CLI attempt — 2026-10-03

[PR #36](https://github.com/coen22/com.cone.grass/pull/36) adds a dedicated CLI availability probe. [Run 37132852610](https://github.com/coen22/com.cone.grass/actions/runs/37132852610) downloaded the official Unity 6000.6.0f1 Linux Editor, verified the archive's 4,167,231,268-byte size and SHA-256, extracted it, generated the test project, and launched the actual Editor with `-batchmode -nographics -runTests -testPlatform EditMode`.

The Editor exited with **198** and reported no valid Unity Editor license. Its result is **blocked_by_license**, with **tests_executed: false** and no test XML. [The run artifact](https://github.com/coen22/com.cone.grass/actions/runs/37132852610/artifacts/11276874299) contains the launcher log, Editor log and `cli-attempt.json`. The probe workflow completed because it recognized an explicit environment block; this is not a passing Unity test result. No license credentials are supplied by this availability probe. The licensed test and build jobs remain the engine acceptance gate.

The same probe can run against an installed Editor locally:

```sh
python3 Tools~/probe_unity_cli.py --editor /path/to/Editor/Unity --project Validation~/Project --output artifacts/unity-cli
```

It writes a new `running` attempt record before cleanup or launch, then removes previous XML and Editor logs. Reporting reads only that attempt's `editmode.xml`, so older sibling exports cannot supply missing passes or unrelated failures. Interrupted launches cannot retain an earlier passing record. Unexpected startup errors, missing results after a successful process exit, failed tests and timeouts fail the command.

The availability-probe workflow runs manually through `workflow_dispatch`. It supplies no license credentials and already established the startup block above; ordinary pull requests exercise its Python regressions. The licensed test and player-build jobs in `validation.yml` remain the engine acceptance gate.

## Reproducible test setup

Project generation checks its required bridge, Editor and runtime template roots before modifying output. A partial checkout fails without deleting existing scripts, metadata or saved scene references. Restore the missing templates before regenerating.

Use the included generator from the repository root:

```sh
python3 Tools~/validate_source.py .
python3 -B -m unittest discover -s Tools~/tests -v
python3 -B -m unittest discover -s Tools~/ShaderChecks -p 'test_*.py' -v
python3 -B Tools~/ShaderChecks/check.py
dotnet run --project Tools~/SourceChecks -- .
python3 Tools~/create_validation_project.py --unity 6000.6.0f1 --urp 17.6.0
```

It creates `Validation~/Project` with a local package dependency, Unity Test Framework 1.6.0, package test discovery, the generic MicroVerse bridge sample, and an editor bootstrap for a native URP asset. This disposable directory is ignored by Git. Open it in the selected Unity editor and verify that the native URP asset is active and RenderGraph is enabled. The generator refuses to overwrite an unrelated project.

Rerunning the generator updates owned template contents while preserving the existing `.meta` files of retained scripts and folders. Saved scenes and renderer assets therefore keep their script GUID references. Obsolete template assets and their metadata are removed; metadata supplied by a template takes precedence. Generated `ValidationSettings` assets are retained.

1. Record the full editor/package versions and graphics API.
2. Run all grass EditMode tests. Core tests cover placement, painting, additive scene registration, mesh topology, culling/grid math and contact projection parameters.
3. Run the `GrassGPU` cases with a graphics device. These execute rounded compute groups, capacity overflow, zero-after-populated reset, perspective/orthographic culling, mixed LOD transitions, the full-density plateau, Terrain height/holes, mesh-boundary height normalization, default shader passes, representative blade variants, motion-history lookup/triangle reconstruction and resource lifetime, regional density uploads and native terrain albedo baking. Motion resource tests also require an initialized URP instance; render a camera after opening the generated project if those tests report that precondition missing.
4. The imported MicroVerse bridge tests exercise synthetic saved-output assets, no-op polling, replacement and stale build inputs. They do not require proprietary MicroVerse assemblies and do not validate native producer events.
5. Create the rendering acceptance scene described below, save the scene and generated sources, then run clean and incremental standalone builds.

Example batch invocation, using the actual Unity executable and project path:

```sh
Unity -batchmode -projectPath /path/to/project -runTests -testPlatform EditMode -testResults /path/to/results/editmode.xml -logFile /path/to/results/editmode.log
```

Use `-nographics` for a headless C# import and non-GPU run. Do not add `-quit` to a Test Runner invocation: the runner exits after writing results. Run rendering tests on a machine with the target graphics API and GPU available. Do not infer graphics support from a headless import alone.

After a test run, inspect the result scope explicitly:

```sh
python3 Tools~/report_unity_results.py /path/to/results --require-gpu
```

The reporter fails a required GPU run if GPU cases are absent, skipped or incomplete. Omit `--require-gpu` for a headless import run; skipped graphics cases still appear in the report.

### Reproducible standalone scene and builds

The generated project contains `GrassValidationBuild`, which operates only in a project carrying the generator's marker. **Tools > Cone > Grass Validation > Create Rendering Scene** creates a flat native URP TerrainLit terrain, a saved synthetic density-mask asset consumed by the actual MicroVerse bridge, the production renderer feature, a directional light, an obstacle and a direct-output game camera. This synthetic mask exercises saved-output binding without pretending to execute MicroVerse generation.

To build the same scene twice, with the standalone target already installed:

```sh
Unity -batchmode -nographics -quit -buildTarget StandaloneLinux64 -projectPath /path/to/project -executeMethod GrassValidationBuild.BuildCurrent -logFile /path/to/results/build.log
```

`BuildCurrent` creates a clean development player and then rebuilds it incrementally without changing the scene or its assets. Both must succeed. It saves `Builds/build-results.json` and the executable under `Builds/<target>/` inside the generated project. Selecting the target on the command line lets Unity reload its platform configuration before the build method executes. Windows and macOS standalone targets are also accepted when installed and selected by the host Editor.

Both builds explicitly use **Managed Code Variant > Checked**, and the previous variant is restored afterward. In Unity 6.6, [managed-code diagnostics are independent of the Development Build option](https://docs.unity.com/en-us/engine/6000.6/manual/upgrade-guides/upgrade-guide-unity66): the default Release variant omits RenderGraph validation and profiling instrumentation even from a development player. The build also enables the pipeline's RenderGraph validity setting. The report records these settings and their restoration, and the running probe requires `UNITY_ENABLE_CHECKS`, `UNITY_INCLUDE_INSTRUMENTATION` and `RenderGraphGlobalSettings.enableValidityChecks` to be active.

Validate the current build attempt and its output with:

```sh
python3 Tools~/report_build_results.py /path/to/project/Builds
```

The build command starts a new attempt record before setup and marks it complete only after both builds and managed-code variant restoration succeed. It preserves each build's executable under `Builds/Evidence/<attemptId>/<kind>/` before the next build can replace the shared output. The reporter verifies the ordered build results, target and diagnostic settings, executable sizes and SHA-256 hashes, and the final incremental executable. A missing, incomplete or inconsistent report fails. These snapshots establish executable build evidence; full player data remains in the normal output directory, and player execution requires the separate probe below.

Launch the built player with `-grassSmoke -grassSmokeOutput /path/to/results/player` to run its opt-in rendering probe. A normal launch leaves the generated scene available for inspection. Do not use `-nographics` for this player probe.

The fourteen required stages cover grass off/on, wind at a fixed camera, a camera pan, a contact off/on pair, render scales 0.75/1.25, requested 2x/4x/8x MSAA, an empty mask, no sources and restored coverage. All required stages keep camera AA **None**, post-processing off and motion history **Off**. The pan records actual camera displacement; wind must change rendered pixels at the same camera pose. Contacts use a fixed camera with wind frozen and must produce darkening without brightening other pixels beyond the comparison tolerance. Empty masks and absent sources must restore the grass-disabled image as well as report zero counts.

A validation-only renderer feature observes the same color/depth attachments after the grass draw. It records allocated RenderTexture sample counts or explicit imported-target metadata for direct output, plus active viewport dimensions and the camera's scaled dimensions. Render-scale stages require the executed viewports to match the requested scale; a larger reused allocation is acceptable only when its active viewport is correct. The observer does not redirect the camera into a probe texture.

The probe records device details, asynchronous counts, frame identities, attachment formats/dimensions/sample counts, logs and PNG captures. Missing required capabilities, stale or incompatible attachments, timeouts, rendering errors, unexpected counts or overflow fail the run. A requested MSAA count that the device cannot support is recorded as **unsupported**, with its observed fallback count; it does not become a passing observation for the requested count.

Check the produced evidence with:

```sh
python3 Tools~/report_player_results.py /path/to/results/player
python3 Tools~/report_player_results.py /path/to/results/player --require-msaa 2 4 8
```

The second command requires successful observations at all three sample counts. The reporter checks the complete baseline, stage chronology, decoded captures, and the actual grass, wind, contact and empty-state image differences. Each launch writes an incomplete current-attempt record before setup, so an interrupted or failed launch cannot retain a previous passing result. It rejects absent, stale, contradictory or malformed evidence. Rebuild the player after changing the probe; older result formats do not establish this baseline.

Add `-grassSmokeMotion` only for the optional final motion stage, and use `--require-motion` when reporting that additional run. The baseline remains present and independent of that stage. This is functional rendering evidence: observing attachment samples and a changing grass image does not certify A2C image quality, contact-shadow quality, motion-vector values, native MicroVerse generation or performance gains. Inspect the captures and perform the detailed comparisons below.

### GitHub Actions

`Package validation` runs metadata checks, Python validation-tool tests, C# 9 parsing, project generation and actual offline HLSL compilation on pull requests. The separate Unity test job and dependent clean/incremental Linux player-build job run only when the repository has a usable license configuration. Configure secrets in GitHub using the organization's approved Unity license method:

- A floating-license endpoint in `UNITY_LICENSE_SERVER`; or
- `UNITY_EMAIL` and `UNITY_PASSWORD`, with `UNITY_LICENSE` for an activated license file or `UNITY_SERIAL` for the applicable serial-based license.

Do not paste license credentials into issue bodies or logs. The workflow tests only whether a configuration is present and never prints its contents. Action revisions, GameCI CLI and the Roslyn package are pinned. Follow [GameCI's licensing instructions](https://game.ci/docs/github/activation/) for the selected account/license.

The workflow saves compiler provenance, Unity results/logs, the standalone player and both build reports as artifacts. The build job does not execute its player. A source/shader success with skipped Unity jobs is not Unity validation. A Unity test-job success with skipped GPU cases is not rendering validation.

### Unity 6.7 target

The public [Unity 6000.7.0a6 release](https://unity.com/releases/editor/alpha/6000.7.0a6) was available when this continuation was reviewed on 2026-10-03. It is an alpha target, not a compatibility result. The generator and workflow accept a complete editor version and exact URP version:

```sh
python3 Tools~/create_validation_project.py --unity 6000.7.0a6 --urp 17.6.0 --output Validation~/Unity67
```

Verify the package version actually resolved by that editor, then record it with the import/build results. The workflow's manual inputs permit newer complete versions without pretending that C# conditional parsing tests their engine or shader APIs. Availability of the corresponding GameCI container must also be checked before an alpha CI run.

## Scene and camera matrix

| Dimension | Required cases |
|---|---|
| Pipeline | URP Forward and Forward+ |
| Camera | Game and Scene views; perspective and orthographic |
| Rendering | MSAA off, 2x, 4x, 8x where supported; render scale below/at/above one |
| Antialiasing baseline | Camera AA None, grass motion Off; record actual grass color/depth sample counts and unsupported fallbacks |
| Camera movement | Stationary rotation, slow pan, crossing capture snaps, teleport, negative X/Z, steep pitch |
| Lifetime | Enable/disable renderer and feature, change capacity/LOD quality, remove a camera, change active renderer asset |
| Editor | Domain reload on/off, scene reload on/off, opening Prefab Stage, multiple Scene views |
| Platform | Each intended D3D11, D3D12, Vulkan, Metal target |
| Lighting | Main light absent, one cascade, multiple cascades, hard/soft shadows, screen-space shadow feature |

Overlay, reflection, preview, and XR cameras are deliberately excluded by the current renderer. Deferred and alternative content/build pipelines require separate validation before being advertised.

## Placement and captures

- New empty painted asset: no grass. Fill, paint, erase, Undo, and Redo restore the expected world coverage.
- Drag across a small rotated/scaled map from far outside both edges. Brush dabs inside the map should remain dense, and outside-only travel must not alter coverage. Re-enter from a different side and verify the path follows the latest cursor endpoint. Undo during a stroke must release its continuation, preserve Redo, and require a new stroke before painting resumes.
- Circle/Box: move, rotate around Y, apply nonuniform X/Z scale, and soften edges. Verify all four map corners and that a small spot stays small when assigned a Terrain.
- External map: linear red-channel density, unreadable texture, missing texture, wrong texture dimension, replaced asset, and resolution change. Uncreated or released density RenderTextures must contribute no coverage without the consumer allocating them; recreate and populate them in the producer, notify unreported writes, then verify coverage returns. Destroy an explicitly assigned density asset while a lower-priority texture is present: coverage must remain absent until the asset binding is deliberately cleared or replaced.
- Assign Alpha8, integer and depth density textures directly to an area. They must contribute no coverage without changing producer storage. Replace or reinitialize the same texture with a usable red-channel format and verify density/ground capture recovers without changing the surface revision.
- Give an area finite coordinates and dimensions whose computed footprint overflows or collapses. Its old valid region must invalidate once, painting must stop, repeated observations must remain stable, and restoring usable coordinates must recover the original mapping.
- Destroy a bound density asset, explicitly clear or replace that binding, and verify external coverage can recover. Disable or rebind the area and ensure the old managed asset wrapper can no longer advance its revisions; the live replacement must retain exactly one subscription.
- Terrain: changed height, holes, different origin/size, negative coordinates, adjacent borders/corners, and multiple tiles.
- At fractional world origins, inspect adjacent terrain roots exactly on their shared world edge and just inside it. The upper-edge terrain must exclude the shared root while the lower-edge terrain accepts it once. An interior root whose normalized UV rounds to one must remain valid.
- Mesh surface: assigned paint surface, legacy height layer, partial vertex-color red, missing capture material, and a moved surface followed by Refresh Grass Data. Destroy or deactivate an explicit collider, remove/disable/force off its associated Renderer, or move its object into a preview/unloaded scene; coverage must stop. Empty meshes and meshes whose only indexed submesh has no corresponding material slot must also stop coverage. Explicitly clearing the binding restores the generic fallback. Disabling physics alone may retain the associated visible mesh support.
- Rotate a stationary camera to reveal previously off-screen parts of the same capture window.
- Moving color, mask, and slope modifiers work while the camera is stationary. After spawning new modifier renderers or replacing an inventory entry, refresh cached grass data.
- Check data-map orientation on each graphics API. World +X and +Z must move in the same direction in generation, density, color, slope, and ground capture.
- Inspect a top-down height map at several world altitudes. Stored R is world Y; G is validity. Camera altitude must not change the decoded world height.
- Verify a partial-validity mesh edge above and below Y=0. Filtering against cleared invalid texels must retain the valid surface height while coverage continues to feather.
- Small painted edits report a small uploaded rectangle, while fill/resize/Undo can upload the complete map. Verify both the regional-copy path and its full-upload fallback.
- Enable/disable a supporting renderer, replace its mesh, move its transform, and open an authored scene additively. Registration and cached coverage must be correct before the first rendered frame.
- Move the active scene-settings owner into a preview scene and enable a replacement before the old owner's next Update. The replacement must register and release the old generated meshes. A second settings component in an eligible scene must still be rejected while preserving its valid owner.
- Change an explicit renderer from one to two material slots while its mesh, bounds and initial indexed submesh remain unchanged. The added height subset must appear after capture invalidation. Changing material values without changing slot count must not invalidate this override-material capture.
- Begin with an inactive modifier and inactive terrain, populate the shared inventory, then activate them. Per-frame modifier capture and terrain surface discovery must use the retained inventory. Also retain support for an active generated terrain with `HideFlags.DontSave`; inactive objects must not draw before activation.

The projection models one captured mesh height at each XZ position. Ground color is also a shared XZ projection: vertically overlapping surfaces cannot retain different ground colors at identical XZ coordinates. Duplicate XZ roots are treated as ambiguous motion history. Stacked mesh layers and arbitrary vertically overlapping terrain/mesh authoring are outside this preview's validated scope.

## RenderGraph and GPU safety

- Use a Checked or Debug managed-code variant and enable the pipeline's RenderGraph validity checks when inspecting runtime resource hazards. A Development Build using Release does not include these checks in Unity 6.6, and diagnostic compilation alone does not enable the separate validity setting.
- Position and argument resources are imported once per camera graph; capture inputs and draw/compute dependencies are visible in the graph.
- Verify no capture read/write hazard, unbound compute texture, undeclared material texture, or indirect argument validation warning. Include modifier `_MainTex` overrides set through renderer-wide and per-material property blocks, then replace and remove those overrides.
- Verify integer grid, source flags, LOD offsets, and cascade count are uploaded as integers.
- Exercise included grid endpoints at positive and negative coordinates, including float32 root rounding and integer-to-float precision loss. The precise shader coordinate must agree with its written jitter-add/spacing-product order, and CPU enumeration must include its source cell. Keep the coordinate and candidate-budget rejection limits in force.
- Repeat boundary cases with sparse tile occupancy enabled. Its clipped tile bounds must retain the rounded root plus the capture filter margin, including negative coordinates and margins smaller than a world-coordinate ULP.
- Change the generated blade mesh's first-submesh index count, start or base vertex through the public mesh cache. The next resource update must refresh native indirect index metadata without replacing unchanged buffers; generation must still own instance counts.
- Test zero dispatches after previously drawing grass: counts and all three indirect instance counts become zero.
- Force a small buffer capacity. Writes remain in bounds; counts are clamped and overflow is reported asynchronously.
- Change capacity and LOD weights from scripts after initialization, including very large finite values. Capacity remains bounded and all three positive queues partition it exactly. Assign an incompatible blade material and confirm that camera resources release before URP depth or motion inputs are requested.
- A source/camera grid above the 67,108,864-candidate budget (64 × 1024²) must skip generation and issue the explicit warning. It must not silently choose a moving subset or wrap counters.
- Confirm argument layout/stride on every target API. The CPU probes the platform-provided typed layout instead of assuming the instance-count byte offset.
- Confirm no repeated large GPU or managed-array allocations at steady state. Large capacity reductions shrink the position buffer; bounded idle terrain/camera caches release their maps. Check repeated optional motion-mode changes release history, including removal of the material/compute prerequisite and unsupported camera changes.
- Grow and shrink the dispatch plan, reject a partial plan at the candidate budget, and interrupt texture import. Planning-only terrain-group references must be cleared while successful execution inputs remain intact; evicted groups must not stay reachable through unused pooled entries.
- Deactivate the renderer feature with `SetActive(false)` while a camera has allocated resources, then render another context. Its camera cache must release even though URP skips the feature. Switch to a different renderer asset and verify idle eviction still runs; reactivation must restore rendering. Destroy or disable the feature object and check callback teardown. Diagnostics from another active feature must remain intact.
- Release the material's borrowed wind RenderTexture. Forward, contact and optional motion must use the same fallback without creating storage or changing the source material. Recreate and populate it in the producer and verify all passes return to that source.
- Reconfigure that same wind RenderTexture for unresolved MSAA (`bindTextureMS` enabled with multiple samples). All grass passes must use the neutral fallback; automatically resolved MSAA and single-sample configurations remain accepted when actually supported by the device.
- Confirm motion history stays within the configured budget: `32 * capacity + 4 * NextPowerOfTwo(2 * capacity)` bytes for roots and hash keys, plus counts and texture snapshots. At two million blades the buffers use 77.04 MiB per active camera; each pair of 1024² RGBAHalf snapshots adds 16 MiB. Test the supported RGBAFloat fallback and device-limit rejection.
- Confirm normal camera matrices remain unchanged after top-down capture passes.
- Set capture-height endpoints to reversed or nonfinite values at runtime. Equivalent finite resolved ranges must reuse their cache; a changed resolved range must invalidate it.

## Blending and aliasing comparisons

Use the same scene, camera path, wind animation, resolution, and population settings for each comparison. Save short camera pans as well as still images.

Run the entire baseline with camera AA **None**, post-processing off and grass motion **Off**. Record zero overflow in every captured frame: atomic queue reservations do not guarantee a stable surviving subset when capacity is exhausted. Keep resolution and effective sample count in the evidence alongside the requested settings.

- Match a single TerrainLayer at several tile sizes, offsets, and diffuse remap settings.
- Compare selected layers at indices 0, 1 and 5 against native terrain albedo with only that layer weighted. Use UVs beyond one, Point/Bilinear filtering and different U/V wrap modes; later layers must use their four-layer group's first sampler. Change only sampler state and verify ground color refreshes while density and surface revisions remain unchanged. Reorder runtime terrain layers, call `MarkGroundColorDirty()` if no terrain notification is emitted, and verify the cached sampler source updates.
- Release and restore an external ground-color RenderTexture. Coverage should remain intact while color falls back to the selected layer or tint. Change only V wrapping or anisotropy on a bake source and verify it is detected as stale. A failing source-change observer must not prevent other consumers from being notified.
- Test a soft MicroVerse spline edge over a contrasting neighboring terrain layer. Bake native URP terrain albedo and compare it with the selected-layer fallback. A surviving blade at low density should receive the full configured ground-match strength when using a full-terrain color map.
- Bake beyond four layers, with height blending, opacity-as-density, remap edits, non-readable source textures and nonzero tile offsets. Reuse the saved output asset and check all consumers refresh only ground color.
- Load a saved bake record from before the albedo capture shader was included in its dependency key. Read-only build preflight must reject it; refresh must rebake once, preserve the output GUID and source assets, then settle. Later capture-shader changes must also invalidate that record.
- Move a small density patch over a terrain-aligned albedo map. Cropped density bounds must not stretch the terrain color UVs.
- Inspect root normals on slopes with Use Ground Normal off/on.
- Verify filtered color, slope, and ground map boundaries do not acquire dark fringes or a default bend.
- Compare minimum width zero/one pixel with specular fade disabled/enabled.
- Compare MSAA off/2x/4x/8x with Alpha to Coverage disabled/enabled. Record the actual grass color and depth attachment sample counts, including render-scale changes and direct screen output. A2C must stay off on single-sample targets; unsupported MSAA requests must be recorded as unsupported.
- For a fully covered blade, verify A2C preserves native MSAA geometry-edge coverage. Density and minimum-width compensation still attenuate partial blades. The single-sample contact depth records an analytic silhouette approximation and must not add a second edge fade to multisampled color.
- Check density and both geometry LOD boundaries during slow and fast movement. Near/middle/far should use different indirect mesh draws.
- At a steep downward view, bend blades far enough to reverse their projected triangle winding. Near, middle and far LODs must remain visible with matching contact and optional motion geometry. Compare calm and bent silhouettes with TAA disabled; rendering both sides must not flip their authored lighting normals.
- Check very distant grazing angles and blade tips. A deterministic fallback can still show a pattern; it is not a substitute for sufficient pixel coverage.

### Optional motion-vector acceptance

After the baseline, test any motion consumer the project actually needs. Run camera-and-object motion blur or custom consumers with Motion Vectors Auto/Always/Off. If a project separately opts into TAA, assess it here. Inspect the actual motion texture while panning, rotating, changing LOD, animating wind and editing an interaction texture. The source path reconstructs previous geometry, but temporal visual acceptance remains unrun.

- Render the same camera twice in one frame, skip a frame, change render scale and viewport, teleport, toggle motion mode and change material/spacing. Check history reset and resource lifetime, including edit-mode behavior.
- Use small motion-history test tables to force collisions, duplicate XZ roots and absent history. Those cases must not attach another blade's previous position.

## Contact-shadow comparisons

Check contacts enabled/disabled at the same exposure and lighting:

- Grass over flat and sloped terrain.
- An opaque object touching grass, grass beside an object, and isolated scene objects away from grass.
- Main light at high and low elevations, near/far distance fade, orthographic projection, reversed/normal Z.
- MSAA silhouettes and transparent objects rendered after the contact pass.
- Render scale changes, screen boundaries, and objects entering/leaving the frame.
- A grass-only receiver whose ray stays inside its own sampled grass texel must not darken from that sample. Repeat with scaled and shifted allocations and a slightly angled ray. A distinct grass texel or reachable scene depth behind the origin must still cast contact; scene depth beyond the ray's reach must not.

The overlay must leave unrelated scene-to-scene shading unchanged and preserve color alpha/MSAA samples. Increasing bias/thickness/length must have bounded, explainable effects. It has one nearest grass depth layer and no off-screen data; fractional silhouettes remain approximate. Its color multiplication attenuates all existing lighting, including ambient and emissive contributions.

## MicroVerse project acceptance

Use the actual installed versions of MicroVerse Core, Splines, and Masks:

1. One Spline Area drives a Texture Stamp and positive Mask Stamp with the same intended filters and falloff.
2. Bind each affected Terrain to the explicit corresponding saved texture name.
3. Edit spline shape, change mask resolution, replace output subassets, undo an edit, and cancel a generation.
4. Verify optional polling eventually refreshes completed saved results and never leaves old coverage after a missing/ambiguous output.
5. Refresh and save, close/reopen the scene, then make clean and incremental standalone builds.
6. Replace a saved mask output or edit a bake source after saving the scene. The unconditional build preflight must reject stale scene bindings before Unity can reuse cached scene data.
7. Preview an unsaved terrain texture edit. Its bake must not be certified by an older disk hash; save the source and rebake before build readiness is restored.
8. Edit or save a terrain input from a bake-completion observer. The next refresh must bake the changed input instead of certifying it against older pixels. A failed observer must be logged while later observers still receive the successful saved output.
9. Delete the current bridge owner and a later queued owner from a bake-completion observer. Watcher and scene-save iteration must skip removed objects and finish refreshing surviving consumers.
10. Delete or replace the output asset from a bake-completion observer. Refresh must fail without losing its configured path or certifying new bake keys, then recover on a later refresh. Also check a live nonpersistent recorded reference and a legitimate move of the same persistent output; the move must retain its GUID and avoid an unnecessary rebake.

The bridge does not know the native generation transaction state. Verified completion/cancellation callbacks require the installed Core/Splines/Masks source and remain follow-up work. The bridge checks saved scenes in an unconditional player-build preflight, because Unity can reuse cached scenes without invoking a scene processor. Prefab/addressable or other content builders must validate their own binding inputs.

## Performance capture

Record GPU/CPU frame times, grass population, draw calls, primitive counts, native/managed allocations, capture texture memory, position-buffer memory, and overflow. Include:

- Dense, sparse painted, and MicroVerse multi-terrain scenes.
- Static and continuously moving cameras.
- Static and moving modifiers.
- Contacts off/on with 4, 8, 16, and 32 steps.
- Motion Off/Auto/Always, including history memory, copy/hash cost, camera repeats and mode transitions.
- Small brush strokes, complete-map edits, no-op MicroVerse polls and actual rebakes.
- Main/SH lighting and optional Forward+ additional-light variants.

At five near subdivisions, geometry is 13/7/3 vertices and 11/5/1 triangles for near/middle/far. That reduction can be verified in the frame debugger. It does not establish a net GPU timing improvement: capture resolution, fragment lighting, map sampling, overdraw, contact passes, and visible density also affect cost.

Keep the issue checklists open until these results, including exact Unity/URP/MicroVerse versions and target GPUs, are recorded.
