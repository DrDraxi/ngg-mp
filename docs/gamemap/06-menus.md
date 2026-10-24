# NGG map 6/6: menus, UI widgets, input navigation, localization, Steam
(From the decompiled game plus runtime `uitree`/`buttons`/screenshot dumps of a test copy, 2026-10-21; dumps and screenshots
are on the `screenshots` branch under `menus-20261021/`. Global namespace unless noted. Paths are `Scene:Canvas/...`.)

## Shared building blocks
- Every screen is a ScreenSpaceOverlay Canvas with CanvasScaler ScaleWithScreenSize, reference 1920x1080, matchWidthOrHeight 1
  (height). Menu2 has two root canvases: `Main Menu` (sortingOrder 0) and `SETTINGS MENU` (999). Main has `PAUSE MENU` (998) and
  its own `SETTINGS MENU` (999) among ~335 world/panel canvases.
- Fonts: TMP_FontAsset `Quantico-Regular SDF` (material `Quantico-Regular Atlas Material`) on virtually every label;
  `Quantico-Dilated` (same material) only for the small red "!" notification dots. Labels are white, Center, auto-size on,
  no Outline/Shadow component (any look of depth comes from the button sprite). Rich-text `<wave>` etc. come from
  Febucci TextAnimator_TMP on a few texts.
- Sprites (all Image, Default UI Material): `button` (Sliced; the rounded coloured button with its darker bottom edge baked in,
  tinted by Image.color), `buttonHighlight` (Sliced white outline, the gamepad/keyboard selection frame), `glass_0` (Simple; the
  dark green translucent panel fill), `frame9sliceHollow` (Sliced; the gold/beige rim drawn over a panel), `frame9slice` (Sliced;
  the solid rimmed dialog used by pause/settings frames), `diagonals` (Tiled; the dark striped fullscreen backdrop),
  `rounded9slice`, `buttonBacking_0` (settings row backing), `tick`, `Circle`, `Knob`/`Background`/`UISprite` (sliders),
  `gamepadArrow_0`.
- Button recipe (all menus): Image(sprite `button`, Sliced, colour = the button colour) + UnityEngine.UI.Button(transition
  ColorTint, normal white, highlighted/selected 0.961 grey, pressed 0.784, fadeDuration 0 on the main menu; navigation mostly
  Explicit on the main menu, Vertical/Automatic elsewhere; onClick = persistent call into the screen script) +
  ButtonPunchAnimator(m_dontPlaySound false) + child `LABEL` (RectTransform stretch, TextMeshProUGUI). No EventTrigger
  components anywhere. Colour is purely Image.color on the shared sprite.
- Sounds: AudioManager (scene singleton `AudioManager.Instance`, one per scene, object `Audio Manager`; not DontDestroyOnLoad)
  holds `Sound[] m_sounds` (name, clip, min/max volume and pitch, priority) and gives each its own AudioSource on the
  Audio Manager GameObject (routed to `m_sfxMixer`). `PlaySoundEffect(string name[, float volumeMod])` picks the Sound by
  m_name and PlayOneShot()s it with random volume/pitch; it is silently skipped while Time.timeSinceLevelLoad < 0.5.
  `PlayButtonSelectSound()` = PlaySoundEffect("click"); `PlayButtonPressedSound()` = StopSoundEffect("click") then
  PlaySoundEffect("click") (same clip). Other UI names used: "pop" (difficulty pick, DVD wall bounce), "driverHit" (difficulty
  confirm), "special" (DVD corner hit), "clubSelect", "openPanel", "error", "textAppear", "restart".
  `PlaySoundEffectNotOneShot(name)` (sliders) plays only if that source is idle. Mixers: public AudioMixer m_music, m_sfx,
  m_ambience; exposed float "Volume" (dB); SetVolumeFromNormalized(mixer, 0..1) via AnimationCurve m_volumeMapping.

## ButtonPunchAnimator (on every menu button)
`[RequireComponent(RectTransform)] public class ButtonPunchAnimator : MonoBehaviour, ISelectHandler, IDeselectHandler,
IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler, ISubmitHandler`; `public bool m_dontPlaySound`.
- Awake caches the RectTransform and its localScale as base scale.
- OnSelect: punch-in (DOPunchScale by -0.02 x base, 0.2 s, OutBack, SetUpdate(true) so it works at timeScale 0) and the
  select sound, but only when the `UI+Move` action's value magnitude is > 0.02 (i.e. selection moved by keys/stick, not by
  code or mouse).
