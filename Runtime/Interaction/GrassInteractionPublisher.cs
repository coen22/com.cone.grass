using UnityEngine;

namespace Cone.Grass
{
    /// <summary>
    /// Publishes the grass interaction capture as the one interaction field other effects read (com.cone.render-features'
    /// ground fog, render-features #4), so the field is simulated and captured once. The globals are the field texture
    /// (rg the push direction as dir * 0.5 + 0.5 premultiplied by a, a the contact strength), the world-xz-to-uv scale
    /// and offset, and a published flag. Grass needs nothing from the readers.
    /// </summary>
    public static class GrassInteractionPublisher
    {
        /// <summary>Shader property ID of the global interaction field texture, <c>_ConeInteractionField</c>.</summary>
        public static readonly int FieldId = Shader.PropertyToID("_ConeInteractionField");
        /// <summary>Shader property ID of the global world-xz-to-uv scale and offset, <c>_ConeInteractionFieldST</c>.</summary>
        public static readonly int ScaleOffsetId = Shader.PropertyToID("_ConeInteractionFieldST");
        /// <summary>Shader property ID of the global parameters, <c>_ConeInteractionFieldParams</c>; x is 1 while a field is published.</summary>
        public static readonly int ParamsId = Shader.PropertyToID("_ConeInteractionFieldParams");

        /// <summary>
        /// The capture's world xz to uv, as GrassWorldToMapUV: uv = (xz - center) / (2 extent) + 0.5.
        /// </summary>
        /// <param name="center">World xz centre of the capture.</param>
        /// <param name="extent">Half the capture's width in metres; clamped to at least 0.001.</param>
        /// <returns>The scale in xy and the offset in zw.</returns>
        public static Vector4 ScaleOffset(Vector2 center, float extent)
        {
            float scale = 0.5f / Mathf.Max(extent, 0.001f);
            return new Vector4(scale, scale, 0.5f - center.x * scale, 0.5f - center.y * scale);
        }

        /// <summary>
        /// Sets the global field texture, its mapping and the published flag. A null field withdraws the field instead.
        /// </summary>
        /// <param name="field">The interaction capture texture.</param>
        /// <param name="center">World xz centre of the capture.</param>
        /// <param name="extent">Half the capture's width in metres.</param>
        public static void Publish(Texture field, Vector2 center, float extent)
        {
            if (field == null)
            {
                Withdraw();
                return;
            }

            Shader.SetGlobalTexture(FieldId, field);
            Shader.SetGlobalVector(ScaleOffsetId, ScaleOffset(center, extent));
            Shader.SetGlobalVector(ParamsId, new Vector4(1f, 0f, 0f, 0f));
        }

        /// <summary>Readers see no field until the next capture publishes one.</summary>
        public static void Withdraw() => Shader.SetGlobalVector(ParamsId, Vector4.zero);
    }
}
