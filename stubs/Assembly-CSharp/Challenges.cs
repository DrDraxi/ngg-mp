// Stubs: challenges, bounties, streaks (docs/gamemap/03, 04, 05; docs/confirm answers).
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Asset names like "ML_DoLMUGC", "CH_Putt3", bounties "BO_*", "PAR_Hole1".."PAR_Hole9"; runtime copies end in
// "(Clone)": compare with Utilities.CleanString.RemoveClone(name). The LMUGC round's set is a bounty set.
public class ChallengeSet : ScriptableObject
{
    // ACCESS-GUESS public: ChallengeSystem / BountyPad read these.
    public string m_requiredTee;
    public string m_extraTeeString;
    public Challenge[] m_challenges;
    public string[] m_completeEnableDialogues;
    public float m_appearDelay;
    public bool isBounty;
    public ChallengeSet challengeSetOnCompleteBounty;
    public Difficulty difficulty;
}

public enum Difficulty
{
    SuperEasy,
    Easy,
    Medium,
    KindaHard,
    Hard,
    VeryHard,
    Nightmare,
    SuperNightmare,
    ActuallyImpossible,
}

public enum SpecialChallenges
{
    None,
    Restart,
    CannotBeCompleted,
    Aim,
    HitWithPower,
    CompleteBounty,
    TouchNothingElse,
    GoVeryHigh,
    UNUSED,
    Skip,
    SkimOnWater,
    PracticeShot,
    GoToRandom,
    Quitter,
    CannotUseBigClubs,
    CannotTouchFairway,
    MultishotMultiTarget,
    MultishotTouchTree,
    CannotTouchBarrier,
    CannotTouchGreen,
}

// ACCESS-GUESS public fields (ChallengeSystem reads them).
[Serializable]
public class Challenge
{
    public string m_challengeString;
    public string m_shortString;
    public string m_arrowPointAt;
    public int m_streaksRequired = 1;       // > 1: CompleteChallenge returns early while the streak is short
    public bool m_isMultiShot;
    public int m_maxShots;
    public bool m_forceWaitForFinishShot;
    public Vector2 m_finishDistanceRange;
    public string m_finishDistanceHoleID;
    public bool m_checkFinishDistanceConstantly;
    public float m_minDistanceHit;
    public Clubs m_requiredClub;
    public Clubs m_recommendedClub;
    public ShotType m_shotType;
    public SpinType m_spinType;
    public Surfaces m_touch;
    public Surfaces m_finishSurface;
    public bool m_forceWind;
    public Vector2 m_wind;
    public ShotTarget[] m_targets;
    public bool m_targetsResetOnRestart;
    public bool m_targetsResetOnStreakBreak;
    public bool m_toggleTargetVisibility;
    public bool m_mustHitTargetsInOneShot;
    public bool m_onlyRespawnTargetsOnChallengeStart;
    public int m_targetsRequiredOverride;
    public string[] m_decorationsToEnable;
    public string[] m_physicsObjects;
    public SpecialChallenges m_specialChallenge;
    public bool m_autoAdvance;
    public bool m_fastAutoAdvance;
    public ChallengeHelpers m_helpers;
    public string functionOnStart;
    public float functionOnStartDelay;
    public string functionOnComplete;       // DialogueManager method name, run via Invoke
    public bool giveBountyRewardEarly;      // the bounty pays (and completes its pads) before its last challenge
    public string staticPIPLocation;
    public bool m_active;
    public bool m_complete;
    public ChallengeUI m_ui;
}

[Serializable]
public class ShotTarget
{
    public string id;
    public bool satisfied;
    public ShotType requiredShotType;
    public bool isAntiTarget;
}

[Flags]
public enum ChallengeHelpers
{
    None = 0,
    ShowTarget = 1,
    ShowNoTeeReminder = 2,
    ShowSpinType = 4,
    NoCheeseAllowed = 8,
}

// TYPE-GUESS: base class MonoBehaviour.
public class ChallengeUI : MonoBehaviour
{
}

// TYPE-GUESS: base class MonoBehaviour.
public class ChallengeReminder : MonoBehaviour
{
    // Sets the "You have $N" line from ProgressManager.instance.m_bank.m_balance (or the bounty count once bounties are
    // restored, where it may unlock the COMPLETE achievement). Called by FPSInteractable after pickups and purchases.
    public void UpdateBalanceReminder() { throw null; }
}

// TYPE-GUESS: base class MonoBehaviour.
public class TeeArrow : MonoBehaviour
{
}

// TYPE-GUESS: base class MonoBehaviour.
public class BountyLargeFeedback : MonoBehaviour
{
}

public class ChallengeSystem : MonoBehaviour
{
    public static ChallengeSystem instance;
    public ChallengeSet m_mainChallengeSet;
    public ChallengeSet m_challengeSet;
    public ChallengeReminder m_challengeReminder;
    public TeeArrow m_teeArrow;
    public string m_currentTee;
    public bool m_challengeCompletedOnThisShot;
    public List<string> m_preBrokenShots;
    public BountyLargeFeedback m_bountyFeedback;
    public bool m_isReplayingBounty;
    public List<Ball> m_ballsInPlay;
    public GameObject m_multiHitHeader;
    public TMP_Text m_strokeText;
    public GameObject m_boundary;
    public string m_previousString;

