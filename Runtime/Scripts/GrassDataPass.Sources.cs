using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public partial class GrassDataRendererFeature
{
    // The renderer and terrain inventory and what each camera's capture window collects from it.
    private sealed partial class GrassDataPass
    {
        // The first active terrain group supplies TerrainLit colour and detail at blade roots.
        private static Terrain SplatTerrain(CameraState state)
        {
            foreach (TerrainGroup group in state.ActiveGroups)
                if (group.Terrain && group.Terrain.terrainData)
                    return group.Terrain;
            return null;
        }

        public bool WantsMotion(Camera camera, bool postProcessEnabled, AntialiasingMode antialiasing,
            int msaaSamples, GrassMotionVectors.Settings settings, Material material)
        {
            bool requested = GrassMotionVectors.IsRequested(camera, postProcessEnabled, antialiasing, msaaSamples, settings) &&
                motion.PrepareCamera(camera, material);
            if (!requested)
                motion.Release(camera);
            return requested;
        }

        public bool TryGetForwardPass(InfiniteGrassRenderer owner, out int forwardPass)
        {
            forwardPass = owner.grassMaterial ? owner.grassMaterial.FindPass("GrassForward") : -1;
            if (forwardPass >= 0)
            {
                missingForwardWarning = false;
                return true;
            }
            if (!missingForwardWarning)
                Debug.LogWarning("The grass material needs the GrassForward pass from the package blade shader.", owner);
            missingForwardWarning = true;
            // Invalid materials cannot use any camera buffers or capture maps.
            // Reject them before allocation or requesting URP depth/motion work.
            ReleaseCameras();
            return false;
        }

        private bool ValidateBufferCapacity(InfiniteGrassRenderer owner, Camera camera, long deviceLimit)
        {
            long roots = (long)owner.Capacity + owner.HeadQueueCapacity(0) +
                (owner.variants && owner.variants.headMesh ? owner.HeadQueueCapacity(1) : 0);
            if (roots * sizeof(float) * 4 <= deviceLimit)
            {
                warnedBufferLimit = false;
                return true;
            }
            // Reject before creating a state or refreshing its activity time.
            // Previously allocated resources cannot serve this configuration.
            ReleaseCamera(camera);
            if (!warnedBufferLimit)
                Debug.LogWarning("The requested grass position buffer exceeds this device's maximum graphics-buffer size. Reduce GPU Capacity.", owner);
            warnedBufferLimit = true;
            return false;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => InvalidateInventory();
        private void OnSceneUnloaded(Scene scene) => InvalidateInventory();
        private void InvalidateInventory() => inventoryDirty = true;

        private void OnTerrainHeightChanged(Terrain terrain, RectInt region, bool synched) => MarkTerrainSurfaceDirty(terrain);

        private void OnTerrainTextureChanged(Terrain terrain, string textureName, RectInt region, bool synched)
        {
            if (textureName == TerrainData.HolesTextureName)
                MarkTerrainSurfaceDirty(terrain);
        }

        private void MarkTerrainSurfaceDirty(Terrain terrain)
        {
            foreach (CameraState state in cameras.Values)
                if (state.SurfaceTerrains.Contains(terrain))
                    state.SurfaceDirty = true;
        }

        private void EnsureInventory(InfiniteGrassRenderer owner)
        {
            // Paint/MicroVerse source revisions and camera movement never trigger
            // scene discovery. New runtime renderers/material assignments can
            // explicitly request discovery with owner.RefreshGrassData().
            if (!inventoryDirty && inventoryOwner == owner && inventoryOwnerRevision == owner.Revision)
                return;
            // Existing sources may be activated after discovery. Keep them in
            // the shared inventory and apply active/scene eligibility at capture.
            rendererInventory = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include);
            terrainInventory.Clear();
            // Native active terrains can include runtime DontSave components,
            // which FindObjectsByType omits even when inactive objects are included.
            Terrain.GetActiveTerrains(terrainInventory);
            Terrain[] discoveredTerrains = UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include);
            for (int i = 0; i < discoveredTerrains.Length; i++)
                if (!terrainInventory.Contains(discoveredTerrains[i]))
                    terrainInventory.Add(discoveredTerrains[i]);
            RebuildModifierInventory();
            inventoryOwner = owner;
            inventoryOwnerRevision = owner.Revision;
            inventoryDirty = false;
            unchecked { inventoryRevision++; }
        }

        private void CollectSources(CameraState state, Bounds captureBounds, bool authored)
        {
            state.Sources.Clear();
            state.ActiveGroups.Clear();
            state.ExplicitSurfaces.Clear();
            state.HasMeshSurfaceFallback = false;
            state.NextGroundVersion = VersionSeed;
            state.NextSurfaceVersion = VersionSeed;
            foreach (TerrainGroup group in state.Groups.Values)
            {
                group.Sources.Clear();
                group.DensityVersion = VersionSeed;
            }

            IReadOnlyList<GrassPlacementArea> areas = GrassPlacementArea.ActiveAreas;
            for (int i = 0; authored && i < areas.Count; i++)
            {
                GrassPlacementArea area = areas[i];
                if (!area || !area.IntersectsCoverage(captureBounds) ||
                    !area.TryGetCaptureData(out GrassPlacementDrawData data) ||
                    !IntersectsXZ(data.WorldBounds, captureBounds))
                    continue;
                var source = new PlacementSource { Area = area, Data = data };
                state.Sources.Add(source);
                uint sourceHash = unchecked((uint)area.GetEntityId().GetHashCode());
                state.NextGroundVersion = MixVersion(MixVersion(state.NextGroundVersion, sourceHash), area.GroundColorRevision);
                state.NextSurfaceVersion = MixVersion(MixVersion(state.NextSurfaceVersion, sourceHash), area.SurfaceRevision);
                if (!data.Terrain)
                {
                    Renderer supportingRenderer = area.PaintSurface ? area.PaintSurface.GetComponent<Renderer>() : null;
                    if (!supportingRenderer && area.PaintSurface)
                        supportingRenderer = area.PaintSurface.GetComponentInParent<Renderer>();
                    if (supportingRenderer)
                    {
                        state.ExplicitSurfaces.Add(supportingRenderer);
                        state.NextSurfaceVersion = MixVersion(state.NextSurfaceVersion,
                            unchecked((uint)supportingRenderer.GetEntityId().GetHashCode()));
                        uint visibility = (supportingRenderer.enabled ? 1u : 0u) |
                            (supportingRenderer.forceRenderingOff ? 2u : 0u) |
                            (supportingRenderer.gameObject.activeInHierarchy ? 4u : 0u) |
                            ((uint)supportingRenderer.gameObject.layer << 3);
                        state.NextSurfaceVersion = MixVersion(state.NextSurfaceVersion, visibility);
                        state.NextSurfaceVersion = MixVersion(state.NextSurfaceVersion,
                            unchecked((uint)supportingRenderer.localToWorldMatrix.GetHashCode()));
                        state.NextSurfaceVersion = MixVersion(state.NextSurfaceVersion,
                            unchecked((uint)supportingRenderer.bounds.GetHashCode()));
                        Mesh supportingMesh = supportingRenderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh :
                            (supportingRenderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null);
                        state.NextSurfaceVersion = MixVersion(state.NextSurfaceVersion,
                            supportingMesh ? unchecked((uint)supportingMesh.GetEntityId().GetHashCode()) : 0u);
                        // Height capture emits one draw per material slot even
                        // though its override material ignores the slot values.
                        sharedMaterials.Clear();
                        supportingRenderer.GetSharedMaterials(sharedMaterials);
                        state.NextSurfaceVersion = MixVersion(state.NextSurfaceVersion, (uint)sharedMaterials.Count);
                    }
                    else
                        state.HasMeshSurfaceFallback = true;
                }
                EntityId key = data.Terrain ? data.Terrain.GetEntityId() : EntityId.None;
                if (!state.Groups.TryGetValue(key, out TerrainGroup group))
                {
                    group = new TerrainGroup { Key = key };
                    state.Groups.Add(key, group);
                }
                if (group.Sources.Count == 0)
                {
                    group.Terrain = data.Terrain;
                    group.LastUsedTime = state.LastUsedTime;
                    state.ActiveGroups.Add(group);
                }
                group.Sources.Add(source);
                group.DensityVersion = MixVersion(MixVersion(group.DensityVersion, sourceHash), area.DensityRevision);
            }
            // Keep the shared primary-density texture assigned to a stable group
            // even when source registration order changes.
            state.ActiveGroups.Sort(GroupKeyComparer.Instance);
            state.StaleGroupKeys.Clear();
            state.InactiveGroups.Clear();
            foreach (KeyValuePair<EntityId, TerrainGroup> pair in state.Groups)
                if (pair.Value.Sources.Count == 0)
                {
                    if (state.LastUsedTime - pair.Value.LastUsedTime > GroupIdleSeconds)
                        state.StaleGroupKeys.Add(pair.Key);
                    else
                        state.InactiveGroups.Add(pair.Value);
                }
            state.InactiveGroups.Sort(GroupAgeComparer.Instance);
            for (int i = 0; i < state.InactiveGroups.Count - MaximumRetainedInactiveGroups; i++)
                state.StaleGroupKeys.Add(state.InactiveGroups[i].Key);
            for (int i = 0; i < state.StaleGroupKeys.Count; i++)
            {
                TerrainGroup inactive = state.Groups[state.StaleGroupKeys[i]];
                inactive.DensityMap?.Release();
                state.Groups.Remove(state.StaleGroupKeys[i]);
            }
        }

        private void CollectSurfaceTerrains(CameraState state, Bounds captureBounds, bool authored)
        {
            state.SurfaceTerrains.Clear();
            for (int i = 0; i < state.ActiveGroups.Count; i++)
            {
                Terrain terrain = state.ActiveGroups[i].Terrain;
                if (terrain && terrain.terrainData)
                    state.SurfaceTerrains.Add(terrain);
            }
            if (!authored || state.HasMeshSurfaceFallback)
            {
                for (int i = 0; i < terrainInventory.Count; i++)
                {
                    Terrain terrain = terrainInventory[i];
                    if (!terrain || !terrain.isActiveAndEnabled || !terrain.terrainData ||
                        !terrain.gameObject.scene.IsValid() || !terrain.gameObject.scene.isLoaded ||
                        (surfaceLayer.value & (1 << terrain.gameObject.layer)) == 0 ||
                        state.SurfaceTerrains.Contains(terrain))
                        continue;
#if UNITY_EDITOR
                    if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(terrain.gameObject.scene))
                        continue;
#endif
                    Bounds bounds = new Bounds(terrain.transform.position + terrain.terrainData.size * 0.5f,
                        terrain.terrainData.size);
                    if (IntersectsXZ(bounds, captureBounds))
                        state.SurfaceTerrains.Add(terrain);
                }
            }
            for (int i = 0; i < state.SurfaceTerrains.Count; i++)
            {
                Terrain terrain = state.SurfaceTerrains[i];
                TerrainData data = terrain.terrainData;
                state.NextSurfaceVersion = MixVersion(state.NextSurfaceVersion, unchecked((uint)terrain.GetEntityId().GetHashCode()));
                state.NextSurfaceVersion = MixVersion(state.NextSurfaceVersion, unchecked((uint)terrain.transform.position.GetHashCode()));
                state.NextSurfaceVersion = MixVersion(state.NextSurfaceVersion, unchecked((uint)data.size.GetHashCode()));
                state.NextSurfaceVersion = MixVersion(state.NextSurfaceVersion, unchecked((uint)data.GetEntityId().GetHashCode()));
                state.NextSurfaceVersion = MixVersion(state.NextSurfaceVersion,
                    data.heightmapTexture ? unchecked((uint)data.heightmapTexture.GetEntityId().GetHashCode()) : 0u);
                state.NextSurfaceVersion = MixVersion(state.NextSurfaceVersion,
                    data.holesTexture ? unchecked((uint)data.holesTexture.GetEntityId().GetHashCode()) : 0u);
            }
        }

        private static ulong MixVersion(ulong hash, uint value) => unchecked((hash ^ value) * 1099511628211UL);

        private void CollectRendererDraws(CameraState state, Bounds captureBounds, bool heights, bool modifiers, bool authored)
        {
            if (heights)
            {
                state.HeightDraws.Clear();
                state.RequiresMeshHeight = false;
                // Explicit supporting renderers do not need a selected layer or
                // a scene-inventory refresh after they are assigned at runtime.
                if (authored)
                    foreach (Renderer renderer in state.ExplicitSurfaces)
                        CollectRendererDraws(state, renderer, captureBounds, true, false);
                if (!authored || state.HasMeshSurfaceFallback)
                    for (int i = 0; state.CaptureRenderers != null && i < state.CaptureRenderers.Length; i++)
                    {
                        Renderer renderer = state.CaptureRenderers[i];
                        if (!renderer || (surfaceLayer.value & (1 << renderer.gameObject.layer)) == 0 ||
                            (authored && state.ExplicitSurfaces.Contains(renderer)))
                            continue;
                        CollectRendererDraws(state, renderer, captureBounds, true, false);
                    }
            }
            if (modifiers)
            {
                state.MaskDraws.Clear();
                state.ColorDraws.Clear();
                state.SlopeDraws.Clear();
                for (int i = 0; state.ModifierRenderers != null && i < state.ModifierRenderers.Count; i++)
                    CollectRendererDraws(state, state.ModifierRenderers[i], captureBounds, false, true);
            }
        }

        private void CollectRendererDraws(CameraState state, Renderer renderer, Bounds captureBounds,
            bool heights, bool modifiers)
        {
            if (!renderer || !renderer.enabled || renderer.forceRenderingOff ||
                !renderer.gameObject.activeInHierarchy || !renderer.gameObject.scene.IsValid() ||
                !renderer.gameObject.scene.isLoaded || !IntersectsXZ(renderer.bounds, captureBounds))
                return;
#if UNITY_EDITOR
            if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(renderer.gameObject.scene))
                return;
