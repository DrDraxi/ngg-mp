// Compile-time stub of the Input System package (Unity.InputSystem.dll): only what InputManager.input (PlayerInput)
// and the test harness's key presses (Keyboard.current, Key, KeyboardState, InputSystem.QueueStateEvent) need, with
// well-known public members of the real package. Never shipped.
using UnityEngine;

namespace UnityEngine.InputSystem
{
    public class PlayerInput : MonoBehaviour
    {
        public InputActionAsset actions { get { throw null; } set { throw null; } }
    }

    public class InputActionAsset : ScriptableObject
    {
        public InputAction this[string actionNameOrId] { get { throw null; } }
        public InputAction FindAction(string actionNameOrId, bool throwIfNotFound) { throw null; }
    }

    public sealed class InputAction
    {
        public string name { get { throw null; } }
        public bool enabled { get { throw null; } }
        public void Enable() { throw null; }
        public void Disable() { throw null; }
        public bool IsPressed() { throw null; }
        public bool WasPressedThisFrame() { throw null; }
        public bool WasReleasedThisFrame() { throw null; }
        public TValue ReadValue<TValue>() where TValue : struct { throw null; }
    }

    public class InputControl
    {
        public string name { get { throw null; } }
        public string displayName { get { throw null; } }
    }

    public class InputDevice : InputControl
    {
        public bool enabled { get { throw null; } }
    }

    public class Keyboard : InputDevice
    {
        public static Keyboard current { get { throw null; } }
    }

    // The real enum has these members (and aliases such as LeftWindows / AltGr); the harness parses names only.
    public enum Key
    {
        None, Space, Enter, Tab, Backquote, Quote, Semicolon, Comma, Period, Slash, Backslash, LeftBracket, RightBracket,
        Minus, Equals, A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9, Digit0,
        LeftShift, RightShift, LeftAlt, RightAlt, LeftCtrl, RightCtrl, LeftMeta, RightMeta, ContextMenu, Escape,
        LeftArrow, RightArrow, UpArrow, DownArrow, Backspace, PageDown, PageUp, Home, End, Insert, Delete, CapsLock,
        NumLock, PrintScreen, ScrollLock, Pause, NumpadEnter, NumpadDivide, NumpadMultiply, NumpadPlus, NumpadMinus,
        NumpadPeriod, NumpadEquals, Numpad0, Numpad1, Numpad2, Numpad3, Numpad4, Numpad5, Numpad6, Numpad7, Numpad8,
        Numpad9, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, OEM1, OEM2, OEM3, OEM4, OEM5, IMESelected
    }

    public static class InputSystem
    {
        // Real signature: (InputDevice device, TState state, double time = -1); always pass the time.
        public static void QueueStateEvent<TState>(InputDevice device, TState state, double time)
            where TState : struct, LowLevel.IInputStateTypeInfo { throw null; }

        public static InputSettings settings { get { throw null; } set { } }
    }

    // Real one is a ScriptableObject; only what DevControl sets (keep the keyboard on without window focus).
    public class InputSettings
    {
        public BackgroundBehavior backgroundBehavior { get { throw null; } set { } }

        public enum BackgroundBehavior
        {
            ResetAndDisableNonBackgroundDevices = 0,
            ResetAndDisableAllDevices = 1,
            IgnoreFocus = 2
        }
    }
}

namespace UnityEngine.InputSystem.Utilities
{
    public struct FourCC
    {
    }
}

namespace UnityEngine.InputSystem.LowLevel
{
    public interface IInputStateTypeInfo
    {
        Utilities.FourCC format { get; }
    }

    public struct KeyboardState : IInputStateTypeInfo
    {
        public KeyboardState(params Key[] pressedKeys) { throw null; }
        public Utilities.FourCC format { get { throw null; } }
    }
}
