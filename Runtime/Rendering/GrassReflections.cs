using System;
using UnityEngine;

namespace Cone.Grass
{
    /// <summary>Which reflection cameras (<see cref="CameraType.Reflection"/>) draw the grass.</summary>
    public enum GrassReflectionCameras
    {
        /// <summary>No reflection camera draws grass.</summary>
        None = 0,

        /// <summary>
        /// Planar reflection cameras, such as Crest's water reflections. Reflection-probe captures (square, 90° cube
        /// faces) are left out, so a realtime probe doesn't pay for the grass.
        /// </summary>
        Planar = 1,

        /// <summary>Every reflection camera, reflection-probe captures included.</summary>
        All = 2,
    }

    /// <summary>When the grass draws relative to URP's depth and opaque texture copies.</summary>
    public enum GrassDrawOrder
    {
        /// <summary>
        /// Just before transparents, after URP copies the depth and opaque textures. Contact shadows and motion vectors
        /// are available; effects that read those textures (water refraction, Crest's underwater fog) don't see the grass.
        /// </summary>
        AfterOpaqueCopies = 0,

        /// <summary>
        /// Right after the opaques, before URP copies the depth and opaque textures, so the grass is in both: water seen
        /// from above refracts it and Crest's underwater effect fogs it. Contact shadows and grass motion vectors need
        /// those textures before the grass and are skipped in this order.
        /// </summary>
        BeforeOpaqueCopies = 1,
    }

    /// <summary>
    /// The cheaper path the grass takes in reflection cameras. Serialized on <see cref="InfiniteGrassRenderer.Reflections"/>.
    /// </summary>
    [Serializable]
    public sealed class GrassReflectionSettings
    {
        [Tooltip("Which reflection cameras draw grass. Planar leaves out reflection-probe captures."), SerializeField]
        private GrassReflectionCameras _cameras = GrassReflectionCameras.Planar;
        [Tooltip("Spacing multiplier in reflections: 2 draws a quarter of the blades."), Range(1f, 8f), SerializeField]
        private float _spacingScale = 2f;
        [Tooltip("Draw distance multiplier in reflections."), Range(0.05f, 1f), SerializeField]
        private float _drawDistanceScale = 0.5f;

        /// <summary>Which reflection cameras draw grass.</summary>
        public GrassReflectionCameras Cameras { get => _cameras; set => _cameras = value; }

        /// <summary>
        /// Spacing multiplier in reflection cameras, 1 to 8. The blade count falls with its square: 2 draws a quarter.
        /// </summary>
        public float SpacingScale { get => _spacingScale; set => _spacingScale = value; }

        /// <summary>Draw distance multiplier in reflection cameras, 0.05 to 1.</summary>
        public float DrawDistanceScale { get => _drawDistanceScale; set => _drawDistanceScale = value; }

        /// <summary>The spacing multiplier, clamped to 1 to 8; a non-finite value is 1.</summary>
        public float EffectiveSpacingScale => float.IsNaN(_spacingScale) || float.IsInfinity(_spacingScale) ? 1f : Mathf.Clamp(_spacingScale, 1f, 8f);

        /// <summary>The draw distance multiplier, clamped to 0.05 to 1; a non-finite value is 1.</summary>
        public float EffectiveDrawDistanceScale => float.IsNaN(_drawDistanceScale) || float.IsInfinity(_drawDistanceScale) ? 1f : Mathf.Clamp(_drawDistanceScale, 0.05f, 1f);

        /// <summary>Whether this reflection camera draws grass.</summary>
        public bool Admits(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Reflection)
            {
                return false;
            }

            return _cameras == GrassReflectionCameras.All || _cameras == GrassReflectionCameras.Planar && !IsProbeCapture(camera);
        }

        /// <summary>A reflection-probe capture renders square 90° cube faces.</summary>
        public static bool IsProbeCapture(Camera camera) =>
            camera != null && !camera.orthographic && Mathf.Abs(camera.fieldOfView - 90f) < 0.01f && Mathf.Abs(camera.aspect - 1f) < 0.001f;
    }
}
