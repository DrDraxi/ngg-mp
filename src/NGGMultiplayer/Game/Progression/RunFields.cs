namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// The shared RunSave fields by name (<see cref="SharedRunFields"/>), read and written in place. RunSave is a
    /// struct: callers pass <c>ref SaveManager.instance.m_run</c>, never a copy.
    /// </summary>
    internal static class RunFields
    {
        public static bool TryGetBool(ref RunSave r, string name, out bool value)
        {
            switch (name)
            {
                case "m_canGolf": value = r.m_canGolf; return true;
                case "m_golfUnlocked": value = r.m_golfUnlocked; return true;
                case "m_spinUnlocked": value = r.m_spinUnlocked; return true;
                case "m_aimUnlocked": value = r.m_aimUnlocked; return true;
                case "m_stancePanelUnlocked": value = r.m_stancePanelUnlocked; return true;
                case "m_windUnlocked": value = r.m_windUnlocked; return true;
                case "m_practiceUnlocked": value = r.m_practiceUnlocked; return true;
                case "m_ironUnlocked": value = r.m_ironUnlocked; return true;
                case "m_putterUnlocked": value = r.m_putterUnlocked; return true;
                case "m_driverUnlocked": value = r.m_driverUnlocked; return true;
                case "m_wedgeUnlocked": value = r.m_wedgeUnlocked; return true;
                case "m_hybridUnlocked": value = r.m_hybridUnlocked; return true;
                case "m_crowbarUnlocked": value = r.m_crowbarUnlocked; return true;
                case "m_cardUnlocked": value = r.m_cardUnlocked; return true;
                case "m_ticketUnlocked": value = r.m_ticketUnlocked; return true;
                case "m_serverCut": value = r.m_serverCut; return true;
                case "m_bunkerOpen": value = r.m_bunkerOpen; return true;
                case "m_bountiesRestored": value = r.m_bountiesRestored; return true;
                case "m_hitLuke": value = r.m_hitLuke; return true;
                case "m_hasReturnedAfterHittingLuke": value = r.m_hasReturnedAfterHittingLuke; return true;
                case "m_playTrueEnding": value = r.m_playTrueEnding; return true;
                case "m_seenTrueEnding": value = r.m_seenTrueEnding; return true;
                case "m_ballAwarded": value = r.m_ballAwarded; return true;
                case "m_foundFirstCash": value = r.m_foundFirstCash; return true;
                case "m_playedMulliganVideo": value = r.m_playedMulliganVideo; return true;
                case "m_playedNoWindVideo": value = r.m_playedNoWindVideo; return true;
                case "m_playedHoleSkipVideo": value = r.m_playedHoleSkipVideo; return true;
                case "m_playedCar2": value = r.m_playedCar2; return true;
                case "seenBulletproof": value = r.seenBulletproof; return true;
                case "finishedDemo": value = r.finishedDemo; return true;
                case "completedBountyOnEasy": value = r.completedBountyOnEasy; return true;
                default: value = false; return false;
            }
        }

        public static bool TrySetBool(ref RunSave r, string name, bool value)
        {
            switch (name)
            {
                case "m_canGolf": r.m_canGolf = value; return true;
                case "m_golfUnlocked": r.m_golfUnlocked = value; return true;
                case "m_spinUnlocked": r.m_spinUnlocked = value; return true;
                case "m_aimUnlocked": r.m_aimUnlocked = value; return true;
                case "m_stancePanelUnlocked": r.m_stancePanelUnlocked = value; return true;
                case "m_windUnlocked": r.m_windUnlocked = value; return true;
                case "m_practiceUnlocked": r.m_practiceUnlocked = value; return true;
                case "m_ironUnlocked": r.m_ironUnlocked = value; return true;
                case "m_putterUnlocked": r.m_putterUnlocked = value; return true;
                case "m_driverUnlocked": r.m_driverUnlocked = value; return true;
                case "m_wedgeUnlocked": r.m_wedgeUnlocked = value; return true;
                case "m_hybridUnlocked": r.m_hybridUnlocked = value; return true;
                case "m_crowbarUnlocked": r.m_crowbarUnlocked = value; return true;
                case "m_cardUnlocked": r.m_cardUnlocked = value; return true;
                case "m_ticketUnlocked": r.m_ticketUnlocked = value; return true;
                case "m_serverCut": r.m_serverCut = value; return true;
                case "m_bunkerOpen": r.m_bunkerOpen = value; return true;
                case "m_bountiesRestored": r.m_bountiesRestored = value; return true;
                case "m_hitLuke": r.m_hitLuke = value; return true;
                case "m_hasReturnedAfterHittingLuke": r.m_hasReturnedAfterHittingLuke = value; return true;
                case "m_playTrueEnding": r.m_playTrueEnding = value; return true;
                case "m_seenTrueEnding": r.m_seenTrueEnding = value; return true;
                case "m_ballAwarded": r.m_ballAwarded = value; return true;
                case "m_foundFirstCash": r.m_foundFirstCash = value; return true;
                case "m_playedMulliganVideo": r.m_playedMulliganVideo = value; return true;
                case "m_playedNoWindVideo": r.m_playedNoWindVideo = value; return true;
                case "m_playedHoleSkipVideo": r.m_playedHoleSkipVideo = value; return true;
                case "m_playedCar2": r.m_playedCar2 = value; return true;
                case "seenBulletproof": r.seenBulletproof = value; return true;
                case "finishedDemo": r.finishedDemo = value; return true;
                case "completedBountyOnEasy": r.completedBountyOnEasy = value; return true;
                default: return false;
            }
        }

        public static bool TryGetInt(ref RunSave r, string name, out int value)
        {
            switch (name)
            {
                case "bountyAccessLevel": value = r.bountyAccessLevel; return true;
                case "lmugcStartMulligans": value = r.lmugcStartMulligans; return true;
                case "lmugcStartNoWinds": value = r.lmugcStartNoWinds; return true;
                case "lmugcStartHoleSkips": value = r.lmugcStartHoleSkips; return true;
                case "lmugcScoreBest": value = r.lmugcScoreBest; return true;
                case "lmugcScoreNoAssistBest": value = r.lmugcScoreNoAssistBest; return true;
                default: value = 0; return false;
            }
        }

        public static bool TrySetInt(ref RunSave r, string name, int value)
        {
            switch (name)
            {
                case "bountyAccessLevel": r.bountyAccessLevel = value; return true;
                case "lmugcStartMulligans": r.lmugcStartMulligans = value; return true;
                case "lmugcStartNoWinds": r.lmugcStartNoWinds = value; return true;
                case "lmugcStartHoleSkips": r.lmugcStartHoleSkips = value; return true;
                case "lmugcScoreBest": r.lmugcScoreBest = value; return true;
                case "lmugcScoreNoAssistBest": r.lmugcScoreNoAssistBest = value; return true;
                default: return false;
            }
        }

        /// <summary>Names in <see cref="SharedRunFields"/> this class does not know (a mod bug); empty when consistent.</summary>
        public static string FindUnmapped()
        {
            var run = default(RunSave);
            foreach (var name in SharedRunFields.Bools)
            {
                if (!TryGetBool(ref run, name, out _))
                    return name;
            }
            foreach (var name in SharedRunFields.Ints)
            {
                if (!TryGetInt(ref run, name, out _))
                    return name;
            }
            foreach (var name in SharedRunFields.HostInts)
            {
                if (!TryGetInt(ref run, name, out _))
                    return name;
            }
            return string.Empty;
        }
    }
}
