using System.Collections.Generic;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// What the harness needs to drive the game like a player at the PC, for remote tests without screen access or
    /// mouse/keyboard: screenshots, key presses through the Input System, clicks on UI buttons, a scene report, the
    /// multiplayer window and quitting. Implemented over Unity by <c>HarnessControl</c>; tests use a fake. Every member
    /// reports what it cannot do instead of throwing.
    /// </summary>
    public interface IHarnessControl
    {
        /// <summary>True while an earlier action is still in progress (a key held, a screenshot not yet on disk, a
        /// click's frame); the command queue waits for it.</summary>
        bool Busy { get; }

        /// <summary>The folder screenshots go to (BepInEx/screenshots).</summary>
        string ScreenshotDirectory { get; }

        /// <summary>Captures the screen into <paramref name="fileName"/> in <see cref="ScreenshotDirectory"/> (written
        /// at the end of the frame); <paramref name="path"/> is the full path actually used.</summary>
        bool Screenshot(string fileName, out string path, out string error);

        /// <summary>Presses <paramref name="key"/> (an Input System Key name, see <see cref="KeyNames"/>) and releases
        /// it after <paramref name="holdSeconds"/> of real time. <paramref name="message"/> says how it was delivered.</summary>
        bool PressKey(string key, float holdSeconds, out string message);

        /// <summary>Active, interactable UI elements a click reaches (Buttons and other pointer-click handlers).</summary>
        IReadOnlyList<UiElement> FindClickables();

        /// <summary>Clicks <paramref name="element"/> (from <see cref="FindClickables"/>): Button.onClick, else the
        /// component's OnPointerClick.</summary>
        bool Click(UiElement element, out string error);

        /// <summary>Writes the UI hierarchy (root canvases whose path contains <paramref name="filter"/>, empty for all)
        /// with each object's components to a text file; returns a summary with its path.</summary>
        string DumpUiTree(string filter, int maxDepth);

        /// <summary>Scenes, time scale, cursor, pause and settings menus, game and player mode, focus (one line each).</summary>
        string DescribeScene();

        /// <summary>Every VideoPlayer (state, clip, frame, target) and every renderer showing a video texture or one
        /// of the Presence look's drawings, plus what the look's video overlay is doing (one line each).</summary>
        string DescribeVideos();

        /// <summary>Shows (true), hides (false) or toggles (null) the plugin's multiplayer window.</summary>
        bool SetLobbyWindow(bool? visible, out bool nowVisible, out string error);

        /// <summary>Opens (PlayerPressedEscape.OpenMenu) or closes (PauseMenu.Exit) the game's pause menu, as Escape
        /// does but without keyboard focus.</summary>
        bool SetPauseMenu(bool open, out string message);

        /// <summary>Quits the game through its own path where there is one; the text says which.</summary>
        string Quit();
    }

    /// <summary>One clickable UI element as <c>buttons</c> lists it.</summary>
    public sealed class UiElement
    {
        public UiElement(string kind, string path, string label, object handle)
        {
            Kind = kind ?? string.Empty;
            Path = path ?? string.Empty;
            Label = UiMatcher.CleanLabel(label);
            Handle = handle;
        }

        /// <summary>"Button", or the type name of another pointer-click handler (Toggle, a game script).</summary>
        public string Kind { get; }

        /// <summary>"Scene:Root/Child/Button".</summary>
        public string Path { get; }

        /// <summary>The visible text below the element (rich-text tags removed); empty when it has none.</summary>
        public string Label { get; }

        /// <summary>The Unity component; opaque outside the implementation.</summary>
        public object Handle { get; }

        public override string ToString() =>
            "[" + Kind + "] " + (Label.Length > 0 ? "'" + Label + "'" : "(no text)") + " at " + Path;
    }
}
