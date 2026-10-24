# Confirmations 2/2 (items 69-149 of NEEDS_CONFIRMATION.md, in file order)
Legend: T true, F false (correct fact given), ? runtime check.

69 T. LoadRunSave dispatches by mode and assigns m_run. Callers: SaveManager.Start and MainMenu.Start only. GetBackUpStorySave does NOT assign m_run; only caller is SetUpScene's broken-save path.
70 T. Plain JSON (SaveReader hardcoded path).
71 F. Add writers: SetUpScene broken-save path, SaveGameSettings, RebindSaveLoad (PlayerPrefs), DebugSaveSkip. JustGolf branch of WriteSave reads m_normalGolfSavePath at write time (after the moveLocked wait).
72 T. SaveManager not DontDestroyOnLoad; Awake sets instance only if null.
73 T. MainMenu.Start private; order vs SaveManager.Start (also LoadRunSave) undefined.
74 F/addition. Broken = Story && m_challengeSetLoaded == "" && (finishedDemo || completedBounties.Count > 0). Non-empty clean name avoids it. UNKNOWN name -> GetChallengeSet null -> Instantiate(null) throws inside SetUpScene, skipping everything after (bank, position, unlocks, pads, LMUGC resume, retro achievements).
75 T, with settings caveat (#58).
76 T. Only PlaySetUp loads Main from Menu2. Loading Main directly skips prompts, m_hasCheckedForFirstSave + SaveGameSettings, music fade. Mode must already be set on the shared GameModeState.
77 T. public Bank m_bank; private FirstPersonCharacterInput m_fpsInput; public ClubSwing m_clubSwingPanel. All MonoBehaviours.
78 T. public void SetMovementDirection(Vector3 movementDirection).
79 T. ScoreCardSave struct; game round-trips all three.
80 T.
81 T/? Credits ends with LoadScene("Menu2"); CREDITS uses SaveReader (SaveManager presence there = runtime check). Neither ending loads Main.
82 F. Only OpenMenu + debug Screenshotter set 0. No save on quit.
83 T. GetSaveData virtual, no overrides, no side effects.
84 ? Steam Auto-Cloud syncs at launch/exit.
85 T.
86 F. CompleteChallenge does NOT advance. Sets c.m_complete (no active check), updates UI, bounty logic + sound, Invokes functionOnComplete, auto-refreshes only for Restart/Aim/m_autoAdvance, then SaveRun. Next challenge activates in RefreshChallenges (from Restart, AutoRefreshChallenges, LoadNewChallengeSet). Streak gate: if m_streaksRequired > 1 and streak short, returns early.
87 F. Use GreatConnector.instance.GetChallengeSet(string) / GreatConnector.instance.m_allChallenges (public ChallengeSet[]). isBounty on ChallengeSet; m_shortString on Challenge.
88 T. Clean names. Extra writer: DialogueManager.UnlockTeleporter adds string m_crossCountyChallenge.
89 T. private ChallengeSet LMUGCChallenge; m_run.isInLMUGC.
90 T. activeSelf; world rotation.
91 F. No ElevatorManager. Elevator.m_bunker (public GO, via GreatConnector.instance.m_elevator), toggled by RideElevatorSequence, turned off by Apply*Defaults. Challenges toggle GreatConnector targets, decorations, physics objects, tees, dialogue triggers. Full survey = runtime.
92 F. InteractionType 0..21 as documented. Money: CollectCash +m_cashValue; FindMissingBall +1000 hardcoded; BuyClub, LMUGCTicket and the 3 assists -m_cashValue. GetCard has no handler.
93 T. m_balance += change synchronously. halfSound true -> volume 1.0, false -> 0.5 (inverted name). fast shortens animation. ChangeBalance(0, true, true) flashes on "can't afford".
94 F. club panel <-> putter (#37).
95 F partly. UnlockStance, UnlockPractice, UnlockAdjust, Spin.Unlock, ClubsPanelNavigation.Unlock and CheckRFIDDoors idempotent (only re-spawn PFX). StartWind NOT: each call starts another WindCycle and re-rolls Random wind. ObjectStates Set*Available spawn PFX + sound each call.
96 ReturnLuke0, 1, 1a (m_returnClips[5]), 2, 3, 4. DeductMoney = ChangeBalance(-100000, true, false) only; ReturnMoney = ChangeBalance(+100000, true, false) only.
   Invoke targets with state effects:
   - HideCar (decoration "Car" off); CloseGates/OpenGates ("securityGate").
   - UnlockStance, UnlockPractice, UnlockWind (StartWind + m_windUnlocked + SaveRun), UnlockSpin, UnlockAdjust.
   - CompletedDemo; OpenShop (pro-shop door GOs + DEMO/BOUNTYTUTE triggers); OpenHole1Gates/OpenHole2Gates.
   - EnableGreenTeeShort/Long, EnableRedTeeForMultiShot, EnableWedgeTutorial2Tee/3Tee, EnableHybridTutorial2Tee (ActivateTee).
   - UnlockBounties1/2/3; DriverAvailable, WedgeAvailable, HybridAvailable, TicketAvailable; UnlockLMUGC; TicketReady (trigger "LMUGCREADY").
   - TeleportCarpark, TeleportMD, TeleportMDTrap, EnableTrap (trigger "MarketingTrap"); StartTeleporterDialogue.
   - UnlockTeleporter: trigger "WEDGEAVAILABLE", doors hole5Shed + bathroom, LoadNewChallengeSet, adds a completed bounty, completes the cross-country pad.
   - OpenBarDoor (door "bar" + trigger "STAIRS"); EnableTopOfMountain; OpenMarketingDept (sliding door "MarketingDoor" + trigger "MarketingMonologue" + SaveRun).
   - RestoreBounties (ServerRestore + server screens); Achievement*; EndLukeFadeDown (ending).
   Presentation only: LookAt*, ShowNullRef, ShowConfettiNullRef, Show/HidePowerRequirement, Luke*, EndLuke*, ReturnLuke*.
   Public: PlayCarDialogue2 (sets m_playedCar2), ResetShot, UnlockDemoPads.
97 F. Also LukeVideo.LukeLeaveSequence (via EndLukeFadeDown) calls FadeDownEnding. IMPORTANT: LukeVideo.OnTriggerEnter(Collider) has NO filter: any collider entering Luke's trigger starts LukeHitSequence -> SaveRun with m_hitLuke = true -> CREDITS. Remote avatars and ghost balls must not have colliders or rigidbodies that can reach it.
98 T. 99 T.
100 T. Only TryTrueEnding calls UnlockNewBall; writes LOCAL unlocks.txt, uses Random.
101 T. Single overload UnlockAchievement(AchievementID achievement); private void AchievementsRetroUnlock().
102 T. All public void; each ObjectStates door method ends with SaveRun.
103 T. Plain local += 3 / ++ / ++ on m_run, then deactivate + SaveRun.
104 T. 105 ? 106 T. 107 T.
108 F (#31). No save on quit.
109 T. 110 T. No DeleteRun. 111 F. No reload.
112 F partly. Settings submenu open: PauseMenu inactive but still paused; SettingsMenu.Update unlocks cursor. Other exits: SettingsMenu.Close -> pause menu; QuitToMenu, DeleteStorySave, DeleteEverything load a scene.
113 F. Only debug Screenshotter; SettingsMenu only sets 1.
114 T.
115 ? scene data (found via FindObjectsByType). Other code reads ClubSwing fields while disabled.
116 T. SaveRun copies m_bank.m_balance into m_run.m_bankBalance.
117 T. 118 T. 119 F (putter). 120 T (EndLukeFadeDown exception). 121 T. 122 F (#40). 123 ? (#41). 124 T. 125 T.
126 F. First number; 0-based.
127 F. Range 0..powerVisualMult (>10 occurs). Use m_power / m_currentClub.powerVisualMult.
128 T. 129 T.
130 ? BallLooks.mat confirmed; URP; ObjectStates checks _BaseColor first, then _Color.
131 ? 132 ? 133 T.
134 T. public ControlMode m_mode.
135 T. Snapshot + JSON synchronous. SaveRun StopCoroutine(_writeSave) first: only newest pending write survives. Not moveLocked -> writes synchronously inside StartCoroutine.
136 T. Caveat: scene change while waiting destroys SaveManager; write lost.
137 F. LMUGCTicket COSTS money: ChangeBalance(-m_cashValue), or ChangeBalance(0) when unaffordable. Crowbar: no money. GetCard: no handler.
138 T. Affordable: exactly one ChangeBalance(-m_cashValue, true, true), synchronous in Interact. Unaffordable: one ChangeBalance(0, true, true) + error. Same for ticket and assists.
139 F. Score = sum of 9 lmugcScore entries, unplayed holes = par+2, so >= 9. AddScoreToCard appends {score, course "NormalFront9"} and immediately File.WriteAllText(scoreCard.txt), bypassing WriteSave. Needs m_scoreCardSave.scores non-null (GetScoreCardSave; LoadInToChallenge calls it in JustGolf).
140 F. FinishChallenge doesn't write bests. CompleteHole does, Story only, once hole 9 scored, before UpdateBestScores + FinishChallenge: if (num < best || best < 9) best = num; same for NoAssist when !lmugcUsedAssist. JustGolf -> CompleteNormalGolfRound, never updates them.
141 F. After CompleteChallenge no challenge is active-and-incomplete, so GetCurrentChallenge() falls back to the LAST challenge. Call RefreshChallenges(false) between completions, or jump with LoadNewChallengeSet(set, index).
142 T. functionOnComplete can run story code (UnlockTeleporter calls LoadNewChallengeSet). Activating a main set's last challenge runs CompletedFinalChallenge. Activation Invokes functionOnStart. DialogueLine.challengeSetToLoad can also load sets.
143 F (likely). LMUGC set is a bounty set: LoadNewChallengeSet keeps m_mainChallengeSet; SaveRun keeps m_challengeSetLoaded on story/ML_NormalGolf track; m_challengeSet = LMUGC set. ChallengeSystem.Update returns early while isInLMUGC. Confirm LMUGCChallenge.isBounty at runtime.
144 F. SetUpScene broken-save path also deletes run2.txt + run2_temp.txt (no valid backup) and reloads Main. DeleteRun/DeleteEverything called from SettingsMenu.DeleteStorySave (-> Menu2) and DeleteEverything (-> INTRO).
145 ? BepInEx 5 manager DontDestroyOnLoad.
146 T. HarmonyX 2.9.0.0: instance Unpatch(MethodBase, MethodInfo), instance GetPatchedMethods(), static GetAllPatchedMethods(), static GetPatchInfo(MethodBase), UnpatchSelf().
147 ? OpenMenu m_objectToEnable.SetActive(true) synchronously; instance count = scene data.
148 T. Hierarchy order same scene; FPSInteractables never instantiated at runtime.
149 T. DeductMoney/ReturnMoney don't SaveRun; dialogue end (NextButtonPressed) does.

Runtime checks pending: #41, 58, 67, 115, 130, 131, 143, 147, pause object.
