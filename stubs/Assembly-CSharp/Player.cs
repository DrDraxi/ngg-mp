// Stubs: player controller, first-person character, cameras, FMV (docs/gamemap/02, 03, 05).
using System.Collections;
using ECM2.Examples.FirstPerson;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

// Top-level, Golf = 0, Walk = 1 (answers #5, #48).
public enum ControlMode
{
    Golf,
    Walk,
}

public class MoveAndHitController : MonoBehaviour
{
    public static MoveAndHitController instance;
    public ControlMode m_mode;
    public GameObject m_FPSController;  // named "First Person Character"
    private ValidGolfSpotCheck m_spotChecker;
    private Vector3 _spotCheckerOffset;
    private Camera m_FPSCam;
    public bool m_moveLocked;
    public FirstPersonVideo m_fpsVideo;
    private FirstPersonCharacterInput m_fpsInput;
    public ECM2.Character m_fpsCharacter;
    private FootstepsAudio m_footsteps;
    public GameObject m_clearingCamera;
    // Not at the player (answers #55): SwitchToGolf sets m_golferHolder.position = FPS position -
    // (m_ballPosition.position - m_golferHolder.position), so the ball (m_ballPosition) lands on the walker's spot.
    // The inactive m_FPSController and m_playerPos stay at that spot while golfing.
    public Transform m_golferHolder;
    private GameObject m_chaseCamera;
    public Transform m_ballPosition;
    private Camera m_3rdPersonCam;
    public Club m_selectedClub;
    private Camera[] m_RTEXExtraCams;
    private Camera m_bountyRTEXCam;
    private GolfVideo m_golfVideo;
    private SecurityCameraManager m_securityCameraManager;
    private Vector2 m_adjustSpeedRange;
    private float m_adjustSpeedAccel;
    private float m_adjustSpeed;
    public bool m_cantSwitch;
    public bool m_wantsToInteract;
    public BountyPad m_activeBountyPad;
    public Vector3 m_playerPos;
    public Vector3 m_ballPos;
    private Coroutine _goToFPS;
    public bool m_cameraZoomed;
    public float m_cameraRatio;
    private RenderTexture m_transitionRTEX;
    private Image m_transitionImage;
    private bool m_usingMenu;
    private FirstPersonCharacter m_fpsChar;
    private ECM2.CharacterMovement m_fpsChar2;
    private float aimRequirement = 0.5f;
    private bool m_adjustedLastFrame;
    private Transform m_error;
    public Transform m_alreadyInLMUGC;

    private void Awake() { throw null; }
    private void Start() { throw null; }
    private void Update() { throw null; }
    private void LateUpdate() { throw null; }
    public void FPSTransitionForDialogue() { throw null; }
    public void FPSTransitionForBounty() { throw null; }
    private IEnumerator FPSTransition() { throw null; }
    public void ResumeShot() { throw null; }
    public void GolfTransition(bool isResumedShot) { throw null; }
    private IEnumerator RenderRTEXCams() { throw null; }
    private void SetRTEX(Camera cam) { throw null; }
    private void SwitchToFPS() { throw null; }
    private void SwitchToGolf(bool isResumedShot) { throw null; }
    public void SetGroundSurface() { throw null; }
    public void Adjust(bool left) { throw null; }
    public IEnumerator ClearCanvasForLargeFeedback(GameObject toDisable) { throw null; }
    public static void WorldPositionToDensityMapCoords(Transform target, Terrain terrain, Texture2D densityMap, out Vector2 uv, out int pixelX, out int pixelY) { throw null; }
}

// TYPE-GUESS: base class MonoBehaviour (type documented only as a field type).
public class ValidGolfSpotCheck : MonoBehaviour
{
}

// TYPE-GUESS: base class MonoBehaviour.
public class FootstepsAudio : MonoBehaviour
{
}

// TYPE-GUESS: base class MonoBehaviour.
public class SecurityCameraManager : MonoBehaviour
{
}

public class SecurityCamera : MonoBehaviour
{
}

public class RoofCamera : MonoBehaviour
{
}

public class Monitor : MonoBehaviour
{
}

public class IntroLogo : MonoBehaviour
{
}

public class MainCameraManager : MonoBehaviour
{
}

public class ThirdPersonCamera : MonoBehaviour
{
}

public class PanelAnimator : MonoBehaviour
{
    public static PanelAnimator instance;
    public Camera m_PIPCamera;
    public bool m_showPIP { get { throw null; } set { throw null; } }  // answers #11 (not on HitSequeneceManager)
}

// Golfer FMV (VideoPlayer -> RenderTexture). Local only. Its VideoPlayer field is private.
public class GolfVideo : MonoBehaviour
{
    public bool m_videoReadyToPlay;

    public void PlayIdle() { throw null; }
    public void PlayHit(float delay) { throw null; }  // SIG-GUESS parameter name
    public void PlayWait() { throw null; }
    public void PlayCelebrate(HoleResult result) { throw null; }  // SIG-GUESS parameter name
}

// Confirmed (answers #16).
public enum HoleResult
{
    Ace,
    Albatross,
    Eagle,
    Birdie,
    Par,
    Bogey,
    DoubleBogey,
    TripleBogey,
    More,
}

// First-person hands FMV. Local only.
public class FirstPersonVideo : MonoBehaviour
{
    public VideoPlayer m_videoPlayer;
}

namespace ECM2.Examples.FirstPerson
{
    public class FirstPersonCharacter : ECM2.Character
    {
        public float _cameraPitch;
        public GameObject cameraParent;
    }

    // TYPE-GUESS: base class MonoBehaviour.
    public class FirstPersonCharacterInput : MonoBehaviour
    {
        private Camera m_fpsCam;  // TYPE-GUESS Camera; ACCESS-GUESS private
    }

    // TYPE-GUESS: base class MonoBehaviour.
    public class FirstPersonCharacterLookInput : MonoBehaviour
    {
    }
}
