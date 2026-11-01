using UnityEngine;
using UnityEngine.Serialization;

namespace Cone.Grass.Samples.Meadow
{
    /// <summary>
    /// Moves the sample's character through the meadow with a <see cref="CharacterController"/>. Its collider is the
    /// one a <see cref="GrassColliderInteractor"/> on the same object bends the grass with. Without input the character
    /// walks a slow figure of eight on its own, so the bending shows without touching the keyboard.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [AddComponentMenu("Cone/Grass/Samples/Meadow Walker")]
    public sealed class MeadowWalker : MonoBehaviour
    {
        [Tooltip("Camera whose yaw turns the keyboard directions. Empty uses world axes.")]
        [SerializeField, FormerlySerializedAs("cameraTransform")] private Transform _cameraTransform;
        [SerializeField, Min(0f), FormerlySerializedAs("walkSpeed")] private float _walkSpeed = 1.6f;
        [SerializeField, Min(0f), FormerlySerializedAs("runSpeed")] private float _runSpeed = 4.5f;
        [SerializeField, Min(0f), FormerlySerializedAs("turnSpeed")] private float _turnSpeed = 540f;
        [Tooltip("Walk a figure of eight while there is no input.")]
        [SerializeField, FormerlySerializedAs("autopilot")] private bool _autopilot = true;
        [Tooltip("Half the width of the autopilot's figure of eight, in metres.")]
        [SerializeField, Min(1f), FormerlySerializedAs("autopilotRadius")] private float _autopilotRadius = 9f;
        [Tooltip("Seconds without input before the autopilot takes over again.")]
        [SerializeField, Min(0f), FormerlySerializedAs("autopilotDelay")] private float _autopilotDelay = 4f;

        private CharacterController _controller;
        private Vector3 _autopilotCentre;
        private float _autopilotPhase, _idleTime, _verticalSpeed;

        /// <summary>Whether the character is currently walking the autopilot path.</summary>
        public bool AutopilotActive => _autopilot && _idleTime >= _autopilotDelay;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _autopilotCentre = transform.position - Lemniscate(0f);
            _idleTime = _autopilotDelay;
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            Vector2 input = MeadowInput.Move();
            _idleTime = input.sqrMagnitude > 0.01f ? 0f : _idleTime + deltaTime;

            Vector3 velocity;
            if (AutopilotActive)
            {
                // A lemniscate around the start point at walking pace.
                _autopilotPhase += _walkSpeed * deltaTime / (_autopilotRadius * 1.9f);
                Vector3 toTarget = _autopilotCentre + Lemniscate(_autopilotPhase + 0.05f) - transform.position;
                toTarget.y = 0f;
                velocity = toTarget.sqrMagnitude > 1e-4f ? toTarget.normalized * _walkSpeed : Vector3.zero;
            }
            else
            {
                Vector3 forward = _cameraTransform ? _cameraTransform.forward : Vector3.forward;
                forward.y = 0f;
                forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
                var right = new Vector3(forward.z, 0f, -forward.x);
                velocity = (forward * input.y + right * input.x) * (MeadowInput.Run() ? _runSpeed : _walkSpeed);
                // Resume the figure of eight from wherever the player leaves the character.
                _autopilotCentre = transform.position - Lemniscate(_autopilotPhase);
            }

            if (velocity.sqrMagnitude > 1e-4f)
            {
                Quaternion facing = Quaternion.LookRotation(velocity, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, facing, _turnSpeed * deltaTime);
            }

            _verticalSpeed = _controller.isGrounded ? -1f : _verticalSpeed + Physics.gravity.y * deltaTime;
            velocity.y = _verticalSpeed;
            _controller.Move(velocity * deltaTime);
        }

        private Vector3 Lemniscate(float phase)
        {
            float s = Mathf.Sin(phase), c = Mathf.Cos(phase);
            float scale = _autopilotRadius / (1f + s * s);
            return new Vector3(scale * c, 0f, scale * s * c);
        }
    }
}
