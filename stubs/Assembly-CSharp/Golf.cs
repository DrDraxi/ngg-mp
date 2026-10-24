// Stubs: clubs, swing panels, shot sequence, ball, wind, LMUGC course (docs/gamemap/02, 03, 05).
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[Flags]
public enum ShotType
{
    None = 0,
    Topped = 1,
    Thin = 2,
    Good = 4,
    Fat = 8,
    Ground = 16,
    Miss = 32,
    Perfect = 64,
}

public enum SpinType
{
    None = 0,
    Straight = 1,
    Slice = 2,
    Hook = 4,
    Draw = 8,
    Fade = 16,
}

// Confirmed (answers #16).
[Flags]
public enum Clubs
{
    None = 0,
    Driver = 1,
    Hybrid = 2,
    Iron = 4,
    Wedge = 8,
    Putter = 16,
}

// Confirmed (answers #16).
[Flags]
public enum Surfaces
{
    None = 0,
    Fairway = 1,
    Green = 2,
    Rough = 4,
    OuterRough = 8,
    Bunker = 16,
}

public class Club : ScriptableObject
{
    public string clubName;     // "Driver", "Hybrid", "Iron", "Wedge", "Putter"
    public float cameraFOV;     // TYPE-GUESS float
    public float balloon;       // TYPE-GUESS float
    public float loft;          // TYPE-GUESS float
    public float dist;          // TYPE-GUESS float
    // Also has a Clubs flags field named clubEnumSorry (answers #16) and powerVisualMult, the top of ClubSwing.m_power
    // for this club (answers #53). Their access is not confirmed: not declared, read by name.
}

// Club pickers in the world/UI.
public class SelectableClub : MonoBehaviour
{
    public void SetLockedState(bool locked) { throw null; }  // SIG-GUESS parameter name; public (ProgressManager calls it)
}

public class PanelManager : MonoBehaviour
{
    public static PanelManager instance;

    // CONFIRMED public (#10).
    public ClubSwing m_clubSwingPanel;
    public Spin m_spinPanel;
    public WindPanel m_windPanel;
    public LMUGC m_LMUGCPanel;
    public CompositeOverlay m_fmvPanel;      // confirmed type (answers #10)
    public ClubsPanelNavigation m_clubPanel; // confirmed type (answers #10); pairs with RunSave.m_putterUnlocked
    public TweaksPanel m_tweaksPanel;
}

// TYPE-GUESS: base class MonoBehaviour (type of PanelManager.m_fmvPanel).
public class CompositeOverlay : MonoBehaviour
{
}

// Type of PanelManager.m_clubPanel (answers #10). Unlocked by SetUpScene when m_putterUnlocked, by buying the
// putter (Unlock(true)) and by DialogueManager.CompletedDemo. The iron is always unlocked.
// TYPE-GUESS: base class MonoBehaviour.
public class ClubsPanelNavigation : MonoBehaviour
{
    public void Unlock(bool animate) { throw null; }  // idempotent (a repeat only spawns the PFX again)
}

public class TweaksPanel : MonoBehaviour
{
    private float GetForwardPositionFactor() { throw null; }  // SIG-GUESS
    private void ResetPosition() { throw null; }              // SIG-GUESS
}

// 2D swing panel (PanelManager.instance.m_clubSwingPanel).
public class ClubSwing : MonoBehaviour
{
    // Clamped 0..m_currentClub.powerVisualMult (per club, can exceed 10), x0.3 for Ground (answers #53).
    public float m_power;
    public float m_loft;
    public float m_backSpin;
    public int m_groundType;        // 0 normal, 1 sand, 2 rough, 3, 4
    public bool m_isPracticeMode;
    public bool m_isPanelActive;
    public Club m_currentClub;

    private void FixedUpdate() { throw null; }
    private void DetectShot() { throw null; }
    private void HandleShot(ShotType shotType, float backSpinMultiplier) { throw null; }
    public void UpdateClub() { throw null; }
    private float GetNewReturnRate(float oldRate) { throw null; }
    public void ResetArms() { throw null; }
}

// Spin / aim panel (PanelManager.instance.m_spinPanel).
public class Spin : MonoBehaviour
{
    public float m_angle;
    public float m_spin;
    public float m_spinForceMultiplier;

    public void Unlock(bool animate) { throw null; }
    private IEnumerator ApplyChaos() { throw null; }
    private IEnumerator ApplySpinChaos() { throw null; }
    private IEnumerator ApplyWiggleChaos() { throw null; }
    public void ResolveFinalAngle() { throw null; }
}

