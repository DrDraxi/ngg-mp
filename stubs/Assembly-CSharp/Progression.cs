// Stubs: money, unlocks, interactions, doors/locks, cosmetics, world machinery (docs/gamemap/03, 04, 05).
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class Bank : MonoBehaviour
{
    private Image m_fpsBacking, m_golfBacking;
    private TMP_Text m_fpsBalance, m_golfBalance;
    private RectTransform m_fpsBalanceTransform;
    public GameObject m_errorMessage;
    private RectTransform m_lmugcPanelTransform;
    private TMP_Text m_mulliganBalance;
    private RectTransform m_mulliganPanel;
    private TMP_Text m_noWindBalance;
    private RectTransform m_noWindPanel;
    private TMP_Text m_holeSkipBalance;
    private RectTransform m_holeSkipPanel;
    private float m_onScreenX, m_offScreenX;
    private Gradient m_gradient;
    private Vector2Int m_minMaxBalances;
    private float m_countTime;
    public int m_balance;
    private Coroutine _balanceChange, _balanceChangeGolf;
    private bool canPlaySound;

    private void Start() { throw null; }
    // m_balance += change synchronously. halfSound true plays at volume 1.0, false at 0.5 (the name is inverted);
    // fast shortens the animation. ChangeBalance(0, true, true) is the "can't afford" flash.
    public void ChangeBalance(int change, bool halfSound, bool fast) { throw null; }
    private IEnumerator BalanceFPSAnimation(int before, int after, bool halfSound, bool fast) { throw null; }
    public void UpdateVisuals(int value, TMP_Text text, Image backing) { throw null; }
    public void UpdateVisuals() { throw null; }
    private IEnumerator BalanceGolfAnimation(int before, int after, bool halfSound, bool fast) { throw null; }
    public void ShowBuyAssist(int type) { throw null; }
    private IEnumerator LMUGCAssistAnimation(int type) { throw null; }
    private void UpdateAssistValues() { throw null; }
}

public class ProgressManager : MonoBehaviour
{
    public static ProgressManager instance;
    public Bank m_bank;
    public ObjectStates m_objectStates;  // ACCESS-GUESS public (used as ProgressManager.instance.m_objectStates)

    private void Start() { throw null; }  // sets bank 99990 before the save load overrides it
    public void UnlockClub(Club c) { throw null; }
    // Idempotent (a repeat only spawns the PFX again).
    public void UnlockStance(bool showPFX) { throw null; }
    public void UnlockPractice(bool showPFX) { throw null; }
    public void UnlockAdjust(bool showPFX) { throw null; }  // pairs with RunSave.m_aimUnlocked
}

// Doors, gates and shop stock by string id.
public class ObjectStates : MonoBehaviour
{
    public PicketGates[] m_gates;
    private float m_openAngle;
    private Doors[] m_doors;
    private GameObject m_sparksPrefab;
    private Transform m_eyes;
    private SlidingDoors[] m_slidingDoors;
    private Material m_glowingRedMaterial, m_glowingGreenMaterial;
    private GameObject m_driver, m_driverStandee, m_wedge, m_wedgeStandee, m_hybrid, m_hybridStandee, m_ticket, m_lmugcMachine, m_assistStandees, m_assists;
    private Material m_flashingLightMaterial;
    private Color m_flashingLightOnColor;
    public ChallengeSet m_cameraOffChallenge;
    public GameObject m_noCardFoundMessage;
    public BunkerTerminal m_bunkerTerminal;

    // Each door / gate method ends with SaveRun.
    public void SetGates(string id, bool open) { throw null; }
    public void OpenDoor(string id) { throw null; }
    public void OpenOrCloseSlidingDoor(bool open, string id) { throw null; }
    private void SetYRotation(Transform t, float angle) { throw null; }
    // Not idempotent: the Set*Available methods spawn PFX and play a sound on every call.
    public void SetDriverAvailable() { throw null; }
    public void SetWedgeAvailable() { throw null; }
    public void SetHybridAvailable() { throw null; }
    public void SetTicketAvailable() { throw null; }
    public void EnableLMUGCMachine() { throw null; }
    public void CheckRFIDDoors() { throw null; }  // idempotent
    public bool CheckRFIDDoorAlreadyOpen(string id) { throw null; }
    public void ServerCut() { throw null; }
    public void ServerRestore() { throw null; }
    public void SetRecordingMaterialState(bool on) { throw null; }
}

// Top-level, public fields.
[Serializable]
public class PicketGates
{
    public string id;
    public Transform gate1, gate2;
    public GameObject padlock, padlockOpened;
    public bool showSparks;
}

// Top-level, public fields.
[Serializable]
public class Doors
{
    public string id;
    public Transform door;
    public Vector3 rotation;
    public GameObject padlock, padlockOpened;
    public bool showSparks;
}

// Top-level, public fields.
[Serializable]
public class SlidingDoors
{
    public string id;
    public Transform door;
    public MeshRenderer[] pips;
    public ParticleSystem pfx;
}

