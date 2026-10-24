# NGG map 4/4: recommended Harmony patch points
(BepInEx 5 / HarmonyX. Reference Assembly-CSharp.dll, ECM2.dll, com.rlabrecque.steamworks.net.dll, UnityEngine.*Module, Unity.InputSystem, DOTween from ...\Normal\Normal Golf Game_Data\Managed. Private members via AccessTools / string names.)

## Steam / boot
- No init patch. Poll static `SteamManager.Initialized`; the game pumps RunCallbacks in SteamManager.Update, so Callback<T>/CallResult work. Never SteamAPI.Init/Shutdown. SteamManager is DontDestroyOnLoad.
- Scene: SceneManager.sceneLoaded ("Main" gameplay, "Menu2" menu). World ready: postfix private `SaveManager.SetUpScene` (or public `SaveManager.LoadRunSave`).

## Player sync
- Poll: MoveAndHitController.instance.m_mode, m_FPSController.transform / m_fpsCharacter.GetPosition()/GetRotation()/IsCrouched(), FirstPersonCharacter._cameraPitch, golf m_golferHolder.position/rotation, m_selectedClub.clubName.
- Mode events: postfix MoveAndHitController.GolfTransition(bool) and private SwitchToFPS().
- Remote avatar: own GO; not named "First Person Character"; not layer "Ball"/"InteractTrigger"; no Rigidbody/trigger touching StandHere/DialogueTrigger/AchievementTrigger/TriggerSecurity/LukeVideo.

## Shot / ball sync
- Shot fired: postfix HitManager.HitBall(float angle, float power, bool isMiss, ShotType shot): capture m_ball.m_rb.position, linearVelocity, m_ball.m_ballSpinForce.force, club, shot type, spin type, PanelManager.instance.m_clubSwingPanel.m_power/m_loft, m_spinPanel.m_angle/m_spin, golfer yaw. (Earlier: prefix HitSequeneceManager.HitSequence or private ClubSwing.HandleShot(ShotType,float).)
- In flight: stream HitManager.instance.m_ball.transform while !m_rb.isKinematic.
- At rest: postfix HitSequeneceManager.CompleteSequence(Ball).
- Hole: postfix Ball.KillBall(); postfix HitSequeneceManager.ShowBallInHoleFeedback(int par, bool showAnyway) (strokes = m_ball.m_currentShot + 1).
- Reset: postfix HitSequeneceManager.Restart(bool); HitManager.ResetBall().
- Ghost skin: SaveManager.instance.m_unlocks.selectedBall -> Cosmetics.instance.m_balls[i].mat.

## Wind (host)
- Clients: prefix-skip WindPanel.NewWind() and WindPanel.WindCycle() (IEnumerator -> empty), set m_currentWind/m_desiredWind from host; apply after WindPanel.LateUpdate (postfix).

## Challenges / bounties
- Completion: postfix ChallengeSystem.CompleteChallenge(Challenge c) (single-arg). Id = CleanString.RemoveClone(ChallengeSystem.instance.m_challengeSet.name) + index.
- Load/switch: postfix ChallengeSystem.LoadNewChallengeSet(ChallengeSet,int); ExitBountyChallenge(bool); BountyPad.StartBounty().
- Bounty awarded: in CompleteChallenge and private BountyPad.CheckAutoComplete. Apply remotely: add to m_run.m_completedBounties, matching BountyPad.Complete(false), BountyBalancer.instance.UpdateBountyBoards().
- Tee: postfix StandHere.Activate() (m_currentTee stays local).
- Keep remote balls out of ChallengeSystem.m_ballsInPlay.

## Unlocks / economy
- Money: Bank.ChangeBalance(int change, bool halfSound, bool fast) (only mutator besides SetUpScene).
- Clubs: postfix ProgressManager.UnlockClub(Club c); ProgressManager.UnlockStance/UnlockPractice/UnlockAdjust(bool showPFX), Spin.Unlock(bool), WindPanel.StartWind(bool); shop ObjectStates.SetDriverAvailable/SetWedgeAvailable/SetHybridAvailable/SetTicketAvailable/EnableLMUGCMachine.
- Interactions: prefix FPSInteractable.Interact(); m_interaction (InteractionType); identify by hierarchy path or parent SaveableObject.m_id. Covers cash, buying, crowbar, padlocks, RFID, elevator, teleport, server cut, bunker, LMUGC, Luke.
- Bounty tier: postfix SaveManager.SetBountyPadStates(int).

## Doors / world
- postfix ObjectStates.SetGates(string,bool), OpenDoor(string), OpenOrCloseSlidingDoor(bool,string), ServerCut(), ServerRestore(), BunkerTerminal.SetScreenOpen(), Elevator.RideElevator(bool)/OpenTopDoors()/OpenBottomDoors(). Replay with reentrancy guard (they call SaveRun).
- Generic: diff SaveableObject set (m_id -> activeSelf, rotation), apply like SetUpScene. Postfix SaveManager.SaveRun() = "something changed".
- Dialogue: DialogueTrigger.OnTriggerEnter / DialogueManager.StartDialogue(DialogueLine,float); mirror story Invoke by method name (DialogueManager.instance.Invoke(name,0f)).
- Code door: postfix Authenticator.Start (private NewCode) on host -> send code; clients set Authenticator.code and m_text.text.
- Trivia: postfix TriviaBoard.OnEnable -> sync order/seed.

## Save
- Redirect paths: getters get_m_runSavePath, get_m_runSavePath_writeBackUp, get_m_normalGolfSavePath (public string props), get_m_settingsSavePath (private). Or prefix private SaveManager.WriteSave (IEnumerator). Steam Cloud syncs the folder (subfolders too).
- Client load: prefix SaveManager.LoadStoryRun()/LoadRunSave() to inject host RunSave JSON (JsonUtility.FromJson<RunSave>), let SetUpScene apply. Clients skip writing.
- WriteSave waits on m_moveLocked; don't hold it for remote dialogues.

## Menus / pause / time
- Pause: postfix PlayerPressedEscape.OpenMenu() restore Time.timeScale=1 in session; private HitSequeneceManager.RestartInput bails on timeScale==0. SettingsMenu/Screenshotter set timeScale.
- Quit: postfix PauseMenu.QuitToMenu() -> leave lobby. Menu injection: postfix MainMenu.Start().
- Mode: SaveManager.instance.m_gamemodeState.mode; MainMenu.PlayNine().

## FMV / endings
- private LukeVideo.OnTriggerEnter loads CREDITS for whoever hits Luke: gate in MP. TeleportManager.FadeDownEnding() also. GolfVideo/FirstPersonVideo local only.

## Achievements
- SteamManager.UnlockAchievement(AchievementID); guard replays with a "replaying" flag.

## LMUGC 9-hole round
- LMUGC.StartChallenge(); postfix LMUGC.CompleteHole(string id, int score); TeleportBallToHole(int,bool); FinishChallenge()/CompleteNormalGolfRound(); RetakeShot(bool). Each player plays own ball locally, exchange scores + ghost balls; host syncs wind and hole progression.

Decompiled game source is kept locally only and never committed.
