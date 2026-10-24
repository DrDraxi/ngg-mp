// Stubs: bootstrap, Steam, game mode, scenes/world registry, tees, input (docs/gamemap/02, 03, 05).
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Steamworks.NET template SteamManager (game script). AppId 3510740.
public class SteamManager : MonoBehaviour
{
    // CONFIRMED (#1): public static properties; Instance creates the "SteamManager" object when null.
    public static bool Initialized { get { throw null; } }
    public static SteamManager Instance { get { throw null; } }  // CONFIRMED public (#1)

    public void UnlockAchievement(AchievementID achievement) { throw null; }
}

// Top-level (answers #2). SteamManager.UnlockAchievement has this single overload (answers #101).
public enum AchievementID
{
    COMPLETE_LMUGC,
    FULL_BAG,
    PUTTER,
    PADLOCK,
    ACE,
    ALBATROSS,
    EAGLE,
    BIRDIE,
    PAR,
    PARKOUR,
    TALL_BALL,
    SKIPPER,
    MISSING_IN_ACTION,
    CODE,
    TOILET,
    CREDITS,
    TRUEENDING,
    SCRATCH,
    CORNER,
    COMPLETE,
}

// Checks collider name "First Person Character" or "Ball".
public class AchievementTrigger : MonoBehaviour
{
}

public class MainManager : MonoBehaviour
{
}

public enum GameMode
{
    Story,
    JustGolf,
}

public class GameModeState : ScriptableObject
{
    public GameMode mode;
}

// World registry; string ids are stable network ids.
public class GreatConnector : MonoBehaviour
{
    public static GreatConnector instance;

    public ChallengeSet[] m_allChallenges;
    public Elevator m_elevator;

    public StandHere GetTee(string id) { throw null; }
    // Compares clean asset names; an unknown name logs an error (Debug.LogError) and returns null.
    public ChallengeSet GetChallengeSet(string name) { throw null; }
    public void ActivateTee(string id) { throw null; }
    public void DisableAllTees() { throw null; }
    public bool HasTee(string id) { throw null; }
}

// Tee. OnTriggerEnter("First Person Character") -> Activate().
public class StandHere : MonoBehaviour
{
    private ParticleSystem m_waiting, m_entered;
    private bool m_unlocksGolf;
    private GameObject m_golfPrompt;
    private CapsuleCollider m_col;
    private ChallengeSet m_loadChallengeSetOnEnter;
    private Transform m_autoRotateTowards;
    public bool m_rotatePlayerOnGolfEnter;
    public bool m_playerInside;
    public bool snapTeeToGround;

    private void OnValidate() { throw null; }
    private void Awake() { throw null; }
    private void OnEnable() { throw null; }
    private void Update() { throw null; }
    private void CheckIfInMultiShotChallenge() { throw null; }
    private void OnTriggerEnter(Collider other) { throw null; }
    private void OnTriggerStay(Collider other) { throw null; }
    public void Activate() { throw null; }
    private void OnTriggerExit(Collider other) { throw null; }
    public void Deactivate() { throw null; }
    private void SnapToGround() { throw null; }
}

// Cup / target trigger (layer Ball).
public class BallTarget : MonoBehaviour
{
    // Only holeNumber is public (answers #13). Its id (the GameObject name when empty) is what
    // LMUGC.CompleteHole(string id, int score) receives (answers #45).
    private bool isHole;
    public int holeNumber;
    private int par;
    private string id;

    private void OnTriggerEnter(Collider other) { throw null; }
}

public class InputManager : MonoBehaviour
{
    public static InputManager instance;

    // Actions: Move, Switch, Use, Reset, Adjust, Practice, Pause, Right Joystick.
    public PlayerInput input;          // CONFIRMED public (#26)
    public bool m_isUsingGamePad;      // CONFIRMED public (#26)
}

// Raycasts layer "InteractTrigger" and calls FPSInteractable.Interact().
public class InteractChecker : MonoBehaviour
{
    private void Update() { throw null; }
}