#endif
            state.RequiresMeshHeight |= heights;
            sharedMaterials.Clear();
            renderer.GetSharedMaterials(sharedMaterials);
            for (int submesh = 0; submesh < sharedMaterials.Count; submesh++)
            {
                Material sourceMaterial = sharedMaterials[submesh];
                if (heights && heightMaterial)
                    state.HeightDraws.Add(new RendererDraw(renderer, heightMaterial, submesh, 0));
                if (!modifiers || !sourceMaterial)
                    continue;
                AddModifierDraw(state.MaskDraws, renderer, sourceMaterial, submesh, "GrassMask");
                AddModifierDraw(state.ColorDraws, renderer, sourceMaterial, submesh, "GrassColor");
                AddModifierDraw(state.SlopeDraws, renderer, sourceMaterial, submesh, "GrassSlope");
            }
        }

        private static void AddModifierDraw(List<RendererDraw> draws, Renderer renderer,
            Material material, int submesh, string lightMode)
        {
            int pass = FindModifierPass(material, lightMode);
            if (pass >= 0)
                draws.Add(new RendererDraw(renderer, material, submesh, pass));
        }

        private void RebuildModifierInventory()
        {
            modifierInventory.Clear();
            for (int i = 0; i < rendererInventory.Length; i++)
            {
                Renderer renderer = rendererInventory[i];
                if (!renderer)
                    continue;
                sharedMaterials.Clear();
                renderer.GetSharedMaterials(sharedMaterials);
                for (int m = 0; m < sharedMaterials.Count; m++)
                {
                    Material material = sharedMaterials[m];
                    if (!material || (FindModifierPass(material, "GrassMask") < 0 &&
                        FindModifierPass(material, "GrassColor") < 0 && FindModifierPass(material, "GrassSlope") < 0))
                        continue;
                    modifierInventory.Add(renderer);
                    break;
                }
            }
        }

        private static int FindModifierPass(Material material, string lightMode)
        {
            int pass = material.FindPass(lightMode);
            if (pass < 0)
            {
                ShaderTagId expected = new ShaderTagId(lightMode);
                for (int i = 0; i < material.passCount; i++)
                {
                    // Most modifiers implement only one capture pass. Compare
                    // IDs so scanning the absent passes does not allocate tag names.
                    if (material.shader.FindPassTagValue(i, LightMode) != expected)
                        continue;
                    pass = i;
                    break;
                }
            }
            return pass;
        }

        private static void CollectInteractionDraws(CameraState state, Bounds captureBounds)
        {
            state.InteractionDraws.Clear();
            state.NextInteractionVersion = VersionSeed;
            IReadOnlyList<GrassColliderInteractor> interactors = GrassColliderInteractor.ActiveInteractors;
            for (int i = 0; i < interactors.Count; i++)
            {
                GrassColliderInteractor interactor = interactors[i];
                if (!interactor || !interactor.TryGetDraw(out Mesh mesh, out Material material, out Bounds bounds) ||
                    !IntersectsXZ(bounds, captureBounds))
                    continue;
                int pass = material.FindPass("GrassSlope");
                if (pass >= 0)
                {
                    state.InteractionDraws.Add(new InteractionDraw(mesh, material, pass));
                    state.NextInteractionVersion = MixVersion(MixVersion(state.NextInteractionVersion,
                        unchecked((uint)interactor.GetEntityId().GetHashCode())), interactor.DrawRevision);
                }
            }
            // An empty relevant set has a stable version. Removing or expiring
            // its last previous draw still changes the version and clears slope;
            // changes wholly outside this camera's window do not recapture it.
        }
    }
}
