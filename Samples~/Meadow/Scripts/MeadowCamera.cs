using UnityEngine;

namespace Cone.Grass.Samples.Meadow
{
    /// <summary>
    /// A third-person camera that follows the sample's character from behind its shoulder. Hold the right mouse button
    /// (or use the right stick) to orbit.
    /// </summary>
    [AddComponentMenu("Cone/Grass/Samples/Meadow Camera")]
    public sealed class MeadowCamera : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [Tooltip("Height of the orbit pivot above the target's feet.")]
        [SerializeField] private float pivotHeight = 1.45f;
        [SerializeField, Min(0.5f)] private float distance = 3.6f;
        [SerializeField, Range(-30f, 80f)] private float pitch = 18f;
        [Tooltip("Degrees per pixel of mouse movement.")]
        [SerializeField, Min(0f)] private float sensitivity = 0.15f;
        [Tooltip("How quickly the camera swings behind the character while the player is not orbiting.")]
        [SerializeField, Min(0f)] private float followYaw = 1.5f;

        private float yaw;
        private float lastLookTime = float.NegativeInfinity;

        /// <summary>The character the camera follows.</summary>
        public Transform Target
        {
            get => target;
            set => target = value;
        }

        private void Start()
        {
            if (target)
                yaw = target.eulerAngles.y;
        }

        private void LateUpdate()
        {
            if (!target)
                return;
            Vector2 look = MeadowInput.Look();
            if (look.sqrMagnitude > 0f)
            {
                yaw += look.x * sensitivity;
                pitch = Mathf.Clamp(pitch - look.y * sensitivity, -30f, 80f);
                lastLookTime = Time.time;
            }
            else if (Time.time - lastLookTime > 2f)
            {
                yaw = Mathf.LerpAngle(yaw, target.eulerAngles.y, 1f - Mathf.Exp(-followYaw * Time.deltaTime));
            }

            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 pivot = target.position + Vector3.up * pivotHeight;
            transform.SetPositionAndRotation(pivot - rotation * Vector3.forward * distance, rotation);
        }
    }
}
