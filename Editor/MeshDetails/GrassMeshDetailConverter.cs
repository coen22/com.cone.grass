using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Cone.Grass.Editor
{
    /// <summary>
    /// Turns terrains' detail mesh prototypes into <see cref="GrassMeshDetails"/> mesh kinds and turns Unity's own detail
    /// drawing off for them. The terrain data is not changed: the kinds read its detail density layers, and only the
    /// terrains' Detail Distance is set to 0 (recorded, so <see cref="RestoreUnityDetails"/> can put it back).
    /// </summary>
    public static class GrassMeshDetailConverter
    {
        private const string MenuPath = "Tools/Cone/Grass/Convert Terrain Details to Mesh Kinds";

        /// <summary>
        /// Builds mesh kinds from every detail mesh prototype of <paramref name="terrains"/>, sharing one kind between
        /// terrains whose prototypes match, and adds them to <paramref name="target"/> (a new "Grass Mesh Details" object
        /// when null). With <paramref name="disableUnityDetails"/>, a terrain whose painted layers are all converted stops
        /// drawing its own details.
        /// </summary>
        public static GrassMeshDetails Convert(IReadOnlyList<Terrain> terrains, GrassMeshDetails target = null, bool disableUnityDetails = true)
        {
            if (terrains == null || terrains.Count == 0)
            {
                return target;
            }

            if (!target)
            {
                var go = new GameObject("Grass Mesh Details");
                Undo.RegisterCreatedObjectUndo(go, "Convert Terrain Details");
                target = Undo.AddComponent<GrassMeshDetails>(go);
            }
            else
            {
                Undo.RecordObject(target, "Convert Terrain Details");
            }

            List<GrassMeshKind> kinds = target.Kinds;
            // Kinds already on the target keep their place; new prototypes are matched by key.
            var keys = new List<string>();
            for (int i = 0; i < kinds.Count; i++)
            {
                keys.Add(null);
            }

            List<GrassMeshDetailTerrain> entries = target.Terrains;
            foreach (Terrain terrain in terrains)
            {
                TerrainData data = terrain ? terrain.terrainData : null;
                if (!data)
                {
                    continue;
                }

                DetailPrototype[] prototypes = data.detailPrototypes;
                var layerKinds = new int[prototypes.Length];
                bool everyPaintedLayerConverted = true;
                for (int layer = 0; layer < prototypes.Length; layer++)
                {
                    layerKinds[layer] = -1;
                    string key = Key(prototypes[layer], terrain.detailObjectDistance);
                    GrassMeshKind kind = key == null ? null : GrassMeshKind.FromDetailPrototype(prototypes[layer], terrain.detailObjectDistance);
                    if (kind == null)
                    {
                        everyPaintedLayerConverted &= !IsPainted(data, layer);
                        continue;
                    }

                    int index = keys.IndexOf(key);
                    if (index < 0)
                    {
                        index = kinds.Count;
                        kinds.Add(kind);
                        keys.Add(key);
                    }

                    layerKinds[layer] = index;
                }

                GrassMeshDetailTerrain entry = entries.Find(e => e.Terrain == terrain);
                if (entry == null)
                {
                    entry = new GrassMeshDetailTerrain { Terrain = terrain };
                    entries.Add(entry);
                }

                entry.LayerKinds = layerKinds;
                if (!disableUnityDetails)
                {
                    continue;
                }

                if (everyPaintedLayerConverted)
                {
                    if (terrain.detailObjectDistance > 0f)
                    {
                        Undo.RecordObject(terrain, "Turn Off Unity Terrain Details");
                        entry.OriginalDetailDistance = terrain.detailObjectDistance;
                        terrain.detailObjectDistance = 0f;
                        EditorUtility.SetDirty(terrain);
                    }
                }
                else
                {
                    Debug.LogWarning($"Terrain '{terrain.name}' also paints detail layers that are not meshes (grass textures), " +
                        "so Unity's own detail drawing stays on and its mesh layers draw twice. Remove those layers or convert them by hand.", terrain);
                }
            }

            target.Kinds = kinds;
            target.Terrains = entries;
            EditorUtility.SetDirty(target);
            return target;
        }

        /// <summary>Puts back the Detail Distance of every terrain the converter turned off, so Unity draws its details again.</summary>
        public static void RestoreUnityDetails(GrassMeshDetails target)
        {
            if (!target)
            {
                return;
            }

            Undo.RecordObject(target, "Restore Unity Terrain Details");
            foreach (GrassMeshDetailTerrain entry in target.Terrains)
            {
                if (entry.Terrain && entry.OriginalDetailDistance >= 0f)
                {
                    Undo.RecordObject(entry.Terrain, "Restore Unity Terrain Details");
                    entry.Terrain.detailObjectDistance = entry.OriginalDetailDistance;
                    entry.OriginalDetailDistance = -1f;
                    EditorUtility.SetDirty(entry.Terrain);
                }
            }

            EditorUtility.SetDirty(target);
        }

        /// <summary>Adds the Grass Mesh Detail Renderer Feature to the active URP renderer, or enables it. Null without URP.</summary>
        public static GrassMeshDetailFeature InstallFeature()
        {
            UniversalRendererData renderer = GrassRendererSetup.ActiveRendererData();
            return renderer ? InstallFeature(renderer) : null;
        }

        /// <summary>Adds the Grass Mesh Detail Renderer Feature to <paramref name="renderer"/>, or enables the one it has.</summary>
        public static GrassMeshDetailFeature InstallFeature(UniversalRendererData renderer)
        {
            foreach (ScriptableRendererFeature existing in renderer.rendererFeatures)
            {
                if (existing is GrassMeshDetailFeature found)
                {
                    if (!found.isActive)
                    {
                        Undo.RecordObject(found, "Enable Grass Mesh Detail Renderer Feature");
                        found.SetActive(true);
                        EditorUtility.SetDirty(renderer);
                        AssetDatabase.SaveAssetIfDirty(renderer);
                    }

                    return found;
                }
            }

            // As URP's own Add Renderer Feature: a hidden sub-asset named after its type, with Undo.
            var feature = ScriptableObject.CreateInstance<GrassMeshDetailFeature>();
            feature.name = nameof(GrassMeshDetailFeature);
            feature.hideFlags |= HideFlags.HideInHierarchy;
            Undo.RegisterCreatedObjectUndo(feature, "Add Grass Mesh Detail Renderer Feature");
            if (EditorUtility.IsPersistent(renderer))
            {
                AssetDatabase.AddObjectToAsset(feature, renderer);
            }

            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
            using (var serialized = new SerializedObject(renderer))
            {
                SerializedProperty features = serialized.FindProperty("m_RendererFeatures");
                SerializedProperty map = serialized.FindProperty("m_RendererFeatureMap");
                features.arraySize++;
                features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
                map.arraySize++;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                serialized.ApplyModifiedProperties();
            }

            feature.Create();
            EditorUtility.SetDirty(renderer);
            if (EditorUtility.IsPersistent(renderer))
            {
                AssetDatabase.SaveAssetIfDirty(renderer);
            }

            return feature;
        }

        // Prototypes that would build the same kind share it: the same prefab and the same settings.
        private static string Key(DetailPrototype prototype, float distance)
        {
            if (prototype == null || !prototype.usePrototypeMesh || !prototype.prototype)
            {
                return null;
            }

            return string.Join("|", prototype.prototype.GetEntityId().ToString(), prototype.density, prototype.useDensityScaling,
                prototype.minWidth, prototype.maxWidth, prototype.minHeight, prototype.maxHeight, prototype.noiseSpread,
                prototype.noiseSeed, prototype.alignToGround, prototype.positionJitter, prototype.useInstancing,
                prototype.healthyColor, prototype.dryColor, distance);
        }

        private static bool IsPainted(TerrainData data, int layer)
        {
            int resolution = data.detailResolution;
            int[,] values = data.GetDetailLayer(0, 0, resolution, resolution, layer);
            foreach (int value in values)
            {
                if (value > 0)
                {
                    return true;
                }
            }

            return false;
        }

        [MenuItem(MenuPath)]
        private static void ConvertFromMenu()
        {
            var terrains = new List<Terrain>();
            foreach (Object selected in Selection.objects)
            {
                if (selected is GameObject go && go.TryGetComponent(out Terrain terrain))
                {
                    terrains.Add(terrain);
                }
            }

            if (terrains.Count == 0)
            {
                terrains.AddRange(Terrain.activeTerrains);
            }

            GrassMeshDetails target = Object.FindFirstObjectByType<GrassMeshDetails>();
            target = Convert(terrains, target);
            GrassMeshDetailFeature feature = InstallFeature();
            Selection.activeObject = target;
            Debug.Log($"Grass mesh details: {target.Kinds.Count} kinds on {target.Terrains.Count} terrains" +
                (feature ? "; the Grass Mesh Detail Renderer Feature is on the active renderer." : "; URP is not active: add the Grass Mesh Detail Renderer Feature by hand."), target);
        }
    }
}
