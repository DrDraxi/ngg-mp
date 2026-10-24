// Stubs: pause, menus, settings (docs/gamemap/03, 04). Access confirmed against the real game (docs/confirm #25,
// #33, #34, #46, #58, #60, #73, #76, #112, #113, #144).
// Time.timeScale: only PlayerPressedEscape.OpenMenu and the debug Screenshotter set 0. Back to 1: PauseMenu.Exit /
// QuitToMenu, SettingsMenu.Close (when not opened from pause) / DeleteStorySave / DeleteEverything, the Luke endings.
using UnityEngine;

public class PlayerPressedEscape : MonoBehaviour
{
    private GameObject m_objectToEnable;    // TYPE-GUESS GameObject, ACCESS-GUESS private (activated by OpenMenu; presumably the PauseMenu)

    private void Update() { throw null; }
    public void OpenMenu() { throw null; }  // public; m_objectToEnable.SetActive(true) synchronously, Time.timeScale = 0
}

public class PauseMenu : MonoBehaviour
{
    public void Exit() { throw null; }             // public: InputManager.DisableInputTemporarily, deactivates this GO, timeScale 1, cursor lock in Walk
    public void QuitToMenu() { throw null; }       // public: timeScale 1, SaveRun, LoadScene("Menu2")
    public void OpenSettingsMenu() { throw null; } // public: deactivates this GO; time stays frozen (SettingsMenu.Close reactivates it)
    public void RestartFromHole1() { throw null; } // public: CompleteNormalGolfRound then Exit (no scene reload)
}

// Menu2 scene. Only PlaySetUp loads "Main" (after PlayGame's existing-save / difficulty screens); loading "Main"
// directly skips the prompts, m_hasCheckedForFirstSave + SaveGameSettings and the music fade, and needs the mode set
// on the shared GameModeState. PlayGame never deletes the run.
public class MainMenu : MonoBehaviour
{
    private void Start() { throw null; }                       // private; forces Story, calls LoadRunSave (order vs SaveManager.Start undefined)
    public void PlayGame() { throw null; }                     // public
    private void PlaySetUp() { throw null; }                   // ACCESS-GUESS private (not listed public; patched by name only)
    public void PlayNineButtonPressed() { throw null; }        // public
    public void PlayNine() { throw null; }                     // public
    public void Quit() { throw null; }                         // public
    public void StreamerModeSelected(bool selected) { throw null; } // public; parameter name SIG-GUESS
}

public class MainMenu2 : MonoBehaviour
{
}

public class DifficultySelect : MonoBehaviour
{
}

// Opened from the pause menu in "Main" (PauseMenu inactive meanwhile) and from Menu2. Update unlocks the cursor.
public class SettingsMenu : MonoBehaviour
{
    private void Close() { throw null; }             // SIG-GUESS private: timeScale 1 when not from pause, else reactivates PauseMenu
    private void DeleteStorySave() { throw null; }   // SIG-GUESS private: DeleteRun, timeScale 1, LoadScene("Menu2")
    private void DeleteEverything() { throw null; }  // SIG-GUESS private: DeleteEverything, timeScale 1, LoadScene("INTRO")
}

// Debug only (P key, editor or m_externalDebug): toggles Time.timeScale 0.
public class Screenshotter : MonoBehaviour
{
}

public class BuildSettings : MonoBehaviour
{
}