public class WindPanel : MonoBehaviour
{
    private Image m_arrow, m_fanGraphic;
    private Transform m_fan;
    private TMP_Text m_text, m_timerText;
    private float m_fanSpeedMult;
    public float m_forceMutiplierOfWindOnBall;
    public Vector2 m_currentWind;
    private Vector2 m_desiredWind;
    private float m_maxWindSpeed;
    private Gradient m_windSpeedGradient;
    public bool m_debugNoWind;
    private Material skyboxMaterial;
    private float rotationSpeed = 1f;
    private Terrain m_terrain;
    private Material m_grass;
    private float m_grassWindMult;
    private Vector3 m_offScreenPos, m_onScreenPos;
    private Transform m_PFXPos;
    private GameObject m_lock;
    private AudioSource m_sfx;
    private AnimationCurve m_volume;
    private static readonly int WindMult, WindDir;

    // Answers #44, #56, #57, #95: StartWind is not idempotent (each call starts another WindCycle and re-rolls the
    // wind), so two cycles can run in vanilla. WindCycle restarts itself and does the easing of m_currentWind toward
    // m_desiredWind (its lerp target); Update never changes m_currentWind. LateUpdate overwrites the wind for
    // m_debugNoWind, the LMUGC no-wind timer (and calls NewWind when it expires) and a challenge's m_forceWind
    // (m_wind * m_maxWindSpeed). BountyPad.StartBounty, ExitBountyChallenge, LMUGC.StartChallenge and
    // SaveManager.LoadInLMUGC do StopAllCoroutines + StartCoroutine(WindCycle()).
    private void Awake() { throw null; }
    public void StartWind(bool showPfx) { throw null; }
    public void NewWind() { throw null; }
    private void Update() { throw null; }
    public IEnumerator WindCycle() { throw null; }
    private void LateUpdate() { throw null; }
}

public class HitSequeneceManager : MonoBehaviour
{
    public static HitSequeneceManager instance;

    // Confirmed (answers #11). m_showPIP is not here: it is the property PanelAnimator.m_showPIP.
    public Camera m_FMVCam;
    private Transform m_PIPCameraHolder;
    public ThirdPersonCamera m_tpc;

    public IEnumerator HitSequence(float angle, float power, bool isMiss, ShotType shot) { throw null; }
    public void CompleteSequence(Ball ballThatSentMessage) { throw null; }   // confirmed (answers #61); Ball calls it
    // Restart(true) runs Ball.AddBallStroke (only if m_ballHit) before the next shot (answers #51).
    public void Restart(bool isResumedShot) { throw null; }
    public void ShowBallInHoleFeedback(int par, bool showAnyway) { throw null; }
    private void RestartInput() { throw null; }
}

public class HitManager : MonoBehaviour
{
    public static HitManager instance;
    public Ball m_ball;
    private int _layerMask;
    private float m_powerMult;
    private float m_spinMult;
    private GameObject m_groundHitEffect, m_sandHitEffect, m_grassHitEffect;
    private Clod m_clod;
    private float m_hardModeWiggleForGoodShotMult, m_hardModespinForDrawOrFadeMult, m_hardModeangleForStraightShotMult;
    public float m_hardModeAngleAccelAdjust;
    public bool m_predictionBallHasHit;
    private float m_wiggleForGoodShot, m_spinPowerComparedToAngle, m_angleForStraightShot;
    private Coroutine _backspin;

    private void Start() { throw null; }
    public void ResetBall() { throw null; }
    public void HitSimulationBall(float power, bool isMiss) { throw null; }
    public SpinType GetSpinType() { throw null; }
    private Vector3 RotateAccordingToGolfer(Vector3 input) { throw null; }
    private Vector3 RotateAccordingToPath(Vector3 input) { throw null; }
    public void HitBall(float angle, float power, bool isMiss, ShotType shot) { throw null; }
    private void GroundHitEffect(ShotType shot) { throw null; }
    private void Clod(ShotType shot) { throw null; }
}

// TYPE-GUESS: base class MonoBehaviour (dirt clod spawned by HitManager).
public class Clod : MonoBehaviour
{
}

// GameObject "Ball", layer "Ball". Every field is public (answers #6, #62).
public class Ball : MonoBehaviour
{
    public Rigidbody m_rb;
    public ConstantForce m_ballSpinForce;
    public CurrentShotData m_currentShotData;
    public bool m_ballHit;
    public bool m_isMultiShot;
    // 0-based and not incremented by the shot itself: the stroke being played is m_currentShot + 1 (answers #45, #51).
    // AddBallStroke (in Restart(true), only if m_ballHit) increments it before the next shot;
    // LMUGC.TeleportBallToHole(.., false) sets -1; LMUGC.RetakeShot +1 (penalty) / -1 (mulligan).
    public int m_currentShot;
    public bool m_canRetakeShot;
    public bool m_isInContact;
    public bool m_ballHasBounced;
    public MeshRenderer m_meshRenderer;
    public GameObject m_resumeBallTrigger;   // "InteractableBall"
    public GameObject m_resumeBallEffect;
    public TrailRenderer m_trail;
    public TrailRenderer m_chasingTrail;

