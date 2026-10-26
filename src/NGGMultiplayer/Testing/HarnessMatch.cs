using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Presence;
using UnityEngine;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// The harness's 'match' command over the live game (the competitive nine holes, MatchSync): status, start (the
    /// machine's StartLMUGC button), resume (the ball's "Next Shot"), cup (drops the ball in the current hole's cup
    /// through BallTarget.OnTriggerEnter), pickup, card. Everything goes through the game's own code and the mod's
    /// patches, so the turn gate applies as in real play. Main thread only.
    /// </summary>
    internal static class HarnessMatch
    {
        private static readonly MethodInfo CupEnterMethod = AccessTools.Method(typeof(BallTarget), "OnTriggerEnter", new[] { typeof(Collider) });
        private static readonly FieldInfo IsHoleField = AccessTools.Field(typeof(BallTarget), "isHole");

        public static string Run(string action)
        {
            var sync = PresenceModule.Instance?.Match;
            if (sync == null)
                return "the presence module is not running";
            try
            {
                switch (action)
                {
                    case "status": return Status(sync);
                    case "card": return MatchText.Plain(sync.Match, sync.Context.LocalId, sync.Context.PlayerName);
                    case "start": return Start();
                    case "resume": return Resume(sync);
                    case "cup": return Cup(sync);
                    case "pickup": return sync.PickUpLocal() ? "picked up; " + Status(sync) : "nothing to pick up (not in a match, or the hole is done)";
                    default: return "usage: match status|start|resume|cup|pickup|card";
                }
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                return "the game threw " + inner.GetType().Name + ": " + inner.Message;
            }
        }

        private static string Status(MatchSync sync)
        {
            var text = new StringBuilder();
            var save = SaveManager.instance;
            var ball = HitManager.instance != null ? HitManager.instance.m_ball : null;
            var controller = MoveAndHitController.instance;
            text.Append("local: ");
            if (save != null)
            {
                var run = save.m_run;
                text.Append("inLMUGC ").Append(run.isInLMUGC).Append(" scores [");
                if (run.lmugcScore != null)
                    text.Append(string.Join(",", Array.ConvertAll(run.lmugcScore, s => s.ToString())));
                text.Append(']');
            }
            if (ball != null)
                text.Append(" ball shot ").Append(ball.m_currentShot).Append(" at ").Append(ball.transform.position.ToString())
                    .Append(ball.m_meshRenderer != null && !ball.m_meshRenderer.enabled ? " (killed)" : string.Empty)
                    .Append(ball.m_resumeBallTrigger != null && ball.m_resumeBallTrigger.activeInHierarchy ? " next-shot ready" : string.Empty);
            if (controller != null)
                text.Append(" mode ").Append(controller.m_mode);
            text.Append(" inMatch ").Append(sync.LocalInMatch).Append(" mayPlay ").Append(sync.LocalMayPlay)
                .Append(sync.HoldingNextHole ? " holding-next-tee" : string.Empty);
            string wait = sync.WaitReason();
            if (!string.IsNullOrEmpty(wait))
                text.Append(" wait '").Append(wait).Append('\'');
            // The HUD's turn banner (MatchHud), as the player sees it.
            string banner = MatchText.Banner(sync.Match, sync.Context.LocalId, sync.Context.PlayerName, out _);
            if (!string.IsNullOrEmpty(banner))
                text.Append(" banner '").Append(banner).Append('\'');
            text.Append('\n').Append(MatchText.Plain(sync.Match, sync.Context.LocalId, sync.Context.PlayerName));
            return text.ToString();
        }

        private static string Start()
        {
            foreach (var interactable in Compat.FindAll<FPSInteractable>(false))
            {
                if (interactable == null || interactable.m_interaction != InteractionType.StartLMUGC)
                    continue;
                interactable.Interact();
                return "FPSInteractable.Interact() (StartLMUGC) on " + interactable.name +
                       "; a client asks the host to start the round (watch 'match status')";
            }
            return "no StartLMUGC button in the scene (not in a JustGolf game?)";
        }

        private static string Resume(MatchSync sync)
        {
            var ball = HitManager.instance != null ? HitManager.instance.m_ball : null;
            if (ball == null || ball.m_resumeBallTrigger == null)
                return "no ball";
            if (!ball.m_resumeBallTrigger.activeInHierarchy)
                return "the ball's Next Shot is not offered now (shot " + ball.m_currentShot + ")";
            var interactable = ball.m_resumeBallTrigger.GetComponentInChildren<FPSInteractable>(true);
            if (interactable == null)
                return "the ball's InteractableBall has no FPSInteractable";
            int before = ball.m_currentShot;
            bool hit = ball.m_ballHit;
            interactable.Interact();
            string game = hit ? "by the game" : "by the game: m_ballHit False, it counts a Next Shot only after a swing";
            return "Next Shot: shot " + before + " -> " + ball.m_currentShot +
                   (ball.m_currentShot == before ? " (refused: " + (sync.LocalMayPlay ? game : sync.WaitReason()) + ")" : string.Empty);
        }

        /// <summary>Drops the ball in the cup of the hole the game is on, as if it rolled in.</summary>
        private static string Cup(MatchSync sync)
        {
            var save = SaveManager.instance;
            var ball = HitManager.instance != null ? HitManager.instance.m_ball : null;
            if (save == null || !save.m_run.isInLMUGC || ball == null)
                return "not in a round";
            var run = save.m_run;
            int hole = 1;
            if (run.lmugcScore != null)
            {
                foreach (int score in run.lmugcScore)
                {
                    if (score > 0)
                        hole++;
                }
            }
            if (CupEnterMethod == null || IsHoleField == null)
                return "BallTarget.OnTriggerEnter / isHole not found";
            BallTarget cup = null;
            foreach (var target in Compat.FindAll<BallTarget>(false))
            {
                if (target == null || target.holeNumber != hole || !(bool)IsHoleField.GetValue(target))
                    continue;
                if (cup == null || target.name.IndexOf("HOLE_Hole" + hole, StringComparison.OrdinalIgnoreCase) >= 0)
                    cup = target;
            }
            if (cup == null)
                return "no cup for hole " + hole;
            var collider = ball.GetComponent<Collider>() ?? ball.GetComponentInChildren<Collider>();
            if (collider == null)
                return "the ball has no collider";
            // At -1 the ball waits on the tee for its "Next Shot": no stroke yet, and the game would score the hole 0
            // (left unscored) yet still move on to the next tee.
            if (ball.m_currentShot < 0)
                return "no stroke on hole " + hole + " yet (shot " + ball.m_currentShot + "): 'match resume' first; " + Status(sync);
            int strokes = ball.m_currentShot + 1;
            // A ball only drops after a swing, which sets m_ballHit; the next tee's "Next Shot" counts a stroke only then.
            ball.m_ballHit = true;
            var body = ball.m_rb;
            if (body != null && !body.isKinematic)
                Compat.SetVelocity(body, Vector3.zero);
            ball.transform.position = cup.transform.position;
            CupEnterMethod.Invoke(cup, new object[] { collider });
            int scored = run.lmugcScore != null && hole - 1 < run.lmugcScore.Length ? SaveManager.instance.m_run.lmugcScore[hole - 1] : 0;
            return "ball into " + cup.name + " (hole " + hole + ", stroke " + strokes + "): " +
                   (scored > 0 ? "scored " + scored : "the game did not score it") + "; " + Status(sync);
        }
    }
}
