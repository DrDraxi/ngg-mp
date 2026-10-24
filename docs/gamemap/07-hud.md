# NGG map 7: the walking HUD (objective and money panels, bank, prompts)
(From a live `uitree` of the HUD canvas in an NGG-UI test copy (Main, walking, story save) and the decompiled game, 2026-10-21.
The dump is `menus-ui-20261021/uitree-hud-canvas.txt` and the reference crops are `hud-reference.png` /
`hud-reference-full-1280x720.png` on the `screenshots` branch. Values in the canvas' 1920x1080 reference units.)

## The canvas
- Path: `Main:___PLAYER/First Person Character/Eye/Camera/Canvas` (layer 0): a child of the first-person camera.
- Canvas: renderMode **ScreenSpaceCamera**, sortingOrder 10. CanvasScaler ScaleWithScreenSize, reference (1920,1080),
  **matchWidthOrHeight 0.5** (the menus use 1). GraphicRaycaster. The dump does not show worldCamera / planeDistance.
- Top-level children: MOTION SICKNESS, COMPASS, MAP (inactive), **CHALLENGEREMINDER**, **YOU HAVE REMINDER**, CUTSCENE
  (two black bars), DIALOGUE, ERROR / ERRORLMUGC / ERROR SCRIPT(2) / NO CARD (inactive), PROMPTS (bottom-centre "[Switch] to
  Golf" boxes), BANK_FRAME, ASSISTS (inactive), DOT (crosshair), EYES (eyelids), RANGEFINDER.
- The whole canvas exists only while walking: entering golf mode deactivates `m_FPSController` (MoveAndHitController's
  private golf switch), which takes this canvas with it; going back to walking reactivates it. Golf mode has its own 2D panel
  UI (PanelManager). A panel parented here therefore disappears in golf mode; a player list that must stay visible needs its
  own overlay canvas (and can copy these values).

## Objective panel: `CHALLENGEREMINDER`
- RectTransform: anchor (0,1)/(0,1), pivot (0,1), anchoredPosition (20,-20), sizeDelta driven by the fitter (seen 702.67 x 54.91).
- Image: sprite `frame9slice`, type **Sliced**, color rgba(0,0,0,**0.196**) (a faint dark rounded box; the rim of the
  sprite barely shows at that alpha), Default UI Material. pixelsPerUnitMultiplier is not in the dump (assume 1).
- ContentSizeFitter: horizontal PreferredSize, vertical MinSize.
- VerticalLayoutGroup: padding left 14, right 14, top 4, bottom 8; spacing 0; UpperLeft; control child size off; force
  expand off.
- CanvasGroup: alpha 1 (when shown), interactable false, blocksRaycasts false.
- ChallengeReminder component (see below).
- Child `Text (TMP)`: anchor/pivot top (0,1)/(0.5,1), anchoredPosition (351.3,-4), size by its own ContentSizeFitter
  (Preferred x Preferred, seen 674.67 x 42.91). TextMeshProUGUI: font `Quantico-Regular SDF` (material `Quantico-Regular
  Atlas Material`), fontSize **30**, Normal, alignment **TopLeft**, autosize off, color white, characterSpacing 0. Margins are
  not in the dump. The text is rich text with per-letter colours for LMUGC (`<color=#FF4089>LM</color><color=#FFDD79>U`...).
  A Febucci `TextAnimator_TMP` sits on it (timeScale Unscaled, referenceFontSize 30, default appearance "size", disappearance
  "fade"); the TMP's renderMode shows DontRender because the animator draws the mesh. A plain clone without the animator
  renders through TMP normally.
- Child `Arrow` (LayoutElement ignoreLayout, anchored right-middle (1,0.5), +42.4 x): a small 3D quad (`1x1SquarePlane`,
  material `TeeArrowRenderTexMat`) showing the orange tee arrow; not UI art.

## Money panel: `YOU HAVE REMINDER`
- A sibling of CHALLENGEREMINDER (not inside it, not under its CanvasGroup).
- RectTransform: anchor/pivot (0,1), anchoredPosition (20,**-84.87**) (fixed: 20 + 54.91 + ~10 below the objective box),
  size by fitter (seen 269.6 x 54.91).
- Same Image (frame9slice, Sliced, black at alpha 0.196), same ContentSizeFitter (Preferred / Min) and same
  VerticalLayoutGroup (14/14/4/8, spacing 0, UpperLeft). No CanvasGroup.
- Child `Text (TMP)`: same settings as the objective text (Quantico-Regular SDF, 30, TopLeft, white, own ContentSizeFitter,
  TextAnimator_TMP), anchoredPosition (134.8,-4), seen 241.6 x 42.91.
- So "the HUD panel style" = Image frame9slice Sliced rgba(0,0,0,0.196) + VerticalLayoutGroup padding (14,14,4,8) +
  ContentSizeFitter (Preferred width, Min height) + a white Quantico 30 TopLeft label; panels stacked 10 px apart at x 20.

## Which code fills them
- `ChallengeReminder` (MonoBehaviour on CHALLENGEREMINDER). Serialized private fields: m_text (objective TMP), m_rect
  (its RectTransform), m_canvasGroup, m_balanceReminderText (money TMP), m_balanceReminder (the YOU HAVE REMINDER object);
  private lastString.
  - Start: canvas group alpha 0.
  - `public void SetString(string s)`: ignores a repeat of the last string; empty -> DOFade(0, 0.5 s); otherwise activates
    the object, sets the text and DOFade(1, 0.5 s); forces a layout rebuild now and again one frame later. Then decides the
    money panel: shown when the current challenge's short string contains "Buy", or when all bounties are restored; hidden
    with no challenge.
  - `public void ShowBalanceReminder(bool show)`: SetActive on the money panel (no fade); showing also calls
    UpdateBalanceReminder.
  - `public void UpdateBalanceReminder()`: text = Loc "You Have" + " $" + bank balance (`ProgressManager.instance.m_bank.
    m_balance`, "N0" invariant), or after the bounties are restored Loc "Bounties Count" + " N / 105" (and the COMPLETE
    achievement check). Called by FPSInteractable after purchases; the mod already refreshes it when the bank changes.
- Callers of SetString: `ChallengeSystem` (public field `m_challengeReminder`) every Update outside LMUGC: "Golf" before
  golf is unlocked, "Ball in water" / "Go to the ball" for multi-shot challenges, the go-to-tee text for the set's required
  tee, otherwise its private ShowShortStringChallenge with Loc "Challenges/<m_shortString>" (empty once complete); empty on
  start and when a set ends. `LMUGC` sets "Ball in water LMUGC" / "Go to the ball" itself (ChallengeSystem.Update returns
  early while `m_run.isInLMUGC`).

## When it shows / hides
- Objective: fades through its CanvasGroup (0.5 s DOTween) whenever the string becomes empty / non-empty. Money: SetActive.
- Golf / swing mode: the whole canvas is gone (FPS character inactive), see above.
- Pause menu: nothing hides it; the pause canvas (overlay, sortingOrder 998) draws its striped BACKING over it, so it is
  dimmed behind the pause frame.
- Dialogue / cutscenes: DialogueManager does not touch the reminders; it scales in the CUTSCENE bars (`m_cineBars`,
  DOScaleY) and slides its dialogue box, so the panels stay visible (under the top bar's reach at y 0..200 when bars show).
- LMUGC course: same panels, strings from LMUGC; the ASSISTS panel (right side) slides in for assists.
- No photo mode exists (Screenshotter is an editor/debug key that hides only its own debug canvas).

## Other HUD panel styles (for a player list)
- **Bank panel** `BANK_FRAME` (right side): anchor/pivot (1,1), anchoredPosition (400,-40) = parked off-screen right; `Bank`
  slides it to its on-screen x with DOAnchorPosX (0.2/0.5 s, InOutBack) when the balance changes and back out ~1 s later.
  Children: FRAME (Image frame9slice Sliced **white** = the gold-rimmed light frame), TEXT_HEADING "Bank" (Quantico 36,
  autosize, Left), Backing (Image `rounded9slice` Sliced green rgba(0.388,0.804,0.556)) with TEXT_BALANCE (font
  `DS-DIGIB SDF`, 36, Center). Size 331.3 x 200.
- **Assists panel** `ASSISTS` (inactive except in LMUGC shop moments): same white frame9slice FRAME, heading 36, then rows
  60 high: Image rounded9slice in a colour (green 0.161,0.726,0.283; blue 0.161,0.596,0.725; pink 0.839,0.29,0.451) with a
  right-aligned label and a number (font `Quantico-NO DROP`, 36), rows 70 apart, inset 25 each side (sizeDelta x -50). This
  is the game's closest thing to a list panel with rows.
- **Prompts** `PROMPTS/GOLF|USE|COST`: Image frame9slice Sliced rgba(0.113,0.142,0.129,0.467) (dark green, more opaque),
  450 x 76.3, bold Quantico 36 centred, characterSpacing -5; stacked by a VerticalLayoutGroup (spacing 10, LowerCenter) at the
  bottom centre.
- **Pause screen controls list** (`PAUSE MENU/CONTROLS`, on the pause canvas): no backing at all; a VerticalLayoutGroup of
  sections (MiddleRight / LowerRight), each a heading (Quantico 36, auto) and rows that are HorizontalLayoutGroups (900 x 50,
  force expand) of a right-aligned label (Quantico 30, TopRight) and a value (Quantico 30, Top, value text coloured
  `#B4FFFD`). Plain text over the dimmed backdrop.

## For a native player list (notes, not decisions)
- Copying the HUD look: a clone of YOU HAVE REMINDER (Image + fitter + layout + one label) per line, or one box with a
  VerticalLayoutGroup of labels; mirror it to the top right with anchor/pivot (1,1) and anchoredPosition (-20,-20).
- The top right is used by BANK_FRAME / ASSISTS when they slide in (from x 400 off-screen to their on-screen x, y -40 and
  -244): a list there should sit below them or move aside while they are shown.
- Strip TextAnimator_TMP (and any LocaliseText) from cloned labels, or keep it for the game's text animation; it re-renders
  the text with its own mesh.
