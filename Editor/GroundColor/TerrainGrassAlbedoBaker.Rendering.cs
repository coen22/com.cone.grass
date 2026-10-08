using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static partial class TerrainGrassAlbedoBaker
{
    /// <summary>Capture caller-owned albedo without creating, saving or notifying an asset.</summary>
    public static bool TryBakeTransient(Terrain terrain, int resolution, out Texture2D texture, out string error)
    {
        texture = null;
        if (!TryGetInputs(terrain, resolution, out Material source, out TerrainLayer[] layers, out error) ||
            !TryGetCaptureShader(out Shader shader, out error))
            return false;
        if (!TryRenderAlbedo(terrain, resolution, source, layers, shader, "Terrain grass albedo", out texture, out error))
            return false;
        texture.hideFlags = HideFlags.DontSave;
        return true;
    }

    private static bool TryGetCaptureShader(out Shader shader, out string error)
    {
        shader = null;
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
            !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ||
            !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf))
        {
            error = "Ground-albedo baking needs an Editor graphics device with RGBAHalf render and texture support.";
            return false;
        }

        shader = Shader.Find(BakeShaderName);
        if (!shader || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
        {
            error = "The grass TerrainLit albedo bake shader is unavailable or failed to compile. Check the Unity shader console.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryBakeOutput(Terrain terrain, int resolution, string assetPath, Material source,
        TerrainLayer[] layers, Texture2D existing, Shader shader, out Texture2D texture, out string error)
    {
        texture = null;
        if (!TryRenderAlbedo(terrain, resolution, source, layers, shader,
            existing ? existing.name : Path.GetFileNameWithoutExtension(assetPath), out Texture2D staging, out error))
            return false;
        try
        {
            if (existing)
            {
                Undo.RegisterCompleteObjectUndo(existing, "Bake terrain grass ground albedo");
                EditorUtility.CopySerialized(staging, existing);
                EditorUtility.SetDirty(existing);
                texture = existing;
            }
            else
            {
                // Saving can reenter authoring callbacks; keep the existing output guard.
                activeBakes[assetPath] = staging;
                AssetDatabase.CreateAsset(staging, assetPath);
                texture = staging;
                staging = null;
            }
            AssetDatabase.SaveAssetIfDirty(texture);
        }
        catch (Exception exception)
        {
            error = "Could not bake terrain ground albedo: " + exception.Message;
            texture = null;
            return false;
        }
        finally { if (staging) Object.DestroyImmediate(staging); }

        return CompleteSavedBake(terrain, ref texture, out error);
    }

    private static bool CompleteSavedBake(Terrain terrain, ref Texture2D texture, out string error)
    {
        if (!TryGetOutputAssetPath(texture, out _))
        {
            texture = null;
            error = "The ground-albedo output was removed or replaced while being saved. Refresh to bake again.";
            return false;
        }
        // Callbacks may edit this same asset, not just replace it. Keep a cheap
        // snapshot of the generated image's reported state before either public
        // notification path. Names and paths are deliberately not image state:
        // moving the same saved output remains supported. GPU writers must
        // report their writes with IncrementUpdateCount; no pixels are scanned.
        BakedOutputState generated = new BakedOutputState(texture);
        NotifyBoundAreas(texture);
        NotifyBaked(terrain, texture);
        // Observers can move an output, but deleting, detaching or replacing it
        // cannot turn a lost image into a successfully persisted bake result.
        if (!TryGetOutputAssetPath(texture, out _))
        {
            texture = null;
            error = "The ground-albedo output was removed or replaced by a completion callback. Refresh to bake again.";
            return false;
        }
        if (!generated.Matches(texture))
        {
            // Preserve the observer's edits, but do not let a caller record
            // their pixels/settings as the native albedo just generated above.
            texture = null;
            error = "The ground-albedo output changed during a completion callback. Refresh to bake again after the callback finishes editing it.";
            return false;
        }
        error = null;
        return true;
    }

    private static Material CaptureMaterial(Material source, TerrainLayer[] layers, Shader shader)
    {
        var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        if (source.IsKeywordEnabled("_TERRAIN_BLEND_HEIGHT")) material.EnableKeyword("_TERRAIN_BLEND_HEIGHT");
        foreach (TerrainLayer layer in layers)
            if (layer.maskMapTexture) { material.EnableKeyword("_MASKMAP"); break; }
        return material;
    }

    private static bool TryRenderAlbedo(Terrain terrain, int resolution, Material source,
        TerrainLayer[] layers, Shader shader, string name, out Texture2D texture, out string error)
    {
        texture = null;
        Material material = null;
        RenderTexture capture = null;
        CommandBuffer commands = null;
        RenderTexture previousActive = RenderTexture.active;
        bool previousSRGBWrite = GL.sRGBWrite;
        bool complete = false;
        try
        {
            material = CaptureMaterial(source, layers, shader);
            ShaderUtil.CompilePass(material, 0, true);
            if (ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("The TerrainLit albedo bake shader failed to compile.");
            capture = RenderTexture.GetTemporary(resolution, resolution, 0,
                RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            capture.filterMode = FilterMode.Bilinear;
            capture.wrapMode = TextureWrapMode.Clamp;
            commands = new CommandBuffer { name = "Bake terrain grass ground albedo" };
            commands.SetRenderTarget(capture);
            commands.SetViewport(new Rect(0, 0, resolution, resolution));
            commands.ClearRenderTarget(false, true, Color.clear);
            for (int group = 0; group < (layers.Length + 3) / 4; group++)
                commands.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1,
                    BuildGroupProperties(terrain.terrainData, source, layers, group));
            GL.sRGBWrite = false;
            Graphics.ExecuteCommandBuffer(commands);
            RenderTexture.active = capture;
            texture = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, true, true)
                { name = name, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 1 };
            texture.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0, false);
            texture.Apply(true, false);
            complete = true;
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = "Could not bake terrain ground albedo: " + exception.Message;
            return false;
        }
        finally
        {
            RenderTexture.active = previousActive;
            GL.sRGBWrite = previousSRGBWrite;
            commands?.Release();
            if (capture) RenderTexture.ReleaseTemporary(capture);
            if (material) Object.DestroyImmediate(material);
            if (!complete && texture) { Object.DestroyImmediate(texture); texture = null; }
        }
    }

    private static MaterialPropertyBlock BuildGroupProperties(TerrainData data, Material source,
        TerrainLayer[] layers, int group)
    {
        MaterialPropertyBlock properties = new MaterialPropertyBlock();
        Texture2D control = data.GetAlphamapTexture(group);
        properties.SetTexture("_Control", control);
        properties.SetVector("_Control_TexelSize", new Vector4(1f / control.width, 1f / control.height,
            control.width, control.height));
        properties.SetFloat("_NumLayersCount", layers.Length);
        properties.SetFloat("_HeightTransition", source.GetFloat("_HeightTransition"));
        properties.SetFloat("_BakeAdditionalGroup", group > 0 ? 1f : 0f);
        for (int slot = 0; slot < 4; slot++)
        {
            int index = group * 4 + slot;
            TerrainLayer layer = index < layers.Length ? layers[index] : null;
            string suffix = slot.ToString();
            Texture2D diffuse = layer && layer.diffuseTexture ? layer.diffuseTexture : Texture2D.grayTexture;
            Texture2D mask = layer && layer.maskMapTexture ? layer.maskMapTexture : Texture2D.grayTexture;
            properties.SetTexture("_Splat" + suffix, diffuse);
            properties.SetTexture("_Mask" + suffix, mask);
            properties.SetFloat("_LayerHasMask" + suffix, layer && layer.maskMapTexture ? 1f : 0f);
            properties.SetVector("_Splat" + suffix + "_ST", layer ?
                new Vector4(data.size.x / layer.tileSize.x, data.size.z / layer.tileSize.y,
                    layer.tileOffset.x / layer.tileSize.x, layer.tileOffset.y / layer.tileSize.y) :
                new Vector4(1f, 1f, 0f, 0f));
            properties.SetVector("_DiffuseRemapScale" + suffix,
                layer ? layer.diffuseRemapMax - layer.diffuseRemapMin : Vector4.zero);
            properties.SetVector("_MaskMapRemapScale" + suffix,
                layer ? layer.maskMapRemapMax - layer.maskMapRemapMin : Vector4.one);
            properties.SetVector("_MaskMapRemapOffset" + suffix,
                layer ? layer.maskMapRemapMin : Vector4.zero);
        }
        return properties;
    }

}
