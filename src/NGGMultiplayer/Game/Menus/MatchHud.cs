using System;
using System.Collections.Generic;
using System.Globalization;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Game.Presence;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NGGMultiplayer.Game.Menus
{
    /// <summary>
    /// The competitive 9-hole round's own screens, built from the game's pieces (docs/gamemap/08-justgolf.md):
    /// <list type="bullet">
    /// <item>the turn banner, a copy of the golf view's orange "Walk to the ball to continue." banner (PANEL_PIP's
    /// Multihit Retry), top centre in both views: whose turn it is, "Your shot", who the group waits for;</item>
    /// <item>the group scorecard in the style of the round's own HUD card (PANEL_LMUGC: green heading bar, green HOLE
    /// cells, yellow PAR, score backings in the game's par / under / over colours, a green TOTAL), one row per player
    /// in leaderboard order; shown while the scorecard key is held, between holes, and as the final results.</item>
    /// </list>
    /// The leaderboard itself is the HUD player list (<see cref="PlayerListHud"/>), which shows the standings in a match.
    /// Both live on an overlay canvas under the pause screen.
    /// </summary>
    internal sealed class MatchHud
    {
        private const string Log = "[Menus] ";
        private const int SortingOrder = 22;
        private const float FadePerSecond = 7f;
        private const double MineBannerSeconds = 5.0;
        private const double ResultsSeconds = 20.0;

        // Scorecard geometry, in the HUD's 1920 x 1080 reference pixels.
        private const float Pad = 24f;
        private const float NameWidth = 300f;
        private const float Cell = 72f;
        private const float Gap = 6f;
        private const float TotalWidth = 100f;
        private const float ToParWidth = 90f;
        private const float HeadingTop = 16f;
        private const float HeadingHeight = 52f;
        private const float HoleTop = 80f;
        private const float HoleHeight = 44f;
        private const float ParTop = 130f;
        private const float ParHeight = 40f;
        private const float RowsTop = 180f;
        private const float RowHeight = 54f;
        private const float RowStep = 60f;
        private const float FooterHeight = 50f;

        private static readonly Color Green = new Color(0.137f, 0.725f, 0.278f, 1f);
        private static readonly Color HeaderGreen = new Color(0.133f, 0.718f, 0.267f, 1f);
        private static readonly Color Yellow = new Color(0.969f, 0.729f, 0.008f, 1f);
        private static readonly Color Orange = new Color(0.953f, 0.518f, 0f, 1f);
        private static readonly Color Teal = new Color(0.706f, 1f, 0.992f, 1f);
        private static readonly Color Backdrop = new Color(0.04f, 0.06f, 0.05f, 0.86f);

        private GameObject _root;
        private Canvas _hud;

        private RectTransform _banner;
        private CanvasGroup _bannerGroup;
        private TextMeshProUGUI _bannerText;
        private Image _bannerImage;
        private Color _bannerColour = Orange;

        private RectTransform _card;
        private CanvasGroup _cardGroup;
        private TextMeshProUGUI _heading;
        private TextMeshProUGUI _footer;
        private Image _footerBacking;
        private readonly TextMeshProUGUI[] _pars = new TextMeshProUGUI[MatchState.Holes];
        private readonly Image[] _holeCells = new Image[MatchState.Holes];
        private readonly List<Row> _rows = new List<Row>();

        private Sprite _rounded;
        private Sprite _frame;
        private Sprite _hollow;
        private TMP_FontAsset _font;
        private TMP_FontAsset _headingFont;
        private Material _fontMaterial;
        private Color _parColour = new Color(0.137f, 0.725f, 0.278f, 1f);
        private Color _underColour = new Color(0.86f, 0.25f, 0.25f, 1f);
        private Color _overColour = new Color(0.16f, 0.5f, 0.85f, 1f);
        private Color _noScoreColour = new Color(0.585f, 0.585f, 0.585f, 1f);

        private uint _shownMatch;
        private uint _shownSequence;
        private bool _shownResults;
        private PeerId _bannerTurn = PeerId.None;
        private double _mineSince = double.NegativeInfinity;
        private uint _finishedMatch;
        private double _finishedAt = double.NegativeInfinity;

        public bool Built => _root != null;

        private sealed class Row
        {
            public RectTransform Root;
            public Image NameBacking;
            public TextMeshProUGUI Name;
            public Image[] Cells;
            public TextMeshProUGUI[] Scores;
            public Image TotalBacking;
            public TextMeshProUGUI Total;
            public TextMeshProUGUI ToPar;
            public CanvasGroup Group;
        }

        // ---- Building ---------------------------------------------------------------------------------------------

        /// <summary>Builds both screens from the scene's HUD and the game's sprites and fonts. False while not there.</summary>
        public bool Build()
        {
            if (_root != null)
                return true;
            if (!HudPanel.FindTemplate(out var template, out var hud))
                return false;
            try
            {
                var label = template.GetComponentInChildren<TextMeshProUGUI>(true);
                _font = label.font;
                _fontMaterial = label.fontSharedMaterial;
                FindArt();
                _hud = hud;

                _root = new GameObject("NGGMP MATCH");
                _root.SetActive(false);
                _root.layer = template.layer;
                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrder;
                var scaler = _root.AddComponent<CanvasScaler>();
                var hudScaler = hud.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = hudScaler != null ? hudScaler.referenceResolution : new Vector2(1920, 1080);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = hudScaler != null ? hudScaler.matchWidthOrHeight : 0.5f;

                BuildBanner();
                BuildCard();
                _root.SetActive(true);
                NetLog.Info($"{Log}Match banner and group scorecard added in the game's style.");
                return true;
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Building the match screens failed: {e}");
                Destroy();
                return false;
            }
        }

        /// <summary>The game's 9-slice sprites, its heading font and the round card's colours.</summary>
        private void FindArt()
        {
            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sprite == null)
                    continue;
                switch (sprite.name)
                {
                    case "rounded9slice": _rounded = _rounded ?? sprite; break;
                    case "frame9slice": _frame = _frame ?? sprite; break;
                    case "frame9sliceHollow": _hollow = _hollow ?? sprite; break;
                }
            }
            foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (font != null && font.name.IndexOf("NO DROP", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _headingFont = font;
                    break;
                }
            }
            var lmugc = PanelManager.instance != null ? PanelManager.instance.m_LMUGCPanel : null;
            if (lmugc != null)
            {
                ReadColour(lmugc, "m_parColor", ref _parColour);
                ReadColour(lmugc, "m_underColor", ref _underColour);
                ReadColour(lmugc, "m_overColor", ref _overColour);
                ReadColour(lmugc, "m_noScoreColor", ref _noScoreColour);
            }
            if (_rounded == null || _frame == null)
                NetLog.Warn($"{Log}The game's 9-slice sprites were not all found; the match screens use plain boxes for them.");
        }

        private static void ReadColour(object target, string field, ref Color colour)
        {
            if (MenuKit.GetField(target, field) is Color value && value.a > 0f)
                colour = value;
        }

        private void BuildBanner()
        {
            // The golf view's orange banner, with its striped material, when it can be found.
            Image template = null;
            TMP_Text templateLabel = null;
            var pip = GameObject.Find("PANEL_PIP");
            var retry = pip != null ? MenuKit.FindDeep(pip.transform, "Multihit Retry") : null;
            if (retry != null)
            {
                template = retry.GetComponent<Image>();
                templateLabel = retry.GetComponentInChildren<TMP_Text>(true);
            }
            if (template != null)
            {
                _bannerImage = MenuKit.CloneImage(template, _root.transform, "BANNER");
                _bannerColour = template.color;
            }
            else
            {
                _bannerImage = NewImage(_root.transform, "BANNER", _rounded, Orange);
            }
            _banner = _bannerImage.rectTransform;
            _banner.anchorMin = _banner.anchorMax = _banner.pivot = new Vector2(0.5f, 1f);
            _banner.anchoredPosition = new Vector2(0f, -26f);
            _banner.sizeDelta = new Vector2(720f, 68f);
            _banner.localScale = Vector3.one;
            _bannerGroup = _bannerImage.gameObject.AddComponent<CanvasGroup>();
            _bannerGroup.alpha = 0f;
            _bannerGroup.interactable = false;
            _bannerGroup.blocksRaycasts = false;
            _bannerText = templateLabel != null
                ? MenuKit.CloneLabel(templateLabel, _banner, "TEXT")
                : NewText(_banner, "TEXT", _font, 30f, Color.white, TextAlignmentOptions.Center);
            Stretch(_bannerText.rectTransform, new Vector2(24f, 6f));
            _bannerText.renderMode = TextRenderFlags.Render;
            _bannerText.enableAutoSizing = true;
            _bannerText.fontSizeMin = 18f;
            _bannerText.fontSizeMax = 32f;
            _bannerText.alignment = TextAlignmentOptions.Center;
            _bannerText.color = Color.white;
            _bannerText.richText = true;
            _bannerText.text = string.Empty;
        }

        private void BuildCard()
        {
            float width = CardWidth;
            var backing = NewImage(_root.transform, "SCORECARD", _frame, Backdrop);
            _card = backing.rectTransform;
            _card.anchorMin = _card.anchorMax = _card.pivot = new Vector2(0.5f, 0.5f);
            _card.anchoredPosition = new Vector2(0f, 30f);
            _card.sizeDelta = new Vector2(width, RowsTop + FooterHeight + Pad);
            _cardGroup = backing.gameObject.AddComponent<CanvasGroup>();
            _cardGroup.alpha = 0f;
            _cardGroup.interactable = false;
            _cardGroup.blocksRaycasts = false;

            var bar = NewImage(_card, "HEADER", _rounded, HeaderGreen);
            Place(bar.rectTransform, Pad, HeadingTop, width - 2f * Pad, HeadingHeight);
            _heading = NewText(bar.rectTransform, "HEADING", _headingFont ?? _font, 30f, Color.black, TextAlignmentOptions.Center);
            if (_headingFont != null)
                _heading.fontSharedMaterial = _headingFont.material;
            _heading.fontStyle = FontStyles.UpperCase;
            Stretch(_heading.rectTransform, Vector2.zero);

            var holeLabel = NewText(_card, "HOLE", _font, 22f, Green, TextAlignmentOptions.MidlineLeft);
            Place(holeLabel.rectTransform, Pad + 12f, HoleTop, NameWidth - 12f, HoleHeight);
            holeLabel.text = "HOLE";
            var parLabel = NewText(_card, "PAR", _font, 22f, Yellow, TextAlignmentOptions.MidlineLeft);
            Place(parLabel.rectTransform, Pad + 12f, ParTop, NameWidth - 12f, ParHeight);
            parLabel.text = "PAR";
            for (int h = 0; h < MatchState.Holes; h++)
            {
                float x = HoleX(h);
                var cell = NewImage(_card, "HOLE " + (h + 1), _rounded, new Color(Green.r, Green.g, Green.b, 0.263f));
                Place(cell.rectTransform, x, HoleTop, Cell, HoleHeight);
                var number = NewText(cell.rectTransform, "TXT", _font, 26f, Green, TextAlignmentOptions.Center);
                Stretch(number.rectTransform, Vector2.zero);
                number.text = (h + 1).ToString(CultureInfo.InvariantCulture);
                _holeCells[h] = cell;
                var par = NewText(_card, "PAR " + (h + 1), _font, 22f, Yellow, TextAlignmentOptions.Center);
                Place(par.rectTransform, x, ParTop, Cell, ParHeight);
                _pars[h] = par;
            }
            var totalLabel = NewText(_card, "TOTAL", _font, 22f, Green, TextAlignmentOptions.Center);
            Place(totalLabel.rectTransform, TotalX, HoleTop, TotalWidth, HoleHeight);
            totalLabel.text = "TOTAL";
            var toParLabel = NewText(_card, "TOPAR", _font, 22f, Green, TextAlignmentOptions.Center);
            Place(toParLabel.rectTransform, ToParX, HoleTop, ToParWidth, HoleHeight);
            toParLabel.text = "+/-";

            _footerBacking = NewImage(_card, "FOOTER", _rounded, new Color(Yellow.r, Yellow.g, Yellow.b, 0.16f));
            _footer = NewText(_footerBacking.rectTransform, "TXT", _font, 30f, Yellow, TextAlignmentOptions.Center);
            Stretch(_footer.rectTransform, new Vector2(16f, 0f));

            if (_hollow != null)
            {
                var frame = NewImage(_card, "FRAME", _hollow, Color.white);
                Stretch(frame.rectTransform, Vector2.zero);
            }
        }

        private Row NewRow(int index)
        {
            var go = new GameObject("ROW " + (index + 1), typeof(RectTransform));
            var root = go.GetComponent<RectTransform>();
            root.SetParent(_card, false);
            Place(root, 0f, RowsTop + index * RowStep, CardWidth, RowHeight);
            var row = new Row { Root = root, Group = go.AddComponent<CanvasGroup>() };
            row.Group.interactable = false;
            row.Group.blocksRaycasts = false;
            row.NameBacking = NewImage(root, "NAME", _rounded, new Color(1f, 1f, 1f, 0.08f));
            Place(row.NameBacking.rectTransform, Pad, 0f, NameWidth, RowHeight);
            row.Name = NewText(row.NameBacking.rectTransform, "TXT", _font, 26f, Color.white, TextAlignmentOptions.MidlineLeft);
            Stretch(row.Name.rectTransform, new Vector2(12f, 0f));
            row.Name.richText = true;
            row.Cells = new Image[MatchState.Holes];
            row.Scores = new TextMeshProUGUI[MatchState.Holes];
            for (int h = 0; h < MatchState.Holes; h++)
            {
                var cell = NewImage(root, "SCORE " + (h + 1), _rounded, _noScoreColour);
                Place(cell.rectTransform, HoleX(h), 0f, Cell, RowHeight);
                var text = NewText(cell.rectTransform, "TXT", _font, 26f, Color.white, TextAlignmentOptions.Center);
                Stretch(text.rectTransform, Vector2.zero);
                text.richText = true;
                row.Cells[h] = cell;
                row.Scores[h] = text;
            }
            row.TotalBacking = NewImage(root, "TOTAL", _rounded, Green);
            Place(row.TotalBacking.rectTransform, TotalX, 0f, TotalWidth, RowHeight);
            row.Total = NewText(row.TotalBacking.rectTransform, "TXT", _font, 28f, Color.white, TextAlignmentOptions.Center);
            Stretch(row.Total.rectTransform, Vector2.zero);
            row.ToPar = NewText(root, "TOPAR", _font, 28f, Color.white, TextAlignmentOptions.Center);
            Place(row.ToPar.rectTransform, ToParX, 0f, ToParWidth, RowHeight);
            row.ToPar.richText = true;
            return row;
        }

        private static float CardWidth => Pad + NameWidth + Gap + MatchState.Holes * (Cell + Gap) + TotalWidth + Gap + ToParWidth + Pad;

        private static float HoleX(int h) => Pad + NameWidth + Gap + h * (Cell + Gap);

        private static float TotalX => HoleX(MatchState.Holes);

        private static float ToParX => TotalX + TotalWidth + Gap;

        private Image NewImage(Transform parent, string name, Sprite sprite, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.layer = _root != null ? _root.layer : go.layer;
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        private TextMeshProUGUI NewText(Transform parent, string name, TMP_FontAsset font, float size, Color colour,
            TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.layer = _root != null ? _root.layer : go.layer;
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            if (font != null)
                text.font = font;
            if (font == _font && _fontMaterial != null)
                text.fontSharedMaterial = _fontMaterial;
            text.fontSize = size;
            text.color = colour;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.text = string.Empty;
            return text;
        }

        /// <summary>Top-left placement inside the parent, y downwards, in reference pixels.</summary>
        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static void Stretch(RectTransform rect, Vector2 inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = inset;
            rect.offsetMax = -inset;
        }

        // ---- Every frame ------------------------------------------------------------------------------------------

        /// <summary>
        /// Every frame in Main. <paramref name="match"/> is the round shown (null: none); <paramref name="keyHeld"/> is
        /// the scorecard key; <paramref name="hidden"/> hides both (pause menu, loading).
        /// </summary>
        public void Tick(MatchState match, PeerId local, Func<PeerId, string> name, double now, bool keyHeld, bool hidden)
        {
            if (_root == null)
                return;
            float dt = Time.unscaledDeltaTime;
            if (match == null || hidden)
            {
                Fade(_bannerGroup, 0f, dt);
                Fade(_cardGroup, 0f, dt);
                return;
            }

            // Banner.
            string banner = MatchText.Banner(match, local, name, out bool mine);
            if (mine && match.Turn != _bannerTurn)
                _mineSince = now;
            _bannerTurn = match.Turn;
            if (mine && now - _mineSince > MineBannerSeconds)
                banner = null;
            if (banner != null)
            {
                if (_bannerText.text != banner)
                    _bannerText.text = banner;
                _bannerImage.color = mine ? Green : _bannerColour;
            }
            Fade(_bannerGroup, banner != null ? 1f : 0f, dt);

            // Scorecard / results.
            bool finished = match.Phase == MatchPhase.Finished;
            if (finished && _finishedMatch != match.MatchId)
            {
                _finishedMatch = match.MatchId;
                _finishedAt = now;
            }
            bool results = finished && now - _finishedAt < ResultsSeconds;
            bool show = keyHeld || results || match.HoleComplete;
            if (show && (match.MatchId != _shownMatch || match.Sequence != _shownSequence || results != _shownResults))
                Fill(match, local, name, finished);
            Fade(_cardGroup, show ? 1f : 0f, dt);
        }

        private static void Fade(CanvasGroup group, float target, float dt)
        {
            if (group != null)
                group.alpha = Mathf.MoveTowards(group.alpha, target, FadePerSecond * dt);
        }

        private void Fill(MatchState match, PeerId local, Func<PeerId, string> name, bool finished)
        {
            _shownMatch = match.MatchId;
            _shownSequence = match.Sequence;
            _shownResults = finished;
            _heading.text = finished ? "Final results" : "Round - hole " + match.Hole.ToString(CultureInfo.InvariantCulture) + " of 9";
            for (int h = 0; h < MatchState.Holes; h++)
            {
                int par = match.Par(h + 1);
                _pars[h].text = par > 0 ? par.ToString(CultureInfo.InvariantCulture) : "-";
                bool current = !finished && h + 1 == match.Hole;
                _holeCells[h].color = new Color(Green.r, Green.g, Green.b, current ? 0.6f : 0.263f);
            }
            var standings = match.Standings();
            while (_rows.Count < standings.Count)
                _rows.Add(NewRow(_rows.Count));
            for (int i = 0; i < _rows.Count; i++)
                _rows[i].Root.gameObject.SetActive(i < standings.Count);
            bool anyPickUp = false;
            for (int i = 0; i < standings.Count; i++)
                anyPickUp |= FillRow(_rows[i], match, standings[i], local, name, finished);

            float footerTop = RowsTop + standings.Count * RowStep;
            string footer = finished ? MatchText.WinnerLine(match, name) : anyPickUp ? "* picked up" : null;
            _footerBacking.gameObject.SetActive(footer != null);
            if (footer != null)
            {
                Place(_footerBacking.rectTransform, Pad, footerTop, CardWidth - 2f * Pad, FooterHeight - 6f);
                _footer.text = footer;
                _footer.fontSize = finished ? 32f : 22f;
                _footerBacking.color = finished ? new Color(Yellow.r, Yellow.g, Yellow.b, 0.16f) : new Color(0f, 0f, 0f, 0f);
            }
            float height = footerTop + (footer != null ? FooterHeight : 0f) + Pad - Gap;
            _card.sizeDelta = new Vector2(CardWidth, height);
        }

        /// <summary>One player's line; true when it shows a picked-up hole.</summary>
        private bool FillRow(Row row, MatchState match, MatchStanding standing, PeerId local, Func<PeerId, string> name,
            bool finished)
        {
            var player = standing.Player;
            bool turn = !finished && match.Turn == player.Id;
            string shown = MatchText.Clip(name(player.Id), 16);
            string text = (turn ? "<color=#FFDD79>▶</color> " : string.Empty)
                          + "<color=#BBBBBB>" + MatchState.PositionText(standing) + "</color>  " + shown
                          + (player.Left ? " <color=#AAAAAA>(out)</color>" : string.Empty);
            row.Name.text = text;
            row.Name.color = player.Id == local ? Teal : Color.white;
            row.NameBacking.color = player.Id == local ? new Color(Teal.r, Teal.g, Teal.b, 0.18f) : new Color(1f, 1f, 1f, 0.08f);
            row.Group.alpha = player.Left ? 0.45f : 1f;
            bool pickUp = false;
            for (int h = 1; h <= MatchState.Holes; h++)
            {
                var cell = row.Cells[h - 1];
                var score = row.Scores[h - 1];
                var state = player.States[h - 1];
                int par = match.Par(h);
                int? value = match.HoleScore(player, h);
                if (value != null)
                {
                    score.text = value.Value.ToString(CultureInfo.InvariantCulture)
                                 + (state == HoleState.PickedUp ? "*" : string.Empty);
                    pickUp |= state == HoleState.PickedUp;
                    score.color = state == HoleState.Missed ? new Color(1f, 1f, 1f, 0.6f) : Color.white;
                    cell.color = par <= 0 ? _noScoreColour
                        : value.Value < par ? _underColour
                        : value.Value == par ? _parColour
                        : _overColour;
                    if (state == HoleState.Missed)
                        cell.color = new Color(cell.color.r, cell.color.g, cell.color.b, 0.45f);
                }
                else if (!finished && h == match.Hole && player.Strokes[h - 1] > 0)
                {
                    // Still playing it: strokes so far, dimmed.
                    score.text = "<size=70%>" + player.Strokes[h - 1].ToString(CultureInfo.InvariantCulture) + "</size>";
                    score.color = new Color(1f, 1f, 1f, 0.7f);
                    cell.color = new Color(_noScoreColour.r * 0.7f, _noScoreColour.g * 0.7f, _noScoreColour.b * 0.7f, 1f);
                }
                else
                {
                    score.text = "-";
                    score.color = Color.white;
                    cell.color = _noScoreColour;
                }
            }
            row.Total.text = standing.Thru > 0 ? standing.Total.ToString(CultureInfo.InvariantCulture) : "--";
            row.ToPar.text = MatchText.ScoreColour(standing.ToPar) + MatchState.ToParText(standing.Thru > 0 ? standing.ToPar : null)
                             + "</color>";
            return pickUp;
        }

        // ---- Teardown ---------------------------------------------------------------------------------------------

        public void Destroy()
        {
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            Forget();
        }

        /// <summary>Called when the scene goes away (the screens went with it).</summary>
        public void Forget()
        {
            _root = null;
            _hud = null;
            _banner = null;
            _bannerGroup = null;
            _bannerText = null;
            _bannerImage = null;
            _card = null;
            _cardGroup = null;
            _heading = null;
            _footer = null;
            _footerBacking = null;
            Array.Clear(_pars, 0, _pars.Length);
            Array.Clear(_holeCells, 0, _holeCells.Length);
            _rows.Clear();
            _shownMatch = 0;
            _shownSequence = 0;
            _bannerTurn = PeerId.None;
        }
    }
}
