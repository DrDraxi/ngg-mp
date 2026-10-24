using System;
using System.Collections.Generic;
using System.Globalization;
using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// The words Presence shows: notices, HUD labels, scorecard cells. Kept free of game and Unity types (unit tested).
    /// </summary>
    internal static class PresenceText
    {
        /// <summary>
        /// The shot's power as a fraction of the club's full swing: the game's power runs 0..<c>Club.powerVisualMult</c>
        /// (per club, often above 10; answers #53, #127). <see cref="UnknownPower"/> when the club's scale is unknown.
        /// </summary>
        public static float PowerFraction(float power, float powerVisualMult)
        {
            if (float.IsNaN(power) || float.IsInfinity(power) || float.IsNaN(powerVisualMult)
                || float.IsInfinity(powerVisualMult) || powerVisualMult <= 0f)
                return UnknownPower;
            return Math.Max(0f, power / powerVisualMult);
        }

        /// <summary>Sent as the shot power when the club's power scale could not be read.</summary>
        public const float UnknownPower = -1f;

        /// <summary>"Alice holed out in 3 (par 4)"; without par when it is unknown.</summary>
        public static string HoledToast(string player, uint strokes, int par)
        {
            string text = Name(player) + " holed out in " + strokes.ToString(CultureInfo.InvariantCulture);
            return par > 0 ? text + " (par " + par.ToString(CultureInfo.InvariantCulture) + ")" : text;
        }

        public static string BountyActivity(string setName) => string.IsNullOrEmpty(setName) ? "Bounty" : "Bounty " + setName;

        /// <summary>HUD activity of a player in a dialogue or cutscene of their own.</summary>
        public const string CutsceneActivity = "In a cutscene";

        public static string RoundActivity(int hole) => "LMUGC hole " + hole.ToString(CultureInfo.InvariantCulture);

        public static string ControlMode(byte mode) =>
            mode == InfoMessage.Golf ? "Golfing" : mode == InfoMessage.Crouch ? "Crouching" : "Walking";

        /// <summary>HUD label for a player who is not in the open world.</summary>
        public static string SceneLabel(byte scene)
        {
            switch ((GameSceneCode)scene)
            {
                case GameSceneCode.Intro: return "Intro";
                case GameSceneCode.Menu: return "In menu";
                case GameSceneCode.Credits: return "Credits";
                case GameSceneCode.Main: return null;
                default: return "Loading";
            }
        }

        /// <summary>Score relative to par: "E", "+2", "-1".</summary>
        public static string ToPar(int strokes, int par)
        {
            int diff = strokes - par;
            if (diff == 0)
                return "E";
            return diff > 0 ? "+" + diff.ToString(CultureInfo.InvariantCulture) : diff.ToString(CultureInfo.InvariantCulture);
        }

        private static string Name(string player) => string.IsNullOrEmpty(player) ? "Someone" : player;

        // GameScene's wire values (Game/GameScene.cs), restated so this file needs no Unity-side types.
        private enum GameSceneCode : byte
        {
            None = 0,
            Intro = 1,
            Menu = 2,
            Main = 3,
            Credits = 4,
            Other = 5
        }
    }

    /// <summary>What Presence knows about one other player: the latest info and their last stroke.</summary>
    internal sealed class PeerPresence
    {
        public InfoMessage Info;
        public bool HasInfo;
        public uint LastStroke;
        public double LastInfoAt;
    }

    /// <summary>Per-player presence state; rows appear on the first message and go when the player leaves.</summary>
    internal sealed class PresenceRoster
    {
        private readonly Dictionary<PeerId, PeerPresence> _peers = new Dictionary<PeerId, PeerPresence>();

        public int Count => _peers.Count;

        public bool TryGet(PeerId peer, out PeerPresence presence) => _peers.TryGetValue(peer, out presence);

        /// <summary>True when the info differs from what was known (the caller then refreshes visuals).</summary>
        public bool ApplyInfo(PeerId peer, InfoMessage info, double now)
        {
            var presence = GetOrAdd(peer);
            bool changed = !presence.HasInfo || !presence.Info.Equals(info);
            presence.Info = info;
            presence.HasInfo = true;
            presence.LastInfoAt = now;
            return changed;
        }

        public void ApplyStroke(PeerId peer, uint stroke) => GetOrAdd(peer).LastStroke = stroke;

        public void Remove(PeerId peer) => _peers.Remove(peer);

        public void Clear() => _peers.Clear();

        private PeerPresence GetOrAdd(PeerId peer)
        {
            if (!_peers.TryGetValue(peer, out var presence))
            {
                presence = new PeerPresence { Info = new InfoMessage { SelectedBall = -1 } };
                _peers.Add(peer, presence);
            }
            return presence;
        }
    }
}
