using UnityEngine;
#if ENDLESS_MEADOW_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace EndlessMeadow
{
    [AddComponentMenu("Endless Meadow/Demo Fly Camera")]
    public class DemoFlyCamera : MonoBehaviour
    {
        public Terrain terrain;
        public float eyeHeight = 1.7f;
        public bool walk = true;
        public float speed = 8f;
        public float fastMultiplier = 5f;
        public float mouseSensitivity = 1f;
        public bool showHelp = true;

        // Current camera angles in degrees.
        float yaw;   // left / right
        float pitch; // up / down

        void Start()
        {
            // Start from the angles the camera has in the scene.
            Vector3 angles = transform.eulerAngles;
            yaw = angles.y;
            pitch = Mathf.DeltaAngle(0f, angles.x);
        }

        void Update()
        {
            LookAround();
            Move();
            HandleShortcuts();
            StayAboveGround();
        }

        // ---------------------------------------------------------------- Camera

        void LookAround()
        {
            // Only turn while the right mouse button is held, so the cursor is free otherwise.
            if (!IsRightMouseHeld())
                return;

            Vector2 mouse = GetMouseMovement() * mouseSensitivity;
            yaw += mouse.x;
            pitch -= mouse.y;
            pitch = Mathf.Clamp(pitch, -80f, 80f); // do not flip over

            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        void Move()
        {
            // Forward without the up/down part, so looking down does not push the camera into the ground.
            Vector3 forward = transform.forward;
            forward.y = 0f;
            forward.Normalize();
            Vector3 right = transform.right;

            Vector3 direction = Vector3.zero;
            if (IsKeyHeld("W")) direction += forward;
            if (IsKeyHeld("S")) direction -= forward;
            if (IsKeyHeld("D")) direction += right;
            if (IsKeyHeld("A")) direction -= right;

            // Q / E fly down and up, and stop walking.
            if (IsKeyHeld("E"))
            {
                direction += Vector3.up;
                walk = false;
            }
            if (IsKeyHeld("Q"))
            {
                direction -= Vector3.up;
                walk = false;
            }

            float currentSpeed = speed;
            if (IsKeyHeld("Shift"))
                currentSpeed *= fastMultiplier;

            transform.position += direction * currentSpeed * Time.deltaTime;
        }

        void HandleShortcuts()
        {
            if (IsKeyPressed("G"))
                walk = true;

            if (IsKeyPressed("F1"))
                showHelp = !showHelp;

            if (IsKeyPressed("F12"))
            {
                string fileName = "EndlessMeadow_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                ScreenCapture.CaptureScreenshot(fileName);
                Debug.Log("Screenshot saved: " + fileName);
            }
        }

        void StayAboveGround()
        {
            if (terrain == null)
                return;

            Vector3 position = transform.position;
            float groundHeight = terrain.SampleHeight(position) + terrain.GetPosition().y;

            if (walk)
                position.y = groundHeight + eyeHeight;                     // stick to the ground
            else
                position.y = Mathf.Max(position.y, groundHeight + 0.5f);  // fly, but never below the ground

            transform.position = position;
        }

        // ---------------------------------------------------------------- Help text

        void OnGUI()
        {
            if (!showHelp)
                return;

            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = 13;
            GUI.Label(new Rect(14, 10, 700, 24),
                "Right mouse: look    WASD: move    Q/E: down/up    Shift: fast    G: walk    F12: screenshot    F1: hide",
                style);
        }

        // ---------------------------------------------------------------- Input
        // The rest of the script only uses these four helpers, so it does not need to know
        // which input system the project uses.

#if ENDLESS_MEADOW_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        // New Input System package.

        static Key ToKey(string name)
        {
            switch (name)
            {
                case "W": return Key.W;
                case "A": return Key.A;
                case "S": return Key.S;
                case "D": return Key.D;
                case "Q": return Key.Q;
                case "E": return Key.E;
                case "G": return Key.G;
                case "F1": return Key.F1;
                case "F12": return Key.F12;
                default: return Key.LeftShift; // "Shift"
            }
        }

        static bool IsKeyHeld(string name)
        {
            return Keyboard.current != null && Keyboard.current[ToKey(name)].isPressed;
        }

        static bool IsKeyPressed(string name)
        {
            return Keyboard.current != null && Keyboard.current[ToKey(name)].wasPressedThisFrame;
        }

        static bool IsRightMouseHeld()
        {
            return Mouse.current != null && Mouse.current.rightButton.isPressed;
        }

        static Vector2 GetMouseMovement()
        {
            if (Mouse.current == null)
                return Vector2.zero;
            return Mouse.current.delta.ReadValue() * 0.1f;
        }

#elif ENABLE_LEGACY_INPUT_MANAGER
        // Old Input Manager.

        static KeyCode ToKey(string name)
        {
            switch (name)
            {
                case "W": return KeyCode.W;
                case "A": return KeyCode.A;
                case "S": return KeyCode.S;
                case "D": return KeyCode.D;
                case "Q": return KeyCode.Q;
                case "E": return KeyCode.E;
                case "G": return KeyCode.G;
                case "F1": return KeyCode.F1;
                case "F12": return KeyCode.F12;
                default: return KeyCode.LeftShift; // "Shift"
            }
        }

        static bool IsKeyHeld(string name)
        {
            return Input.GetKey(ToKey(name));
        }

        static bool IsKeyPressed(string name)
        {
            return Input.GetKeyDown(ToKey(name));
        }

        static bool IsRightMouseHeld()
        {
            return Input.GetMouseButton(1);
        }

        static Vector2 GetMouseMovement()
        {
            return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 2f;
        }

#else
        // No input system enabled: the camera stays still.
        static bool IsKeyHeld(string name) { return false; }
        static bool IsKeyPressed(string name) { return false; }
        static bool IsRightMouseHeld() { return false; }
        static Vector2 GetMouseMovement() { return Vector2.zero; }
#endif
    }
}
