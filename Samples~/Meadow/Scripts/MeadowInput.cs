using UnityEngine;
#if ENABLE_INPUT_SYSTEM && CONE_GRASS_SAMPLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Cone.Grass.Samples.Meadow
{
    /// <summary>
    /// Reads movement and camera input from whichever input backend the project has enabled, so the sample runs
    /// with the Input System package, the legacy Input Manager, or both.
    /// </summary>
    internal static class MeadowInput
    {
        /// <summary>WASD or arrow keys, as a vector with x to the right and y forward.</summary>
        public static Vector2 Move()
        {
            Vector2 move = Vector2.zero;
#if ENABLE_INPUT_SYSTEM && CONE_GRASS_SAMPLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move.y += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move.y -= 1f;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move.x += 1f;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move.x -= 1f;
            }
            Gamepad gamepad = Gamepad.current;
            if (gamepad != null)
                move += gamepad.leftStick.ReadValue();
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) move.y += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) move.y -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) move.x += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) move.x -= 1f;
#endif
            return Vector2.ClampMagnitude(move, 1f);
        }

        /// <summary>True while the run key (Left Shift) is held.</summary>
        public static bool Run()
        {
#if ENABLE_INPUT_SYSTEM && CONE_GRASS_SAMPLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKey(KeyCode.LeftShift);
#else
            return false;
#endif
        }

        /// <summary>Mouse movement while the right button is held, in pixels this frame.</summary>
        public static Vector2 Look()
        {
#if ENABLE_INPUT_SYSTEM && CONE_GRASS_SAMPLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;
            Vector2 look = mouse != null && mouse.rightButton.isPressed ? mouse.delta.ReadValue() : Vector2.zero;
            if (Gamepad.current != null)
                look += Gamepad.current.rightStick.ReadValue() * 8f;
            return look;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButton(1) ? new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f : Vector2.zero;
#else
            return Vector2.zero;
#endif
        }
    }
}
