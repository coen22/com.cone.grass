# Saved-scene fixture ownership

## Read-only package installations

Issue #60 records Unity 6000.6 rejecting an additive open of
`Packages/com.cone.grass/Tests/Editor/GrassSupportingSceneEmpty.unity`
with `Opening scene in read-only package!`. A writable local package did not
expose the same error. The former `GrassSavedSceneFixture` copied its source
scene into `Assets/`, but opened the package template itself when a second
scene was requested.

The fixture now copies the template into a unique `Assets/GrassSavedScene_*.unity`
asset for **each** requested scene. The package template is an input only:
it is never opened, saved or deleted. Both loaded scenes are normal additive
scenes, not preview scenes. Source and target have distinct asset GUIDs and
scene handles. No user scene is saved or recreated to accommodate an untitled
scene left by a Play-to-Edit return.

## Ownership and cleanup

The fixture records an asset GUID after each successful copy. Opening or
reopening a copy, saving the source, and deleting either copy require the
current path to retain that recorded identity. A replacement asset at the
same path must not be overwritten or deleted by fixture cleanup.

Only a scene handle returned for a successfully acquired copy can become an
owned source or target. Existing or uncertain loaded scenes are rejected.
Cleanup attempts both owned scene closures and both owned asset deletions
independently. An uncertain, still-loaded or replaced asset is retained and
reported as a failure, not silently removed. A partial acquisition does not
grant ownership of the package template or a pre-existing scene.

The existing checks for prior active scene, scene count and order, handle,
path, name, loaded state, dirty state, root objects and order, and scene-manager
setup remain in place. The package template's dependency hash must remain
unchanged. Body and cleanup failures are still aggregated rather than hidden.

## Regression coverage and required execution

The existing test identities, event expectations, registration checks and
batch reload-guard inventory are unchanged:

- `GrassPlacementAreaTests.SupportingSceneChangesPublishSurfaceEventsWithoutMovingTheSeparateArea(False)`
- `GrassPlacementAreaTests.SupportingSceneChangesPublishSurfaceEventsWithoutMovingTheSeparateArea(True)`
- `GrassPlacementAreaTests.AnAreaRegistersWhenASavedSceneIsOpenedAdditively`
- `GrassSettingsLifecycleTests.AdditiveSceneActivationRegistersSettingsBeforeFirstUpdate`

Their shared fixture now also asserts writable copy paths, distinct asset
identities, ownership on reopen/save/delete, and removal of both copies.
These are retained native regressions with stronger fixture assertions, not
newly executed native results.

Run the original scene leaves in their post-Play ordered context on Unity
6000.6 / URP 17.6, first with a read-only Git package and then with a writable
local package. Retain an existing unsaved untitled scene and verify that the
fixture leaves it unchanged. Use fresh discovery and preserve the original
failure logs, current XML, actual Play/Edit transitions and cleanup evidence.
The [validation guide](Validation.md) describes the supported commands and
opt-in full-core batch reload guard.

Also qualify normal one-scene and two-scene acquisition, save/close/reopen,
partial-acquisition failure, and same-path replacement refusal. No relaxed
event, capture, cleanup or test-result assertion is part of this repair.

Source parsing, package checks and offline shader compilation cannot establish
Unity scene-loading behavior. Issue #60 remains open until the original cases
and preservation requirements are demonstrated in both package modes. This
fixture repair does not qualify unrelated placement, capture, lifecycle or
baker failures tracked in #66, nor rendering or performance acceptance.
