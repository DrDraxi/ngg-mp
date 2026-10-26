# NGG map 8: "Just Play Nine" (GameMode.JustGolf) and the LMUGC 9-hole round
(From the on-device study of the decompiled game and a runtime check in a test copy, 2026-10-26. Paraphrased.)

## Short version
- Each player plays their own ball. The stroke counter is `Ball.m_currentShot`; a hole's score is `m_currentShot + 1`
  when the ball drops.
- Between strokes the player walks to the ball and uses its "Next Shot" interaction (`FPSInteractable` with
  `InteractionType.ContinueShot`, the GameObject `InteractableBall` = `Ball.m_resumeBallTrigger`). That is the only
  place a new stroke is counted and the swing panel is switched on.
- Between holes the ball moves to the next tee once the player leaves golf mode; the player walks there. The player is
  only teleported to the hole-1 tee ("RedTee") at the start (and on a reload, to the current hole's tee).
- No stroke limit, no pickup, no out-of-bounds. Water (and a wrong cup) kill the ball: the reminder says "Ball in
  water", the player opens the Space menu and picks "Retake Shot - 1 shot penalty" (counter +2).
- Mulligan, no-wind and hole-skip assists are never available in JustGolf.

## LMUGC (`PanelManager.instance.m_LMUGCPanel`)
Fields (private unless noted): `int[9] m_pars` (runtime 3,4,3,4,4,5,4,4,3 = 34), `ScorecardEntry[] m_scoreCards` (HUD
cards), `GameObject m_challengeUI, m_LMUGCUI`, `TMP_Text m_challengeHeading` ("ROUND #n"), `ChallengeSet LMUGCChallenge`
(behaves as a bounty set), `Color m_parColor, m_underColor, m_overColor, m_noScoreColor`, `TMP_Text m_totalScoreText`,
`ScorecardEntry[] m_scoreCardsMachine`, public `HandicapSign m_handicapSign`, `bool m_ballInHole` (while
WaitingToTeleportBall runs), `bool forceTeleport`, public `Vector3 m_shotFromPosition`, public `Quaternion
m_lastShotRotation`, Space-menu buttons `m_retake, m_mulligan, m_noWind, m_skip`.

- `Update()` (while `m_run.isInLMUGC`): ball mesh disabled (killed) -> reminder `Challenges/Ball in water LMUGC`, else
  `Challenges/Go to the ball` with the arrow on the ball. `ChallengeSystem.Update` returns early in LMUGC.
- `StartChallenge()`: from `FPSInteractable.Interact` with `InteractionType.StartLMUGC` (the red button of
  `!!!Bountys/lmugcMachine`, near (92.0, 2.3, -306.8)). Exits a loaded bounty, `isInLMUGC = true`, `ResetLMUGCSave`,
  `ResetScores`, `LoadInToChallenge`, `TeleportBallToHole(1, true)` (ball and player on RedTee, golf mode), 10 s camera
  move (`m_cantSwitch`), `Restart(false)` -> panel on: **the hole-1 tee shot needs no interaction**.
- `LoadInToChallenge()`: loads `LMUGCChallenge`, hides `m_challengeUI`, shows `m_LMUGCUI`, the multi-hit header.
- `TeleportBallToHole(int hole, bool teleportPlayer)`: tee id `"Hole" + hole + "Tee"`, hole 1 `"RedTee"`
  (`GreatConnector.instance.GetTee(id)`, works on inactive tees). Ball kinematic on the tee, trails cleared,
  `ResetCurrentShot`, `m_shotFromPosition` = tee. `teleportPlayer`: `ResetBallStrokes` (0), player on the tee in golf
  mode. Otherwise `m_currentShot = -1` and the resume ring + `InteractableBall` on (first "Next Shot" makes it 0).
- `CompleteHole(string id, int score)`: from `BallTarget.OnTriggerEnter` (and `UseHoleSkip`). Hole = first digit run of
  `id` (`ExtractNumber`). `m_canRetakeShot = false`; hole < 9: start `WaitingToTeleportBall(hole)`;
  `lmugcScore[hole-1] = score`, `UpdateScoreCard`. Hole 9 in JustGolf: `CompleteNormalGolfRound()`.
  Before it, `BallTarget` calls `ShowBallInHoleFeedback(par, false)` (ParFeedback knows Albatross..TripleBogey only) and
  after it shows `m_goToNextHole` ("Go to the next tee. The ball is waiting for you!").
- `WaitingToTeleportBall(int hole)`: `m_ballInHole = true`; yields while `m_mode == Golf && !forceTeleport`; then
  `TeleportBallToHole(hole + 1, false)`, `m_ballInHole = false`. No timer.
- `UpdateScoreCard()`: cards with a score > 0 get the number and a backing by score - par (under / par / over colour),
  others `m_noScoreColor`; total on HUD and machine.
- `CompleteNormalGolfRound()` (end of hole 9; also `PauseMenu.RestartFromHole1`): unscored holes get par + 2 and then
  the player is teleported to `m_handicapSign.m_playerPosition` (only when a hole was unscored); `AddScoreToCard(total)`,
  board update, UI swap back, `ExitBountyChallenge(false)` (walk mode, main set `ML_NormalGolf`, "Press button to start
  round"), `isInLMUGC = false`, `ResetScores`, `Restart(false)`, `SaveRun`. No dialogue, no results screen.
- `RetakeShot(bool isMulligan)`: ball back to `m_shotFromPosition`, `Restart(true)` (+1), not a mulligan: +1 penalty.
- `UpdateButtons()`: Retake interactable only if `ball.m_canRetakeShot && !m_ballInHole`.
- No current-hole field: the current hole is `1 + count(lmugcScore[i] > 0)` while `isInLMUGC`.
  `BallTarget.OnTriggerEnter` scores only when its public `holeNumber` equals that.

`PauseMenu.m_quitRoundButton` ("Restart From Hole 1", JustGolf in a round only) ends and records the round with par + 2
for the unplayed holes; it does not start a new one. A reload mid-round resumes at the current hole's tee
(`SaveManager.LoadInLMUGC`, private).

## Start of a JustGolf game
`MainMenu.PlayNine()` -> mode JustGolf -> Main -> `LoadNormalGolfRun` (fresh: `ApplyGolfModeDefaults`, everything
unlocked, set `ML_NormalGolf`) -> `SetUpScene`; `isInLMUGC` -> `Invoke("LoadInLMUGC", 0.1f)`. Fresh spawn in the car
park (58.32, 2.51, -380.22); the player walks to the machine and presses the button. `HandicapSign` (`Main:handicapSign`,
public `Transform m_playerPosition` at (91.43, 2.49, -317.69)) shows the last 20 rounds.

## Shot cycle
1. `ClubSwing.FixedUpdate` -> private `DetectShot()` (if `m_isPanelActive`) -> ground strike `HitSequence(...)`, or
   private `HandleShot(ShotType, float)` -> `HitSequence` (not in practice mode).
2. `HitSequence` (iterator): `m_canRetakeShot = true`, `m_shots++`, LMUGC stores `m_shotFromPosition`; hides the panels;
   `HitBall` at ~0.87 s. The swing does not change the stroke counter.
3. Ball stops: `CompleteSequence(ball)`: "Walk to the ball to continue." (`m_multihitMenu`, PANEL_PIP) if the mesh is
   visible, then `ShowAndAllowResumeShot(true)`. Nothing continues by itself.
4. The player walks to the ball, "Next Shot" (`InteractChecker` prefers a collider named `InteractableBall`, layer
   InteractTrigger): `FPSInteractable.Interact()` (`ContinueShot`): `HitManager.instance.m_ball = m_thisBall`,
   `MoveAndHitController.ResumeShot()` -> golf mode, `Restart(true)` -> `AddBallStroke()` (+1), panel on.
5. Golf mode away from the ball works (Switch anywhere), but the panel stays off: no swing.

Stroke counter: 0 = `ResetBallStrokes` / `TeleportBallToHole(_, true)`; -1 = `TeleportBallToHole(_, false)`; +1 =
ResumeShot / RetakeShot; +1 more = retake penalty.

**Block a swing out of turn:** prefix `FPSInteractable.Interact()` for `ContinueShot` (nothing changed yet); prefix the
private `ClubSwing.DetectShot()` and `HandleShot(ShotType, float)` (hole-1 tee shot, reload tee shot, the harness);
prefix `LMUGC.RetakeShot(bool)`. Never block Switch / `GolfTransition` (the next-hole move waits on the mode) and never
make a `HitSequence` prefix return false (iterator).

**Hold the next hole:** prefix `LMUGC.TeleportBallToHole(int, bool)` with `teleportPlayer == false`: return false and
remember the hole, call it later. The coroutine clears `m_ballInHole` anyway. While holding, `LMUGC.Update` says "Ball
in water LMUGC" (ball hidden in the cup): override the reminder in a postfix.

## Positions
Tees: "RedTee" (82.33, 2.54, -309.5), "Hole2Tee" ball at (123.89, 7.82, -126.58); others via `GetTee`.
Cups (`Main:3D Course/HOLES/HOLE_HoleN`, `BallTarget` with private `isHole`, public `holeNumber`, private `par`; other
cups exist, filter by holeNumber 1..9):

| Hole | Cup | Par |
|---|---|---|
| 1 | (87.21, 3.19, -176.44) | 3 |
| 2 | (379.23, 18.51, -83.66) | 4 |
| 3 | (364.55, 15.30, -176.36) | 3 |
| 4 | (389.07, 20.86, -505.48) | 4 |
| 5 | (165.32, 3.70, -538.79) | 4 |
| 6 | (-282.87, 4.92, -535.79) | 5 |
| 7 | (-229.31, 16.28, -280.83) | 4 |
| 8 | (-68.00, 10.14, -452.80) | 4 |
| 9 | (0.35, 1.99, -302.43) | 3 |

Distance to the cup for "farthest plays": horizontal, ball to the current hole's cup; a killed ball counts from
`m_shotFromPosition`. `CompositeOverlay.GetDistanceToHole()` uses the cup in front of the local golfer (unsuitable).

## UI
- `Main:PANEL_LMUGC/Canvas` (world space, golf view, top right): `ChallengeUIHolder/HEADING` ("ROUND #1",
  Quantico-NO DROP 26, black on the green `Bounty Header` rgba(0.133,0.718,0.267), rounded9slice), `ScoreCard`
  (GridLayout cells 55 x 160, spacing 6): `LABELS` (HOLE / PAR / SCORE), `HoleCard 1..9` (names vary: "HoleCard2 ",
  "HoleCard5"...; children `HoleNumber/TXT` green rgba(0.137,0.725,0.278) on that green at alpha 0.263, `TXT` par in
  yellow rgba(0.969,0.729,0.008), `SCORE` backing (no-score grey 0.585) with white `TXT`, `FRAME` frame9sliceHollow),
  `LABELS (1)` (TOTAL). Quantico-Regular SDF 20 / 26. `Hold Reset/GiveUp` "Press Space to see options".
- `Main:PANEL_PIP/Canvas` (world space): `Multihit Retry` (= `m_multihitMenu`, "Walk to the ball to continue.") and
  `Go To Next Tee` (= `m_goToNextHole`): orange rgba(0.953,0.518,0) rounded9slice with DiagonalsUIWhite, Quantico 24.
- `PANEL_MULTIHITMESSAGE` is a choice box (`m_multiHitMenu`), not a banner.
- Walk HUD `PROMPTS/USE`: "<Tab> to take Next Shot" (stringID "Next Shot").
- Walk HUD `ASSISTS` (inactive in JustGolf): white frame9slice FRAME, heading Quantico 36, rows of rounded9slice in
  green / blue / pink with Quantico-NO DROP 36 labels.
