# Confirmations 1/2 (items 1-68 of NEEDS_CONFIRMATION.md, in file order)
Real build on the PC: 0 errors, 0 warnings, binding = game. All 64 by-name targets exist.
Legend: T true, F false (correct fact given), ? runtime check.

1 T. Both public static properties: Instance (creates GO "SteamManager" if null), Initialized => Instance.m_bInitialized.
2 T. top-level enum.
3 T. public GameObject cameraParent; public float _cameraPitch.
4 T. SetPosition(Vector3 position, bool updateGround = false); TeleportPosition(Vector3 newPosition, bool interpolating = true, bool updateGround = false); TeleportRotation(Quaternion, bool interpolating = true).
5 T. Golf=0, Walk=1.
6 F. ALL public incl. m_canRetakeShot, m_isInContact, m_ballHasBounced (bool), MeshRenderer m_meshRenderer, GameObject m_resumeBallTrigger, GameObject m_resumeBallEffect, ConstantForce m_ballSpinForce, TrailRenderer m_trail, m_chasingTrail.
7 T. public IEnumerator BonusFriction(), public IEnumerator SpinApplication(), public void ResetCurrentShot(), public void KillBall().
8 T. public float m_power/m_loft/m_backSpin; public void UpdateClub(); private float GetNewReturnRate(float oldRate); public void ResetArms().
9 T. public floats; private IEnumerator ApplyChaos()/ApplySpinChaos()/ApplyWiggleChaos(); public void ResolveFinalAngle(); Unlock(bool animate).
10 T. All public. m_fmvPanel: CompositeOverlay. m_clubPanel: ClubsPanelNavigation (public void Unlock(bool animate)).
11 F. HitSequence public IEnumerator (float angle, float power, bool isMiss, ShotType shot); Restart public; ShowBallInHoleFeedback public; RestartInput private; public Camera m_FMVCam; private Transform m_PIPCameraHolder; public ThirdPersonCamera m_tpc. m_showPIP is NOT on HSM: public bool m_showPIP {get;set;} on PanelAnimator (PanelAnimator.instance.m_showPIP).
12 T. All public. GetChallengeSet compares clean asset names; unknown -> LogError + null.
13 F. Only public int holeNumber; isHole, par, id are private.
14 ShotTarget class; ChallengeHelpers [Flags] {None0, ShowTarget1, ShowNoTeeReminder2, ShowSpinType4, NoCheeseAllowed8}; ScoreCardSave STRUCT; GameSettings STRUCT; ScoreCardEntry class {int score; string course} (+ unrelated UI class ScorecardEntry); BountyValues class {string id; BountyPad bountypad; Difficulty difficulty; BountyTags tags}; BallLooks class {Material mat; Sprite sprite; Color color}.
15 T. string, string, string, int (=1), int, int, float, bool.
16 Difficulty {SuperEasy, Easy, Medium, KindaHard, Hard, VeryHard, Nightmare, SuperNightmare, ActuallyImpossible}; Clubs [Flags] {None0, Driver1, Hybrid2, Iron4, Wedge8, Putter16}; Surfaces [Flags] {None0, Fairway1, Green2, Rough4, OuterRough8, Bunker16}; HoleResult {Ace, Albatross, Eagle, Birdie, Par, Bogey, DoubleBogey, TripleBogey, More}; Speakers {Unknown, Luke}; BountyTags [Flags] {None0, PrePutter1, PreDriver2, OuterArea4, Secret8, ManualEnable16, Par32, ParPlus64}. Club field: clubEnumSorry.
17 T. public bool IsFinalChallenge(), public void CheckTeeRequirement(Ball theBall), public bool CompareStringsStart(string,string) (instance), private void CompletedFinalChallenge(bool skippedToChallenge).
18 T. int; public List<BountyValues> m_bounties; UpdateBountyBoards public.
19 T. public StreakInfo m_streak (class: m_name, m_currentStreak, m_chitCount, m_chitHolder, List<StreakChit> m_chits).
20 T. top-level [Serializable] classes, public fields.
21 T. public string code; public TMP_Text m_text (code made in private NewCode() from Start).
22 T. OpenSequence/TryTrueEnding public IEnumerator; Server/Bunker/FinalDoorSequence private IEnumerator (); private IEnumerator PadlockSequence(string gateToOpen); private void LockPlayer(bool locking).
23 F. No static instance; reach via Teleporter.m_teleManager (public) or FindObjectOfType. public IEnumerator FadeDownEnding(), public IEnumerator TeleTransition(Teleporter te, GameObject objectToEnable, GameObject objectToDisable), public IEnumerator TeleportEndBattle(Transform t).
24 T. public GameObject m_bunker; private IEnumerator LukeHitSequence(); public void LukeLeaveSequence().
25 T. MainMenu.Start private; MainMenu PlayGame/Quit/PlayNineButtonPressed/PlayNine/ChosenWhatToDoWithDemoSave/StreamerModeSelected(bool) public; PauseMenu Exit/QuitToMenu/OpenSettingsMenu/RestartFromHole1 public; OpenMenu public.
26 T. public PlayerInput input; public bool m_isUsingGamePad; public Camera m_PIPCamera; public bool m_videoReadyToPlay; FirstPersonVideo: public VideoPlayer m_videoPlayer (GolfVideo's is private).
27 T. namespace Utilities, public static classes; RemoveClone(string s), GetString(string key).
28 T. All 3 DLLs present; TMP = Unity.TextMeshPro.dll (TMPro). No firstpass DLL.
29 T. They assign m_run themselves. With no file, LoadStoryRun only mutates m_run via ApplyStoryModeDefaults and does NOT call SetUpScene.
30 T. Plain JsonUtility JSON; Compress/Encode never called. SaveReader reads persistentDataPath+"/run2.txt" (own hardcoded property).
31 F. Extra writers: SetUpScene broken-save path (WriteAllText run2.txt from backup, or Delete run2 + temp, then LoadScene("Main")); SaveGameSettings (config.txt); RebindSaveLoad -> PlayerPrefs "rebinds"; DebugSaveSkip (editor buttons). UpgradeDemoSave only edits m_run. No quit handlers.
32 T. Only if active scene == "Main"; gameObject.SetActive(m_active); transform.rotation (world) when m_saveRotation.
33 F. PlayGame never DeleteRuns. Story: if HasRun && !m_hasCheckedForFirstSave shows the existing-save screen; if !HasRun (or after) the difficulty screen (DifficultySelect sets m_gameSettings.m_difficulty); then m_hasCheckedForFirstSave = true + SaveGameSettings, LoadScene("Main"). New run created in Main by ApplyStoryModeDefaults + DialogueManager.OpenSequence.
34 F. No reload: CompleteNormalGolfRound() then Exit().
35 T. m_cashValue is the price (BuyClub, LMUGCTicket, BuyMulligan/NoWind/HoleSkip) and the amount for CollectCash.
36 T. Same GetDifficultyValue(set.difficulty); easy only also records m_completedOnEasy.
37 F. aim<->UnlockAdjust right; m_clubPanel.Unlock pairs with m_putterUnlocked (SetUpScene; buying putter -> Unlock(true); CompletedDemo). Iron forced unlocked.
38 F/mostly presentation: Luke*/EndLuke*/ReturnLuke* = m_luke.PlayVideo(clip). Exception: EndLukeFadeDown -> LukeLeaveSequence = TRUEENDING achievement + timeScale 1 + FadeDownEnding -> CREDITS.
39 T. LoadNewChallengeSet replaces m_mainChallengeSet only if !isBounty; marks challenges[0..skip-1] complete (skip clamped to Length-1). ExitBountyChallenge re-instantiates live main set with skip = m_run.m_currentChallenge.
40 F. Not via m_activeBountyPad: if isBounty && (c is last || giveBountyRewardEarly) && !HasCompletedThisBounty, StopAllCoroutines + Complete(true) on every pad whose clean set name matches, adds to m_completedBounties, pays, PlayAnimation(1), ExitBountyChallenge if in Walk mode.
41 ? SetUpScene has no shop logic; standees likely persist via SaveableObject; check at runtime.
42 T. NewCode sets m_text.text = code; InputBoard (public Authenticator auth) compares currentInput == auth.code -> CompleteChallenge(GetCurrentChallenge()).
43 T. Random.Range + SetAsFirstSibling, in OnEnable (buttons instantiated there, destroyed in OnDisable).
44 Starts: StartWind (DialogueManager.UnlockWind; SetUpScene if m_windUnlocked||m_putterUnlocked); WindCycle restarts itself; StopAllCoroutines + StartCoroutine(WindCycle()) in BountyPad.StartBounty, ExitBountyChallenge, LMUGC.StartChallenge, SaveManager.LoadInLMUGC. m_desiredWind = WindCycle's lerp target (only while a set is loaded and current challenge has no m_forceWind). LateUpdate also calls NewWind() when the LMUGC no-wind timer expires.
45 F. id = BallTarget.id (GO name if empty); hole = FIRST number (Regex "\d+"); no number -> -1 -> IndexOutOfRange. m_currentShot 0-based.
46 F. PlayerPressedEscape.OpenMenu and debug Screenshotter (P key, editor/m_externalDebug). Back to 1: PauseMenu.Exit/QuitToMenu, SettingsMenu.Close (when not from pause)/DeleteStorySave/DeleteEverything, LukeVideo endings.
47 T. Only TryTrueEnding. True = random locked ball (of 5, index 0 forced) unlocked + selected + showNewBallAlert + unlocks.txt written; false = all unlocked.
48 T. GameMode {Story, JustGolf}; ControlMode {Golf, Walk}.
49 F. No save on quit.
50 ? Mod decision. Game reads only KeyCode.P debug (+ editor-only BackQuote/G/V/Alpha0); InputSystem bindings are asset data.
51 T. 0-based, not incremented by the shot. AddBallStroke (in Restart(true), only if m_ballHit) increments before the next shot; TeleportBallToHole(..,false) sets -1; RetakeShot +1 (penalty) / -1 (mulligan). Stroke = m_currentShot + 1 at HitBall postfix is correct.
52 F. Game uses the FIRST number; fallback is mod-only.
53 F. Not 0..1: ClubSwing.m_power clamped 0..m_currentClub.powerVisualMult (per club), x0.3 for Ground. Percent = power / m_clubSwingPanel.m_currentClub.powerVisualMult. HitWithPower tests m_power > 10.
54 F. Unity units (XZ ball to closest hole in front), labelled "yd" in the UI, no conversion; 999 if no hole; -1 after reset.
55 F. GetPosition at the feet. m_golferHolder is NOT at the player: SwitchToGolf sets golferHolder.position = FPS.position - (m_ballPosition.position - golferHolder.position), so the BALL lands on the walker's spot. Inactive m_FPSController / m_playerPos stay at that spot.
56 T. Two cycles can run (vanilla already double-starts).
57 F. Update never changes m_currentWind. Easing in WindCycle; LateUpdate overwrites for m_debugNoWind, LMUGC no-wind timer, and m_forceWind (= challenge.m_wind * m_maxWindSpeed).
58 F/caveat. Exit deactivates PauseMenu GO; OpenMenu activates m_objectToEnable (presumably PauseMenu). PauseMenu.OpenSettingsMenu deactivates PauseMenu while timeScale stays 0; SettingsMenu.Close reactivates it: "inactive" != "unpaused".
59 T. InteractChecker has no timeScale check; MoveAndHitController Switch only checks != 0; HSM Update/RestartInput bail at 0; ClubSwing uses legacy Input.GetMouseButton(0).
60 T. Both set timeScale 1. QuitToMenu: timeScale 1, SaveRun, LoadScene("Menu2"). Exit: InputManager.DisableInputTemporarily, deactivate, timeScale 1, MouseCursor.Lock if Walk.
61 T. public void OpenMenu(), Exit(), QuitToMenu(), Restart(bool isResumedShot), CompleteSequence(Ball ballThatSentMessage), ShowBallInHoleFeedback(int par, bool showAnyway).
62 T. Ball public Rigidbody m_rb / int m_currentShot / CurrentShotData / MeshRenderer m_meshRenderer; private FirstPersonCharacterInput m_fpsInput; public ClubSwing m_clubSwingPanel; public BallLooks[] m_balls with public Material mat.
63 T. public virtual bool IsCrouched(), public bool IsOnGround(), public Vector3 GetVelocity().
64 T. public bool m_forceWind; public Vector2 m_wind.
65 F. SaveManager.LoadInLMUGC (private, Invoked 0.1 s after SetUpScene when isInLMUGC) calls LMUGC.LoadInToChallenge, then TeleportBallToHole(first hole with lmugcScore < 1, true), Restart(false), m_isMultiShot = true, DisableAllTees, restarts WindCycle. Resumes at the first UNSCORED hole, not via StartChallenge.
66 T. Neither calls ExitChallenge. FinishChallenge duplicates the exit (m_endPoint teleport, UI swap, ExitBountyChallenge, isInLMUGC=false, noWindTime=0) without ResetScores/Restart/dialogue. CompleteNormalGolfRound: AddScoreToCard, ExitBountyChallenge, isInLMUGC=false, ResetScores, Restart(false), SaveRun.
67 ? No Tab in game code; dump InputManager.instance.input.actions bindings at runtime.
68 ? visual.