- OnPointerEnter: punch-out (+0.02) only, no sound. OnPointerExit: tween back to base scale unless this is
  EventSystem.current.currentSelectedGameObject. OnDeselect: tween back (DOScale base, 0.2 s, OutSine, unscaled).
- OnPointerClick and OnSubmit: punch-in + PlayButtonPressedSound. So the click sound comes from this component, not from the
  Button's onClick; a cloned button keeps it. All tweens are unscaled, safe in the pause menu.

## GamepadMenuInput (one per screen; selection, highlight, Back)
Fields: `public bool alwaysForceDefault; public GameObject m_defaultButton; [SF] Button[] m_buttons; [SF] bool m_addAllButtons;
public GameObject m_lastSelected; [SF] Image m_highlight; [SF] bool m_showIfControllerConnected, m_showAlways,
m_isMultiHitMenu; public Button buttonToPressOnBack; public GamepadMenuInput togglePreviousMenu`.
- `public void OnEnable()`: disables togglePreviousMenu, then SetUpMenu (rebuilds m_buttons =
  GetComponentsInChildren<Button>(m_addAllButtons) — so by default only ACTIVE child buttons — and m_sliders; highlight
  enabled iff InputManager.m_isUsingGamePad || m_navigatingWithDirections). If m_showAlways or gamepad/directions: SetSelected;
  else just hides the highlight. Consequence: a button added under the screen root is picked up the next time the
  component is enabled (the mod can also call OnEnable() itself); it is then highlighted like the others.
- `public void SetSelected()`: EventSystem.current.SetSelectedGameObject(m_defaultButton) if m_lastSelected is null,
  inactive, or alwaysForceDefault; else m_lastSelected. Then refreshes the highlight.
- `public void GamepadConnected()`: SetSelected if nothing was selected yet (InputManager calls it on every enabled
  GamepadMenuInput the first time a stick/dpad moves).
- OnDisable: hides highlight; re-enables togglePreviousMenu and calls its OnEnable.
- Update: Back = the `Cancel` action WasPressedThisFrame and buttonToPressOnBack != null -> PlayButtonPressedSound +
  buttonToPressOnBack.onClick.Invoke(). (Main menu has none; every settings sub-screen points it at its BACK button; pause
  menu points it at RESUME.)
