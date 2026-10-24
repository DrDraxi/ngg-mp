# NGG map 2/4: core architecture, Steam, scenes, player, camera, swing, ball, shots
(From the on-device session's study of the decompiled game; global namespace, singletons `X.instance`. One local player + one ball by design. Unity 6 APIs.)

## Bootstrap + Steam
- SteamManager (Steamworks.NET template): lazy singleton, DontDestroyOnLoad, RestartAppIfNecessary(3510740), SteamAPI.Init, pumps RunCallbacks in Update, Shutdown in OnDestroy. `SteamManager.Initialized`. Mod must not Init; wait for Initialized.
- Achievements: SteamManager.Instance.UnlockAchievement(AchievementID) is the single choke point. enum AchievementID { COMPLETE_LMUGC, FULL_BAG, PUTTER, PADLOCK, ACE, ALBATROSS, EAGLE, BIRDIE, PAR, PARKOUR, TALL_BALL, SKIPPER, MISSING_IN_ACTION, CODE, TOILET, CREDITS, TRUEENDING, SCRATCH, CORNER, COMPLETE }. AchievementTrigger checks collider name "First Person Character" or "Ball".
- MainManager targetFrameRate 120. BuildSettings demo/playtest flags.
- GameModeState : ScriptableObject { GameMode mode } enum GameMode { Story, JustGolf }; SaveManager.instance.m_gamemodeState.mode. MainMenu.PlayNine() = JustGolf.

## Scenes
- 0 INTRO, 1 Menu2, 2 Main, 3 CREDITS; all SceneManager.LoadScene(string) sync. MainMenu.PlaySetUp -> "Main"; PauseMenu.QuitToMenu -> "Menu2"; TeleportManager.FadeDownEnding & LukeVideo -> "CREDITS"; SaveManager.SetUpScene reloads "Main" on broken save.
- One open-world scene "Main". Tees = StandHere via GreatConnector.instance.GetTee(id) ("RedTee", "Hole2Tee".."Hole9Tee"); cups = BallTarget{isHole, holeNumber, par}; LMUGC.m_pars (private int[9]). GreatConnector.instance.m_allChallenges (ChallengeSet[]; GetChallengeSet(name)); also registries of tees, DialogueTriggers, targets, decorations, PhysicalClubs, PhysicsChallengeObjects, PIP locations, m_elevator; string ids = stable network ids.

## Player
- MoveAndHitController (singleton): ControlMode m_mode {Golf, Walk}; GameObject m_FPSController (named "First Person Character"); ECM2.Character m_fpsCharacter; Transform m_golferHolder (golf stance root); Transform m_ballPosition; Club m_selectedClub; bool m_moveLocked (dialogue/cutscene, blocks saving); m_cantSwitch; BountyPad m_activeBountyPad; m_playerPos, m_ballPos; FirstPersonVideo m_fpsVideo.
- ECM2.Examples.FirstPerson.FirstPersonCharacter : ECM2.Character (GetPosition/SetPosition/TeleportPosition/GetRotation/SetRotation/GetVelocity/IsOnGround/IsCrouched/IsJumping; _cameraPitch, cameraParent). FirstPersonCharacterInput (m_fpsCam), FirstPersonCharacterLookInput.
- Mode switch in MoveAndHitController.Update (action "Switch"); GolfTransition(bool isResumedShot) -> private SwitchToGolf(bool) (places golferHolder, hides FPS, HitManager.ResetBall, SetGroundSurface); coroutine FPSTransition -> private SwitchToFPS. ResumeShot(), FPSTransitionForDialogue(), FPSTransitionForBounty(), Adjust(bool left).
- InputManager.instance.input (PlayerInput) actions: Move, Switch, Use, Reset, Adjust, Practice, Pause, Right Joystick; m_isUsingGamePad. Swing also uses legacy Input mouse.
- InteractChecker.Update raycasts layer "InteractTrigger" -> FPSInteractable.Interact().

## Camera
- Walk: FPS cam FOV 80. Golf: 2D swing panel (PanelManager + ClubSwing), golfer is FMV (GolfVideo VideoPlayer->RenderTexture), PIP ball cam PanelAnimator.instance.m_PIPCamera; HitSequeneceManager.m_FMVCam, m_PIPCameraHolder, m_showPIP, ThirdPersonCamera m_tpc; MainCameraManager. Remote players need mod-made avatars.

## Swing
- ClubSwing (PanelManager.instance.m_clubSwingPanel): FixedUpdate integrates club; public m_power, m_loft, m_backSpin, int m_groundType (0 normal,1 sand,2 rough,3,4), m_isPracticeMode, m_isPanelActive, Club m_currentClub. private DetectShot() -> private HandleShot(ShotType, float backSpinMultiplier) -> HitSequeneceManager.instance.HitSequence(m_angle, m_power, false, shotType); ChallengeSystem.instance.m_ballsInPlay.Add(ball).
- ShotType flags { None=0, Topped=1, Thin=2, Good=4, Fat=8, Ground=16, Miss=32, Perfect=64 }.
- Spin (PanelManager.instance.m_spinPanel): m_angle, m_spin, m_spinForceMultiplier; RANDOM chaos (ApplyChaos/ApplySpinChaos/ApplyWiggleChaos), ResolveFinalAngle bunker wiggle.
- Club : ScriptableObject (clubName Driver/Hybrid/Iron/Wedge/Putter, Clubs flags, physics params, cameraFOV, balloon, loft, dist...). ClubSwing.UpdateClub(). TweaksPanel.GetForwardPositionFactor/ResetPosition.

## Shot sequence
- HitSequeneceManager.HitSequence(float angle, float power, bool isMiss, ShotType): ball.m_ballHit, SaveManager.instance.m_run.m_shots++, LMUGC bookkeeping + SaveRun, ChallengeSystem.CheckTeeRequirement(ball), GolfVideo.PlayHit, ~0.45s HitManager.HitSimulationBall, ~0.867s HitManager.HitBall(angle, power, isMiss, shot).
- HitManager (public Ball m_ball): HitBall computes launch from loft, spin angle, golferHolder yaw; spawns Clod (random torque); sets Physics.bounceThreshold GLOBALLY; AddForce; m_ballSpinForce.force; m_isMultiShot; SpinApplication. ResetBall(); GetSpinType() SpinType {None, Straight=1, Slice=2, Hook=4, Draw=8, Fade=16}.
- HitSequeneceManager.CompleteSequence(Ball) when ball stops -> m_finishedDistance, m_shotComplete, ChallengeSystem.CheckChallenges(). Restart(bool isResumedShot). ShowBallInHoleFeedback(int par, bool showAnyway) -> ParFeedback.

## Ball
- Ball (GO "Ball", layer "Ball"): m_rb, ConstantForce m_ballSpinForce, CurrentShotData m_currentShotData, m_ballHit, m_isMultiShot, m_canRetakeShot, m_isInContact, m_ballHasBounced, int m_currentShot, trails, m_meshRenderer, m_resumeBallTrigger ("InteractableBall").
- FixedUpdate: balloon lift from shooter's panel state, wind from WindPanel.m_currentWind by height over "GolfCourse" layer, swaps GLOBAL TerrainCollider.material per splat.
- Triggers: "BrushCollision" (RANDOM force), "HoleSoundEffectTrigger", Barrier; collisions: surfaces, gong, BulletProof. BonusFriction coroutine -> kinematic -> CompleteSequence. SpinApplication, KillBall, ResetCurrentShot.
- CurrentShotData { m_touchedSurfaces, m_lastTouchedSurface, m_finishedDistance, m_hitWithCorrectClub, m_shotType, m_spinType, m_shotComplete, m_hitTarget, m_shotFromCorrectTee, m_hitFromLocation }.
- BallTarget.OnTriggerEnter (layer Ball) marks ShotTarget.satisfied; isHole -> HitManager.instance.m_ball.KillBall() + par feedback / LMUGC.CompleteHole(id, strokes) (uses HitManager's ball, not the collider). Breakable, BallGlass, BallHitButton, PhysicsChallengeObject, gongs react to layer Ball.
- Remote ball ghosts: different layer and name, no Ball component, kinematic interpolated.

## Wind
- WindPanel (PanelManager.instance.m_windPanel): Vector2 m_currentWind, m_desiredWind, m_forceMutiplierOfWindOnBall, m_maxWindSpeed; NewWind() random; WindCycle coroutine. Challenge.m_forceWind/m_wind. LMUGC no-wind assist. Host-authoritative.