// Money in FPSInteractable.Interact: CollectCash +m_cashValue; FindMissingBall +1000 (hardcoded); BuyClub,
// LMUGCTicket and the three assists -m_cashValue (one ChangeBalance(-m_cashValue, true, true), or
// ChangeBalance(0, true, true) + error message when unaffordable). GetCrowbar pays nothing; GetCard has no handler.
[Serializable]
public enum InteractionType
{
    ContinueShot,
    CollectCash,
    StartBounty,
    BuyClub,
    Teleport,
    GetCrowbar,
    GetCard,
    Padlock,
    LMUGCTicket,
    ElevatorDoorUpper,
    ElevatorDoorLower,
    ElevatorDown,
    ElevatorUp,
    RFID,
    StartLMUGC,
    BuyMulligan,
    BuyNoWind,
    CutServer,
    OpenBunker,
    BuyHoleSkip,
    FindMissingBall,
    TalkToLuke,
}

public class FPSInteractable : MonoBehaviour
{
    private GameObject m_topOfObject;
    public InteractionType m_interaction;
    public bool m_useUseButton;
    public int m_cashValue;
    private Club m_club;
    private Ball m_thisBall;
    private float m_dialogueDelay;
    private DialogueLine m_dialogueToFire;
    private string m_videoToPlay;
    private string m_gateToOpen;
    private string m_gateToClose;
    private bool m_deactivateAfterUse;

    private void OnValidate() { throw null; }
    public string GetPrompt() { throw null; }
    public void Interact() { throw null; }
}

// Ball skins.
public class Cosmetics : MonoBehaviour
{
    public static Cosmetics instance;
    public BallLooks[] m_balls;  // confirmed public (answers #62)

    public void SetBall(int index) { throw null; }  // SIG-GUESS parameter name
}

// Element of Cosmetics.m_balls: a class (answers #14, #62). mat is public, a URP material (answers #130). It also has
// a Sprite sprite and a Color color (answers #14; access not confirmed, so not declared).
[Serializable]
public class BallLooks
{
    public Material mat;
}

public class BallSelectMenu : MonoBehaviour
{
    public void SelectBall(int index) { throw null; }  // SIG-GUESS parameter name
}

// Code door. Random 4-digit code per client, made in Start by private NewCode, which also sets m_text.text = code.
public class Authenticator : MonoBehaviour
{
    public string code;
    public TMP_Text m_text;

    private void Start() { throw null; }
    private void NewCode() { throw null; }
}

// Compares its input with auth.code; on a match CompleteChallenge(GetCurrentChallenge()).
public class InputBoard : MonoBehaviour
{
    public Authenticator auth;

    public void AddInput(string input) { throw null; }  // SIG-GUESS parameter name
}

// OnEnable instantiates the answer buttons and orders them with UnityEngine.Random.Range + SetAsFirstSibling;
// OnDisable destroys them.
public class TriviaBoard : MonoBehaviour
{
    private void OnEnable() { throw null; }
    private void OnDisable() { throw null; }       // ACCESS-GUESS private
    private void ShuffleButtons() { throw null; }  // SIG-GUESS
}

public class TicketShelf : MonoBehaviour
{
}

public class TargetAnimator : MonoBehaviour
{
}

public class ServerBlinks : MonoBehaviour
{
}

public class TriggerSecurity : MonoBehaviour
{
}

// There is no ElevatorManager. GreatConnector.instance.m_elevator.
public class Elevator : MonoBehaviour
{
    public GameObject m_bunker;  // toggled by the ride (per player's view); turned off by Apply*Defaults

    public void OpenTopDoors() { throw null; }
    public void OpenBottomDoors() { throw null; }
    public void RideElevator(bool down) { throw null; }
}

public class BunkerTerminal : MonoBehaviour
{
    public void SetScreenOpen() { throw null; }
}

public class Teleporter : MonoBehaviour
{
    public TeleportManager m_teleManager;

    public void Teleport() { throw null; }
}

// No static instance: reach it through Teleporter.m_teleManager or FindObjectOfType.
public class TeleportManager : MonoBehaviour
{
    public IEnumerator FadeDownEnding() { throw null; }  // loads "CREDITS"
    public IEnumerator TeleTransition(Teleporter te, GameObject objectToEnable, GameObject objectToDisable) { throw null; }
    public IEnumerator TeleportEndBattle(Transform t) { throw null; }
}

// OnTriggerEnter has NO filter: any collider entering Luke's trigger starts LukeHitSequence (SaveRun with
// m_hitLuke = true, then "CREDITS").
public class LukeVideo : MonoBehaviour
{
    private void OnTriggerEnter(Collider other) { throw null; }
    private IEnumerator LukeHitSequence() { throw null; }
    // The true ending (via DialogueManager.EndLukeFadeDown): TRUEENDING achievement, timeScale 1, FadeDownEnding.
    public void LukeLeaveSequence() { throw null; }
}
