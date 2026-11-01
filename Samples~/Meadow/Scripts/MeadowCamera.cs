using UnityEngine;
using UnityEngine.Serialization;

namespace Cone.Grass.Samples.Meadow
{
    /// <summary>
    /// A third-person camera that follows the sample's character from behind its shoulder. Hold the right mouse button
    /// (or use the right stick) to orbit.
    /// </summary>
    [AddComponentMenu("Cone/Grass/Samples/Meadow Camera")]
    public sealed class MeadowCamera : MonoBehaviour
    {
        [SerializeField, FormerlySerializedAs("target")] private Transform _target;
        [Tooltip("Height of the orbit pivot above the target's feet.")]
        [SerializeField, FormerlySerializedAs("pivotHeight")] private readonly float _pivotHeight = 1.45f;
        [SerializeField, Min(0.5f), FormerlySerializedAs("distance")] private readonly float _distance = 3.6f;
        [SerializeField, Range(-30f, 80f), FormerlySerializedAs("pitch")] private float _pitch = 18f;
        [Tooltip("Degrees per pixel of mouse movement.")]
        [SerializeField, Min(0f), FormerlySerializedAs("sensitivity")] private readonly float _sensitivity = 0.15f;
        [Tooltip("How quickly the camera swings behind the character while the player is not orbiting.")]
        [SerializeField, Min(0f), FormerlySerializedAs("followYaw")] private readonly float _followYaw = 1.5f;

        private float _yaw;
        private float _lastLookTime = float.NegativeInfinity;

        /// <summary>The character the camera follows.</summary>
        public Transform Target
        {
            get => _target;
            set => _target = value;
        }

        private void Start()
        {
            if (_target)
            {
                _yaw = _target.eulerAngles.y;
            }
        }

        private void LateUpdate()
        {
            if (!_target)
            {
                return;
            }

            Vector2 look = MeadowInput.Look();
            if (look.sqrMagnitude > 0f)
            {
                _yaw += look.x * _sensitivity;
                _pitch = Mathf.Clamp(_pitch - look.y * _sensitivity, -30f, 80f);
                _lastLookTime = Time.time;
            }
            else if (Time.time - _lastLookTime > 2f)
            {
                _yaw = Mathf.LerpAngle(_yaw, _target.eulerAngles.y, 1f - Mathf.Exp(-_followYaw * Time.deltaTime));
            }

            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 pivot = _target.position + Vector3.up * _pivotHeight;
            transform.SetPositionAndRotation(pivot - rotation * Vector3.forward * _distance, rotation);
        }
    }
}
