using UnityEngine;

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
        [SerializeField] private Transform cameraTransform;
        [SerializeField, Min(0f)] private float walkSpeed = 1.6f;
        [SerializeField, Min(0f)] private float runSpeed = 4.5f;
        [SerializeField, Min(0f)] private float turnSpeed = 540f;
        [Tooltip("Walk a figure of eight while there is no input.")]
        [SerializeField] private bool autopilot = true;
        [Tooltip("Half the width of the autopilot's figure of eight, in metres.")]
        [SerializeField, Min(1f)] private float autopilotRadius = 9f;
        [Tooltip("Seconds without input before the autopilot takes over again.")]
        [SerializeField, Min(0f)] private float autopilotDelay = 4f;

        private CharacterController controller;
        private Vector3 autopilotCentre;
        private float autopilotPhase, idleTime, verticalSpeed;

        /// <summary>Whether the character is currently walking the autopilot path.</summary>
        public bool AutopilotActive => autopilot && idleTime >= autopilotDelay;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            autopilotCentre = transform.position - Lemniscate(0f);
            idleTime = autopilotDelay;
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            Vector2 input = MeadowInput.Move();
            idleTime = input.sqrMagnitude > 0.01f ? 0f : idleTime + deltaTime;

            Vector3 velocity;
            if (AutopilotActive)
            {
                // A lemniscate around the start point at walking pace.
                autopilotPhase += walkSpeed * deltaTime / (autopilotRadius * 1.9f);
                Vector3 toTarget = autopilotCentre + Lemniscate(autopilotPhase + 0.05f) - transform.position;
                toTarget.y = 0f;
                velocity = toTarget.sqrMagnitude > 1e-4f ? toTarget.normalized * walkSpeed : Vector3.zero;
            }
            else
            {
                Vector3 forward = cameraTransform ? cameraTransform.forward : Vector3.forward;
                forward.y = 0f;
                forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
                var right = new Vector3(forward.z, 0f, -forward.x);
                velocity = (forward * input.y + right * input.x) * (MeadowInput.Run() ? runSpeed : walkSpeed);
                // Resume the figure of eight from wherever the player leaves the character.
                autopilotCentre = transform.position - Lemniscate(autopilotPhase);
            }

            if (velocity.sqrMagnitude > 1e-4f)
            {
                Quaternion facing = Quaternion.LookRotation(velocity, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, facing, turnSpeed * deltaTime);
            }

            verticalSpeed = controller.isGrounded ? -1f : verticalSpeed + Physics.gravity.y * deltaTime;
            velocity.y = verticalSpeed;
            controller.Move(velocity * deltaTime);
        }

        private Vector3 Lemniscate(float phase)
        {
            float s = Mathf.Sin(phase), c = Mathf.Cos(phase);
            float scale = autopilotRadius / (1f + s * s);
            return new Vector3(scale * c, 0f, scale * s * c);
        }
    }
}