- LateUpdate (private GamepadMenu): when the selected object changed, the highlight Image is reparented under the selected
  button (or slider, sized to the slider's fill area +70/+30), placed at its position with the button's sizeDelta, and enabled
  only if gamepad/directions/m_showAlways. m_lastSelected tracks the selection. If nothing in m_buttons is selected while
  navigating, it selects m_defaultButton. So the "selection highlight" is ONE `highlight` Image per screen (sprite
  `buttonHighlight`) that jumps between buttons; no arrow/cursor follows it.
- TrackInputType: InputManager.m_navigatingWithDirections = `UI+Move` magnitude > 0.02 || `Submit` or `Cancel` pressed ||
  m_isUsingGamePad. On the frame it turns on (and not m_isMultiHitMenu) it re-selects m_lastSelected/m_defaultButton.
  Runtime: in Menu2 the EventSystem already had `Menu Button` (Play) selected with the mouse (MainMenu2 selects it on appear,
  highlight hidden). The first DownArrow only turned the highlight on around Play (selection stayed Play); the second moved it
  to Settings (Explicit nav). Screenshots menu2_keydown1/2.

## InputManager / EventSystem
- EventSystem per scene (Menu2:`EventSystem`, Main:`__Managers House/EventSystem`), firstSelectedGameObject null,
  module `UnityEngine.InputSystem.UI.InputSystemUIInputModule` on actions asset `Inputs`: move=`Player/UI+Move`,
  submit=`Player/Submit`, cancel=`Player/Cancel`, point=`Player/Mouse`, leftClick=`Player/Click` (all in the Player map).
- `public class InputManager : MonoBehaviour`: `public static InputManager instance` (first Awake wins); `public PlayerInput
  input`; `public bool m_isUsingGamePad, m_navigatingWithDirections`; `[HideInInspector] public string m_controllerType`;
  `public RebindSaveLoad m_rebindSaveLoad`; `public Vector2 m_mousePos`; `public bool m_useSoftwareCursor; public Texture2D
  m_cursor`; private InputActionReference m_UIMove, m_Move, m_UI. Actions used by name: Move, Switch, Use, Reset, Adjust,
  Practice, Pause, Right Joystick, Sprint, Crouch, Zoom, Angle, Path, ClubHold, UI+Move, Submit, Cancel, Mouse, Click.
- m_isUsingGamePad comes from an `InputSystem.onEvent` hook (StateEvent/DeltaStateEvent): Gamepad left stick > 0.2 or dpad >
  0.1 -> true (first time: GamepadConnected() on all enabled GamepadMenuInput), right stick > 0.2 -> true; any Keyboard
  event -> false; Mouse only with Input.GetMouseButton(0) (legacy) -> false; Touchscreen -> false. Moving the mouse alone does
  not switch back. Update: when the control scheme string changed -> UpdateControls (m_controllerType = gamepad name) and
  InputIconDictionary.RefreshIcons (re-runs LocaliseText with updateOnControlChange).
- `public bool PlayerPressedBack()`: `Pause` pressed this frame, or (gamepad && `Reset` pressed && timeScale == 0).
  `public void DisableInputTemporarily()`: PlayerInput disabled for one frame (so the closing Pause press does not reopen).
- OnEnable enables m_UIMove/m_Move/m_UI and after 0.75 s `SyncMoveBindings` rebuilds `UI+Move` bindings = Move's + UI's
  bindings (so WASD and arrows/dpad navigate menus).
- Cursor: `public static class MouseCursor { Lock(); Unlock(); IsMouseLocked(); }`. Lock stores the mouse position (zero on
  gamepad) and sets Locked/hidden. Unlock: None + visible, warps the cursor back into the central 80 % of the screen, optional
  software cursor; Confined when fullscreen. PauseMenu.Update and SettingsMenu.Update call Unlock every frame while open.
- Other: `PressAndHoldButton` (IPointerDown/Up/Enter/Exit; `[SF] UnityEvent m_onHold` invoked every Update while held; used
  by the credits fast-scroll). `MouseDetector` (m_isValid on pointer enter/down). `SelectableClub` / `SelectableJournalEntry`
  (world panels; pointer enter slides the item by m_clubLiftAmount and DOColor tints, click selects via SelectClub /
  SelectJournalEntry, "clubSelect" sound) — pointer-only list items, not UI.Selectable.

## Main menu (scene Menu2, canvas `Main Menu`)
Root components: Canvas, CanvasScaler, GraphicRaycaster, Animator (controller `Main Menu`; clips not visible in the dumps),
MainMenu, MainMenu2, GamepadMenuInput (m_defaultButton `Menu Button` = Play, m_highlight `Main Menu/highlight`,
m_showIfControllerConnected true, no back button). Settings button calls `PlayerPressedEscape.OpenMenu` on
`Menu2:Managers House` (its m_objectToEnable = Menu2 `SETTINGS MENU`).

Hierarchy (anchors in the 1920x1080 reference):
```
Main Menu
  MainFrame            anchor bottom-centre (0.5,0), pos (0,256), size 600x120, Image glass_0 (white), DVDBouncer (disabled until 10 bounces)
    FRAMEOVERLAY       stretch, sizeDelta (21.4,25.6) (rim overhangs ~11 px), Image frame9sliceHollow Sliced white
    PlayButton         stretch, sizeDelta (-10,-10)  (5 px inner padding)
      Menu Button      590x110 centred; Image button rgba(0.185,0.802,0.46) green; Button -> MainMenu.PlayGame
        LABEL "Play" 36 auto;  PlayButtonEXTRAS (Ending A/B ticks, active when bounties restored)
    Frame2             child of MainFrame, centre anchor, pos (0,-163.5), size 600x151.5, Image glass_0
      FRAMEOVERLAY     as above
      VLG              stretch (-10,-10); NO layout group despite the name: fixed positions
        Settings       top-left anchor, pos (147.8,-32.9), 295x75, rgba(0.821,0.6,0.221) orange -> PlayerPressedEscape.OpenMenu
          LABEL 26 auto (offset (0,1.2), height -27.2);  Notification (NewBallHelper red "!")
        PlayNine       pos (441.5,-32.9), 295x75, rgba(0.244,0.654,0.821) blue -> MainMenu.PlayNineButtonPressed
        Quit           bottom-centre anchor, pos (1.3,36.7), 592.6x75, rgba(0.698,0.314,0.317) red -> MainMenu.Quit
    collision          622.8x325 at (0,-90.7), disabled Image: the DVD collision box covering both frames
  ExtrasFrame          anchor bottom-left, pos (167.05,92), size 303.5x151.5, Image glass_0, DVDBouncer (disabled)
    FRAMEOVERLAY
    VLG                stretch (-10,-10), VerticalLayoutGroup (spacing 0, UpperLeft, control size off, force expand on)
      MakingOf         295x75, rgba(0.789,0.337,0.839) purple -> OpenLink.OpenURL(string) (Application.OpenURL)
      Discord          295x75, rgba(0.553,0.337,0.839) violet -> OpenLink.OpenURL
  Logo Collision       the NORMAL GOLF GAME logo (3 tinted Images menuLogo_0..2), DVDBouncer (m_delay 1)
  VersionText, BounceText (top-right bounce counter, BounceCount), highlight (Image buttonHighlight, disabled)
  Wishlist Button / DEBUG SAVE SKIP (inactive), INPUT MESSAGE, STREAMER MODE (first-run prompt, VLG of 2 buttons),
  DIFFICULTY (first Play: Easy/Normal/Hard HorizontalLayoutGroup + CONFIRM, DifficultySelect), DEMOSAVEUPGRADE,
  LOADING COVER, REGULAR GOLF (Play Nine first-time overlay)
```
A panel = `glass_0` Image (the dark green fill) with a stretched `FRAMEOVERLAY` child (frame9sliceHollow, white, overhanging
by ~10.7/12.8 px each side), then a content child inset 5 px (sizeDelta -10,-10) holding the buttons. Button order in the
hierarchy matters only for draw order. Main menu labels have NO localize component (texts are plain scene strings); STREAMER
MODE labels use LocaliseText, DIFFICULTY ones I2 Localize.

Explicit navigation (main menu): Play down->Settings, left->MakingOf, right->PlayNine; Settings up->Play, down->Quit,
left->MakingOf, right->PlayNine; PlayNine up->Play, down->Quit, left->Settings; Quit up->Settings, left->Discord,
right->PlayNine; MakingOf down->Discord, right->Settings; Discord up->MakingOf, right->Quit. A new panel needs its own
Explicit links set both ways (the existing buttons will not point at it unless the mod edits their Navigation).

### Open sequence
- `MainMenu.Start` (private): hides streamer/loading/difficulty, sets mode Story, version text, ending ticks from
  LoadRunSave, starts private coroutine `StartSequence`: INPUT MESSAGE card, then (first run only) STREAMER MODE choice,
  fades message out, starts menu music (`m_music` = AudioSource on Audio Manager, time 94.8 s), then `m_menu.StartSequence()`.
- `MainMenu2.Start`: MainFrame anchoredPosition (0,-220), ExtrasFrame y -220 (offscreen below), logo (`m_rect` = Logo
  Collision) localScale 0; sets grass material wind. `public void StartSequence()` -> private coroutine `Appear`: wait 0.5 s,
  logo DOScale(1, m_scaleTime=1 s), wait 1 s, MainFrame DOAnchorPosY(256, 0.5 s) whose OnComplete does
  m_mainGamepadMenu.m_lastSelected = null; SetSelected(); m_dvd.StartBounce() (the logo bouncer); wait 1.5 s, ExtrasFrame
  DOAnchorPosY(92, 0.5 s). Tweens are scaled (DOTween default) and default ease. A new panel copies this by starting
  off-screen and DOAnchorPosY to its rest position after the frames arrive (or Harmony-postfix MainMenu2.StartSequence).
- `MainMenu.PlayGame()` -> PlaySetUp: optional DEMOSAVEUPGRADE and DIFFICULTY screens (CanvasGroup DOFade 0.5 s; difficulty
  GamepadMenuInput SetSelected on fade-in), saves m_hasCheckedForFirstSave, LOADING COVER fades in over 0.75 s while the music
  fades, then SceneManager.LoadScene("Main"). `PlayNineButtonPressed()` shows REGULAR GOLF overlay first time, else
  `PlayNine()` (mode JustGolf + PlayGame). `Quit()` = Application.Quit. Public flags m_selectedDifficulty,
  m_chosenWhatToDoWithDemoSave; `public SettingsMenu m_settings`.
- In Menu2 the pause action also works: PlayerPressedEscape (on Managers House) opens SETTINGS MENU and sets timeScale 0.
  The main menu stays visible and interactable underneath (the settings BACKING blocks raycasts).

### The DVD bounce (not idle-based)
`public class DVDBouncer : MonoBehaviour`: public `Vector2 m_dir; Color[] m_colors; float m_delay; RectTransform
bounceEdges; RectTransform m_collisionRect; List<Image> m_images; BounceCount m_bounceCounter`; private m_speed = 50,
m_myrect (GetComponent<RectTransform> in Start), bool move.
- `public void StartBounce()` waits m_delay seconds (scaled) and sets move. FixedUpdate (so frozen at timeScale 0, i.e. while
  Settings is open): anchoredPosition += m_dir * fixedDeltaTime * 50 (reference px/s), then collision: the world corners of
  m_collisionRect in bounceEdges' local space against bounceEdges.rect (all use `Main Menu` = the whole canvas); on an edge
  it flips the axis, pushes the object back inside, counts a bounce; one axis -> "pop" + recolour, both axes (corner) -> 3
  bounces, "special" sound, and `SteamManager.Instance.UnlockAchievement(AchievementID.CORNER)`.
- Recolour: each Image in m_images gets a distinct random colour from m_colors (sampled without replacement, so m_colors
  must have at least as many entries as m_images). MainFrame's list = Play, Settings, PlayNine, Quit (10 muted colours);
  ExtrasFrame's = MakingOf, Discord; logo's = the 3 logo parts.
- `public class BounceCount : MonoBehaviour { public int bounceCounter; public TMP_Text m_text; public int start1BounceCount
  (10); public DVDBouncer start1 (MainFrame); public int start2BounceCount (100); public DVDBouncer start2 (ExtrasFrame);
  public void Bounce(); }` Bounce() increments, writes the counter (top-right), and at the thresholds enables + StartBounce()s
  start1/start2. Only the logo bounces at first (starts 1 s after the main frame lands); after 10 bounces (about 1-1.5 min in the test run) MainFrame
  joins, after 100 ExtrasFrame. Nothing resets it: no idle timer, input does not stop it, panels never return; it lasts until
  the scene reloads. Every bouncer's hits add to the same counter.
- For a new panel to join: give its root a DVDBouncer (AddComponent while inactive or before Start runs; set m_dir e.g.
  (1,-1), m_colors (>= its button count), bounceEdges = `Main Menu` RectTransform, m_collisionRect = a rect covering the panel
  incl. rim, m_images = its button Images, m_bounceCounter = the `BounceText` BounceCount, m_delay 0), keep it disabled, and
  enable + StartBounce() when bounceCounter reaches a threshold of our choosing (BounceCount knows only start1/start2:
  Harmony postfix on BounceCount.Bounce, or poll). It must live under `Main Menu` (any parent; DVDBouncer moves its own
  anchoredPosition). If it has m_bounceCounter set its corner hits also unlock the CORNER achievement (as the game's do).

## Settings (canvas `SETTINGS MENU`, one copy in Menu2 and one in Main)
`public class SettingsMenu : MonoBehaviour` on the canvas root. Serialized: m_pauseMenu (null in Menu2, `PAUSE MENU` in Main);
public Sliders m_musicSlider, m_sfxSlider, m_ambienceSlider, m_mouseSense, m_gamepadSense; TMP_Text value labels
(m_qualityText, m_fullscreenText, m_resolutionText, m_AAtext, m_noAutoResetStanceText, m_clubQuickSelect,
m_realisticPhysicsText, m_speedrunTimerText, m_disablePowerBarText, m_disableShotShapeText, m_invertYText,
m_enableMotionFrame, m_enableCrosshair, m_lowGrassDensity, m_smoothLook, m_alwaysRun, m_difficulty, m_difficultyInfo);
GameObjects m_settingsPanel (`SETTINGS FRAME`), m_controlsMenu, m_rebindMenu, m_languageMenu, m_creditsMenu,
m_difficultyMenu, m_visualMenu, m_gameplayMenui, m_soundMenu, m_deleteMenu, m_deleteMenuConfirm1, m_deleteMenuConfirm2;
`public GamepadMenuInput m_previousGamepadController`.
- Opening: Menu2 via PlayerPressedEscape.OpenMenu (Settings button or Pause action); Main via PauseMenu.OpenSettingsMenu
  (SetActive(true) on SETTINGS MENU, pause menu SetActive(false)). No tween, instant SetActive.
- Screens are siblings under the canvas, each a `frame9slice` rimmed frame with its own GamepadMenuInput (default + back =
  its `BACK (2)` button, colour rgba(0.157,0.224,0.357)) and own `highlight`: SETTINGS FRAME (GridLayoutGroup 2 columns,
  cell 440x90: Change Difficulty (in a spacer row), Sound, Video, Configure Controls, Gameplay, Language (inactive), Delete
  Saves, Credits), SOUND SETTINGS, VISUAL SETTINGS, GAME SETTINGS, CONTROL SETTINGS, BALL SELECT (BallSelectMenu),
  DeleteSaves, DELETE Confirm1/2, DIFFICULTY SETTINGS, LANGUAGE (SetLanguage; English/German/Chinese), REBIND (RebindMenu +
  23 RebindActionUI rows), OVERLAY WAIT FOR INPUT, CREDITS. Fullscreen `BACKING` (diagonals, rgba 0.038 a 0.894) blocks clicks.
- `Open*()` methods SetActive(true) the sub-screen and SetActive(false) SETTINGS FRAME; `BackToSettings()` deactivates all
  sub-screens and reactivates SETTINGS FRAME; `Close()`: with m_pauseMenu -> reactivate pause menu, deactivate self; without
  (Menu2) -> timeScale 1, deactivate, MouseCursor.Lock if walking; then re-enables m_previousGamepadController (which re-runs
  its OnEnable, rescanning buttons). Update: Pause action at timeScale 0 -> Close() from SETTINGS FRAME, else
  BackToSettings(); also Unlock cursor and refresh fullscreen/resolution labels every frame.
- Widgets (all plain Buttons, no Toggle/Dropdown/InputField/ScrollRect/TMP_InputField anywhere in the game's code or these
  dumps):
  - "Toggle" row: HorizontalLayoutGroup row (Image buttonBacking_0) = `Backing` label + `Button` whose LABEL shows the value;
    onClick -> SettingsMenu.ToggleX(), which flips the GameSettings bool, saves, and SetUIStates() rewrites the value labels
    (LocalizationManager.GetTranslation("On"/"Off"), some hard-coded "On"/"Off", "Low"/"High", "Enabled"/"Disabled").
  - Left/right carousel: VISUAL SETTINGS/VLG/Resolution = Backing label + `Button LEFT` "<" + `Button` (value label, no Button
    component) + `Button RIGHT` ">"; both arrows call SetResolution(bool up) with a persistent bool argument; arrows have
    LayoutElement + ButtonPunchAnimator. Difficulty uses one button cycling ToggleDifficulty().
  - Sliders: UnityEngine.UI.Slider 0..1 (Background, Fill Area/Fill, Handle Slide Area/Handle) with onValueChanged ->
    SetSFXVolume etc.; GamepadMenuInput highlights sliders too.
  - Confirmation popup: `DELETE Confirm1`/`2` = CanvasGroup root + Content (CanvasGroup, HIGHLIGHT, Text (TMP) with
    LocaliseText, VLG VerticalLayoutGroup spacing 7.18 with red `Delete` rgba(0.736,0.183,0.149) and white `Cancel`),
    GamepadMenuInput default = Cancel. MainMenu's DIFFICULTY/STREAMER MODE/DEMOSAVEUPGRADE overlays are the same idea with
    CanvasGroup fades.
  - Scrolling text: CREDITS uses RectMask2D + ScrollCredits (auto-scrolls a TMP text by scrollSpeed*unscaledDeltaTime,
    loops between markers; PressAndHoldButton -> SpeedUp). Not a ScrollRect. A lobby list would need a new ScrollRect
    (UnityEngine.UI) or a paged list built from cloned buttons.
  - No text input widget exists (the in-world code board InputBoard is driven by ball hits). A name/code entry would need a
    new TMP_InputField.
  - Tabs: none; sub-screens are swapped with SetActive.

## Pause menu (scene Main, canvas `PAUSE MENU`)
- Components on root: Canvas (998), PauseMenu, GamepadMenuInput (m_defaultButton `RESUME`, buttonToPressOnBack RESUME,
  m_buttons RESUME/SETTINGS/Quit/QuitRound/discord, m_showIfControllerConnected false).
- Hierarchy: `BACKING` (diagonals); `FRAME` 480x360 centred, Image frame9slice; `FRAME/VLG` stretch (-40,-40),
  VerticalLayoutGroup spacing 7.18, UpperLeft, childForceExpandWidth; buttons 440x100: `RESUME` rgba(0.349,0.443,0.631) ->
  PauseMenu.Exit, `SETTINGS` rgba(0.275,0.365,0.549) -> PauseMenu.OpenSettingsMenu (+ NewBallHelper "!"), `Quit` ("Save and
  Quit") rgba(0.216,0.306,0.49) -> PauseMenu.QuitToMenu; labels 36 auto. Navigation Vertical (wrap on SETTINGS). Outside the
  frame: `CONTROLS` (right side key list, LocaliseText controlsOnly rows + LOCKED overlays), `QuitRound` (inactive; Just Play
  Nine in LMUGC only, -> RestartFromHole1), `discord` purple bottom-left, `highlight`. Adding a button = clone RESUME into
  FRAME/VLG and grow FRAME's height by 107.18 per button (the VLG does not resize the frame).
- `public class PauseMenu : MonoBehaviour`: [SF] m_settingsMenu, m_golfLocked, m_crouchLocked, m_quitRoundButton. OnEnable
  updates the LOCKED overlays and QuitRound visibility. Update: Unlock cursor; at timeScale 0, InputManager.PlayerPressedBack()
  -> Exit(). `public void Exit()`: DisableInputTemporarily, SetActive(false), timeScale 1, MouseCursor.Lock if walking.
  `public void QuitToMenu()`: timeScale 1, SaveManager.SaveRun(), LoadScene("Menu2"). `public void OpenSettingsMenu()`,
  `public void RestartFromHole1()`.
- Opening: `public class PlayerPressedEscape : MonoBehaviour` ([SF] GameObject m_objectToEnable, [SF] GamepadMenuInput
  m_gamepadMenuInput, private float m_timer = 1). Update: m_timer refills to 1 while MoveAndHitController.m_moveLocked, else
  counts down (0 when there is no player). On `Pause` pressed with timeScale > 0: no player or no HitSequeneceManager ->
  OpenMenu; else closes HitSequeneceManager.m_multiHitMenu or m_lmugcResetMenu if open; else OpenMenu when m_timer <= 0 (so
  no pause for 1 s after dialogue/cutscene locks). `public void OpenMenu()`: only if timeScale > 0; disables
  m_gamepadMenuInput and hands it to SettingsMenu.m_previousGamepadController when the target has a SettingsMenu; SetActive
  the target; Time.timeScale = 0. Nothing tweens; no sound on open (buttons click on press). Music is not changed by pause.
- Runtime: Escape (key, focus needed) opened it: timeScale 0, cursor None/visible, EventSystem selection null (mouse mode);
  pause Settings -> SETTINGS MENU with m_pauseMenu = PAUSE MENU; its Back returned to the pause menu (timeScale still 0);
  Save and Quit loaded Menu2 with `Menu Button` selected again.
- Other in-game popups: HitSequeneceManager.m_multiHitMenu / m_lmugcResetMenu with MultihitMenu (OnEnable punch-scales the
  menu and its buttons one by one with WaitForSecondsRealtime, plays the pressed sound; closes itself in Walk mode).

## Localization
- I2 Localization (namespace I2.Loc) plus the game's wrappers. Two label components exist:
  - `I2.Loc.Localize` (mTerm, LocalizeOnAwake true): Awake and OnEnable call OnLocalize, which (unless forced or it has a
    callback) returns early when LastLocalizedLanguage == CurrentLanguage; otherwise writes the term's translation into the
    target (LocalizeTarget_TextMeshPro_UGUI). Re-runs on LocalizationManager.LocalizeAll (language change).
  - `LocaliseText` (game): `public string stringID; public TMP_Text text; public bool updateOnControlChange, controlsOnly;
    public ForceShowControls forceShowControls`; Start() sets text = Loc.GetString(stringID) (or Loc.AddControls when
    controlsOnly) and, with updateOnControlChange, registers in InputIconDictionary.m_textToUpdate to be redone when the input
    device changes. Not re-run on enable.
  - `Utilities.Loc` (static): `GetString(key)` = translation, then `[Action]` placeholders replaced by binding names or TMP
    sprite icons (`<sprite="sheet_xbox" name=...>`, cyan `<color=#B4FFFD>` + `<wave>` on keyboard), then `{Term}` replaced
    by "Terms/..." translations. `AddControls(string, bool controlsOnly, ForceShowControls)`, `StyleTerms(string)`.
  - Scripts also set texts directly (SettingsMenu.SetUIStates, ClubsPanelNavigation title via Loc.GetString).
- `SaveManager.LoadGameSettings` always ends with LocalizationManager.CurrentLanguage = "English" (the saved m_language is
  never applied) and the LANGUAGE button is inactive, so the game is effectively English-only and no LocalizeAll happens
  after startup.
- Cloning: the main menu buttons (Play, Settings, PlayNine, Quit, MakingOf, Discord) and pause buttons carry no
  Localize/LocaliseText, so a cloned label keeps whatever the mod writes. When cloning from a localized source (settings
  sub-screens, difficulty, streamer, delete popups), destroy Localize/LocaliseText on the clone before it is activated
  (Instantiate an inactive template, or DestroyImmediate on the copy) or its Awake/OnEnable/Start rewrites the text.

## Steam
- SteamManager (Steamworks.NET template, DisallowMultipleComponent): lazy `Instance` creates a GameObject "SteamManager";
  Awake: singleton, throws if initialized twice per process, DontDestroyOnLoad, Packsize/DllCheck tests,
  SteamAPI.RestartAppIfNecessary(3510740) (quits if it relaunched), SteamAPI.Init into m_bInitialized;
  `public static bool Initialized`. OnEnable installs the warning hook. Update calls SteamAPI.RunCallbacks() every frame
  while initialized (so the mod's Steam callbacks are pumped; no need to pump again). OnDestroy: SteamAPI.Shutdown.
- Only SteamUserStats is used (UnlockAchievement / GetAchievementStatus / ResetAchievements). No SteamFriends, overlay
  calls, rich presence, matchmaking or SteamUtils anywhere in Assembly-CSharp. External links use Application.OpenURL.

## Surprises / gotchas
- Main menu Settings/PlayNine/Quit live in a frame named `VLG` that has no layout group: positions are fixed; ExtrasFrame's
  VLG is a real VerticalLayoutGroup. Frame2 is a child of MainFrame, so one DVDBouncer moves both.
- GamepadMenuInput rebuilds its button list from ACTIVE children whenever it is enabled; and it is re-enabled every time the
  Menu2 settings close (SettingsMenu.Close -> m_previousGamepadController.enabled = true).
- In Menu2, opening Settings sets timeScale 0 (DVD stops; scaled DOTween tweens freeze); Close restores 1.
- The menu frames only DVD after the logo has bounced 10 times (~1 min) and never stop; corner hits unlock CORNER.
- The mod's current IMGUI "Multiplayer" button sits on top of the Join the Discord button (bottom-left) in Menu2, and the
  pause screen's "Multiplayer: F7 - host or join" overlaps the Discord feedback button (see screenshots).
- In Main the harness `buttons` still lists world-panel pointer targets (club list, tweaks) while paused; not checked
  whether the pause BACKING actually blocks clicks on them.