    // Returns early while SaveManager.instance.m_run.isInLMUGC; otherwise runs CheckChallenges.
    private void Update() { throw null; }  // ACCESS-GUESS private
    public bool IsFinalChallenge() { throw null; }
    // Re-instantiates the live main set with skip = m_run.m_currentChallenge.
    public void ExitBountyChallenge(bool showFailAnimation) { throw null; }
    // Replaces m_mainChallengeSet only when !set.isBounty; marks challenges [0..skip-1] complete (skip clamped to
    // Length - 1).
    public void LoadNewChallengeSet(ChallengeSet set, int skipToChallenge) { throw null; }
    // Activates the next challenge (Invokes its functionOnStart); activating a main set's last challenge runs
    // CompletedFinalChallenge.
    public void RefreshChallenges(bool skippedChallenges) { throw null; }
    private void CompletedFinalChallenge(bool skippedToChallenge) { throw null; }
    public int ChallengesCompleted() { throw null; }
    public void CheckChallenges() { throw null; }
    // With no challenge active and incomplete (right after CompleteChallenge) this falls back to the LAST one.
    public Challenge GetCurrentChallenge() { throw null; }
    public Challenge GetCurrentActiveChallenge() { throw null; }
    public bool HasCompletedThisBounty() { throw null; }
    public void CheckTeeRequirement(Ball theBall) { throw null; }
    // Does NOT advance: sets c.m_complete (no active check), bounty logic (when isBounty and c is the last
    // challenge or giveBountyRewardEarly, and the bounty is new: Complete(true) on every pad of that set,
    // m_completedBounties, reward, ExitBountyChallenge in Walk mode), Invokes functionOnComplete, auto-refreshes
    // only for Restart / Aim / m_autoAdvance, then SaveRun. Returns early (nothing marked) when
    // c.m_streaksRequired > 1 and the streak is short.
    public void CompleteChallenge(Challenge c) { throw null; }
    public void CompleteChallenge(Challenge c, float delay) { throw null; }
    private IEnumerator CompleteChallengeWithDelay(Challenge c, float delay) { throw null; }
    public void UpdateProgressBarForSave() { throw null; }
    public void ToggleOffPreviousChallengeObjects() { throw null; }
    public void AddBallStroke() { throw null; }
    public void ResetBallStrokes() { throw null; }
    public bool CompareStringsStart(string a, string b) { throw null; }  // SIG-GUESS parameter names
}

public class StreaksPanel : MonoBehaviour
{
    public static StreaksPanel instance;
    public StreakInfo m_streak;

    public void ChallengeSatisfied(bool streaked) { throw null; }
}

[Serializable]
public class StreakInfo
{
    public string m_name;            // TYPE-GUESS string
    public int m_currentStreak;      // TYPE-GUESS int (read by name)
    public int m_chitCount;          // TYPE-GUESS int
    public Transform m_chitHolder;   // TYPE-GUESS Transform
    public List<StreakChit> m_chits;
}

// TYPE-GUESS: base class MonoBehaviour (element of StreakInfo.m_chits).
public class StreakChit : MonoBehaviour
{
}

public class BountyPad : MonoBehaviour
{
    public Transform m_playerPosition;
    public StandHere m_bountyTee;
    public GameObject m_objectToEnable;
    public ChallengeSet m_bountyChallengeSet;
    public bool m_completed;
    private MeshRenderer m_screen;
    private const int m_nameSlot = 1;
    private MeshRenderer m_tick, m_cross, m_backLight;
    private TMP_Text m_nameText, m_rewardText;
    private BountyPad m_autocompletePad;
    private int m_autoCompleteShotLimit;
    private bool m_useBoundary;
    private float m_boundarySize;
    private GameObject[] m_unlocked;
    public GameObject[] m_locked;
    public Transform m_arrowHolder;
    private MeshRenderer m_arrow;
    private Transform m_hollowMonitor, m_realMonitor;
    private BountyPad m_padThatIsUnlocked;
    public bool ManualUnlock, ManualLock;
    private BountyBalancer bb;

    private void OnValidate() { throw null; }
    private void UnlockManually() { throw null; }
    private void GetBountyBalancer() { throw null; }
    private void LockManually() { throw null; }
    private void Start() { throw null; }
    public void SetText() { throw null; }
    public IEnumerator Active() { throw null; }
    public void InActive() { throw null; }
    public void Complete(bool checkAutocomplete) { throw null; }
    public void SetLock(bool unlocked, BountyPad pad) { throw null; }
    private void CheckAutoComplete() { throw null; }
    public void GoOffline() { throw null; }
    public void GoOnline() { throw null; }
    public void Incomplete() { throw null; }
    public void StartBounty() { throw null; }
}

public class BountyBalancer : MonoBehaviour
{
    public static BountyBalancer instance;
    public List<BountyValues> m_bounties;

    // The bounty reward; the same on easy (easy only also records m_completedOnEasy).
    public int GetDifficultyValue(Difficulty difficulty) { throw null; }  // SIG-GUESS parameter name
    public void UpdateBountyBoards() { throw null; }
}

[Serializable]
public class BountyValues
{
    public string id;
    public BountyPad bountypad;
    public Difficulty difficulty;
    public BountyTags tags;
}

[Flags]
public enum BountyTags
{
    None = 0,
    PrePutter = 1,
    PreDriver = 2,
    OuterArea = 4,
    Secret = 8,
    ManualEnable = 16,
    Par = 32,
    ParPlus = 64,
}