    private void FixedUpdate() { throw null; }
    public void KillBall() { throw null; }
    public IEnumerator BonusFriction() { throw null; }
    public IEnumerator SpinApplication() { throw null; }
    public void ResetCurrentShot() { throw null; }
}

[Serializable]
public class CurrentShotData
{
    public List<string> m_touchedSurfaces = new List<string>();
    public string m_lastTouchedSurface;
    // Unity units from the ball to the closest hole in front (XZ), labelled "yd" in the UI without conversion; 999
    // when there is no hole, -1 after a reset (answers #54). Not the distance travelled.
    public float m_finishedDistance;
    public bool m_hitWithCorrectClub;
    public ShotType m_shotType;
    public SpinType m_spinType;
    public bool m_shotComplete;
    public bool m_hitTarget;
    public bool m_shotFromCorrectTee;
    public Vector3 m_hitFromLocation;
}

public class Breakable : MonoBehaviour
{
}

public class BallGlass : MonoBehaviour
{
}

public class BallHitButton : MonoBehaviour
{
}

public class PhysicsChallengeObject : MonoBehaviour
{
}

public class ParFeedback : MonoBehaviour
{
}

// TYPE-GUESS: base class MonoBehaviour (UI row of the LMUGC scorecard).
public class ScorecardEntry : MonoBehaviour
{
}

// TYPE-GUESS: base class MonoBehaviour.
public class HandicapSign : MonoBehaviour
{
}

// 9-hole course (PanelManager.instance.m_LMUGCPanel); also JustGolf mode.
public class LMUGC : MonoBehaviour
{
    private int[] m_pars = new int[9];
    private ScorecardEntry[] m_scoreCards;
    private GameObject m_challengeUI, m_LMUGCUI;
    private TMP_Text m_challengeHeading;
    private GameObject m_storyTerminalHeading, m_normalGolfTerminalHeading;
    private MainCameraManager m_mainCamManager;
    private ChallengeSet LMUGCChallenge;
    private Color m_parColor, m_underColor, m_overColor, m_noScoreColor;
    private TMP_Text m_totalScoreText;
    private Transform m_endPoint;
    private DialogueLine m_completeDialogue, m_failedDialogue, m_failedShortDialogue;
    private ScorecardEntry[] m_scoreCardsMachine;
    private TMP_Text m_totalScoreMachine, m_bestScore, m_bestScoreNoAssist;
    private Transform m_center;
    private MeshRenderer m_lmugcTick, m_lmugcCross, m_lmugcBackLight;
    private Material m_noLightMaterial, m_redMaterial, m_greenMaterial;
    public HandicapSign m_handicapSign;
    private bool hasSeenTransitionThisSession;
    private bool m_ballInHole;
    private bool forceTeleport;
    public Vector3 m_shotFromPosition;
    public Quaternion m_lastShotRotation;
    private Button m_retake, m_mulligan;
    private TMP_Text m_mulliganText;
    private Button m_noWind;
    private TMP_Text m_noWindText;
    private Button m_skip;
    private TMP_Text m_skipText;

    private void Start() { throw null; }
    private void Update() { throw null; }
    public void StartChallenge() { throw null; }
    public void LoadInToChallenge() { throw null; }
    public void TeleportBallToHole(int hole, bool teleportPlayer) { throw null; }
    // id = BallTarget.id (its GameObject name when empty); the hole is the FIRST number in it (ExtractNumber, Regex
    // "\d+"); an id without a number throws IndexOutOfRange inside the game (answers #45, #52).
    public void CompleteHole(string id, int score) { throw null; }
    public void UpdateScoreCard() { throw null; }
    public void UpdateBestScores() { throw null; }
    public void UpdateTerminalWithNormalScores() { throw null; }
    public void CompleteNormalGolfRound() { throw null; }
    private IEnumerator WaitingToTeleportBall(int hole) { throw null; }
    private int ExtractNumber(string input) { throw null; }
    private void ResetScores() { throw null; }
    public void RetakeShot(bool isMulligan) { throw null; }
    public void UseNoWind() { throw null; }
    public void UseHoleSkip() { throw null; }
    // Neither FinishChallenge nor CompleteNormalGolfRound calls ExitChallenge (answers #66). FinishChallenge (Story)
    // keeps the scores; CompleteNormalGolfRound (JustGolf) runs AddScoreToCard, ExitBountyChallenge, ResetScores,
    // Restart(false), SaveRun. The round score is the sum of the 9 m_run.lmugcScore entries with unplayed holes
    // counted as par + 2 (answers #139). JustGolf resume: SaveManager.LoadInLMUGC calls LoadInToChallenge (not
    // StartChallenge) and resumes at the first hole with lmugcScore < 1 (answers #65).
    public void ExitChallenge() { throw null; }
    public void FinishChallenge() { throw null; }
    public void UpdateButtons() { throw null; }
}
