// Stubs: save system (docs/gamemap/01, 03, 04, 05). RunSave and UnlocksSave are structs: mutate
// SaveManager.instance.m_run in place.
// Confirmed against the real game (docs/confirm answers #29-#33, #49, #65, #69-#74, #79, #108, #135-#136, #139, #144):
// - SaveManager is per scene (not DontDestroyOnLoad); Awake sets instance only if it is null.
// - Saves are plain JsonUtility JSON (CompressSave / EncodeDecode are never called).
// - LoadRunSave dispatches by mode; LoadRunSave / LoadStoryRun / LoadNormalGolfRun assign m_run themselves.
//   GetBackUpStorySave does not; its only caller is SetUpScene's broken-save path.
// - SetUpScene runs only in "Main". Broken = Story && m_challengeSetLoaded == "" && (finishedDemo ||
//   m_completedBounties.Count > 0): writes run2.txt from the backup (or deletes run2.txt + run2_temp.txt) and reloads
//   "Main". An unknown set name makes GetChallengeSet return null and Instantiate(null) throw inside SetUpScene.
// - SaveRun snapshots synchronously and stops the previous _writeSave first; WriteSave waits while m_moveLocked, else
//   writes at once; its JustGolf branch is File.WriteAllText(m_normalGolfSavePath). A scene change while it waits
//   destroys the SaveManager and loses the write.
// - AddScoreToCard appends {score, "NormalFront9"} and File.WriteAllText(scoreCard.txt) at once (needs scores != null).
// - UpgradeDemoSave only edits m_run. LoadInLMUGC (Invoked 0.1 s after SetUpScene when isInLMUGC) resumes at the
//   first hole with lmugcScore < 1. Other writers: SaveGameSettings (config.txt), RebindSaveLoad (PlayerPrefs).
//   No save on application quit.
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SaveManager : MonoBehaviour
{
    private bool m_debugNoSave;
    private GameObject m_debugNoSaveUI;
    public bool m_isDemo;
    public bool m_isLimitedPlaytest;
    public static SaveManager instance;
    private float m_timer;
    private static string m_version = "v1";
    public BountyBalancer m_bountyBalancer;
    public GameModeState m_gamemodeState;
    public JustGolfSaveState m_normalGolfSaveState;
    public RunSave m_run;
    public ScoreCardSave m_scoreCardSave;
    public UnlocksSave m_unlocks;
    private Coroutine _writeSave;
    public GameSettings m_gameSettings;
    private static string m_codeWord;
    public bool dupeCheck;

    public string m_runSavePath => throw null;               // persistentDataPath + "/run2.txt"
    public string m_runSavePath_writeBackUp => throw null;   // ".../run2_temp.txt"
    public string m_normalGolfSavePath => throw null;        // ".../actuallyNormal.txt"
    public string m_scorecardSavePath => throw null;         // ".../scoreCard.txt"
    public string m_unlocksSavePath => throw null;           // ".../unlocks.txt"
    public string m_backupSavePath => throw null;            // ".../"
    private string m_settingsSavePath => throw null;         // ".../config.txt"

    private void Awake() { throw null; }
    private void Start() { throw null; }
    private void Update() { throw null; }
    public RunSave LoadRunSave() { throw null; }
    public RunSave LoadStoryRun() { throw null; }
    public RunSave GetBackUpStorySave() { throw null; }
    private void ApplyStoryModeDefaults() { throw null; }
    public RunSave LoadNormalGolfRun() { throw null; }
    private void ApplyGolfModeDefaults() { throw null; }
    public ScoreCardSave GetScoreCardSave() { throw null; }
    public UnlocksSave GetUnlocksSave() { throw null; }
    private void CreateBackupOfSave(string path, string contents) { throw null; }
    public void SaveUnlocks() { throw null; }
    public void AddScoreToCard(int score) { throw null; }
    private void UpgradeDemoSave() { throw null; }
    private void SetUpScene() { throw null; }
    private void AchievementsRetroUnlock() { throw null; }
    public bool HasRun() { throw null; }
    public bool HasStartedNine() { throw null; }
    public void SaveRun() { throw null; }
    private IEnumerator WriteSave(string json) { throw null; }
    private void SafeWrite(string targetPath, string backupPath, string json) { throw null; }
    public void DeleteRun() { throw null; }
    public void DeleteEverything() { throw null; }
    public GameSettings LoadGameSettings() { throw null; }
    private void ApplyDefaultSettings() { throw null; }
    private void LoadInLMUGC() { throw null; }
    public void ResetLMUGCSave() { throw null; }
    public void SaveGameSettings() { throw null; }
    private static string EncodeDecode(string data) { throw null; }
    public static string CompressSave(string json) { throw null; }
    public static string DecompressSave(string base64) { throw null; }
    public void SetBountyPadStates(int level) { throw null; }
    public bool UnlockNewBall() { throw null; }
    public void CheckForDuplicateGUIDs() { throw null; }
    public void SetGrassDensity() { throw null; }
    public void SetSmoothLook() { throw null; }
}

// All public, in this order (JsonUtility field order).
[Serializable]
public struct RunSave
{
    public Vector3 m_playerPosition;
    public Quaternion m_playerRotation;
    public string m_challengeSetLoaded;
    public int m_currentChallenge;
    public int bountyAccessLevel;
    public List<string> m_completedBounties;
    public List<string> m_doNotReloadChallengeList;
    public List<string> m_completedOnEasy;
    public bool completedBountyOnEasy;
    public SavedObjectData[] m_savedObjects;
    public string[] m_preHitTargets;
    public bool m_canGolf, m_golfUnlocked, m_spinUnlocked, m_aimUnlocked, m_stancePanelUnlocked, m_windUnlocked,
        m_practiceUnlocked, m_ironUnlocked, m_putterUnlocked, m_driverUnlocked, m_wedgeUnlocked, m_hybridUnlocked,
        m_crowbarUnlocked, m_cardUnlocked, m_ticketUnlocked, m_serverCut, m_bunkerOpen, m_bountiesRestored, m_hitLuke,
        m_hasReturnedAfterHittingLuke, m_playTrueEnding, m_seenTrueEnding, m_ballAwarded, m_foundFirstCash,
        m_playedMulliganVideo, m_playedNoWindVideo, m_playedHoleSkipVideo, m_playedCar2;
    public int m_bankBalance;
    public int m_shots;
    public float m_time;
    public bool seenBulletproof;
    public bool finishedDemo;
    public bool isInLMUGC;
    public int[] lmugcScore;
    public Vector3 lastShotPos;
    public int lmugcSaveScumCount;
    public int lmugcScoreBest;
    public int lmugcScoreNoAssistBest;
    public bool lmugcUsedAssist;
    public int lmugcStartMulligans;
    public int lmugcCurrentMulligans;
    public int lmugcStartNoWinds;
    public int lmugcCurrentNoWinds;
    public float lmugcNoWindTime;
    public int lmugcStartHoleSkips;
    public int lmugcCurrentHoleSkips;
}

[Serializable]
public struct UnlocksSave
{
    public int selectedBall;
    public List<bool> unlockedBalls;
    public bool showNewBallAlert;
}

[Serializable]
public class SavedObjectData
{
    public string m_id;
    public bool m_active;
    public Quaternion m_rotation;
}

public class SaveableObject : MonoBehaviour
{
    public string m_id;
    public bool m_saveRotation;
    public bool resetGUID;

    public virtual SavedObjectData GetSaveData() { throw null; }
    private void OnValidate() { throw null; }
    private void Reset() { throw null; }
}

// A struct (confirmed #14). Members undocumented (DifficultySelect sets m_difficulty; saved to config.txt).
[Serializable]
public struct GameSettings
{
}

// A struct (confirmed #14, #79); JsonUtility round-trips it.
[Serializable]
public struct ScoreCardSave
{
    public List<ScoreCardEntry> scores;
}

// A class (confirmed #14). Not the UI class ScorecardEntry.
[Serializable]
public class ScoreCardEntry
{
    public int score;
    public string course;
}

public class JustGolfSaveState : ScriptableObject
{
    public SavedObjectData[] m_objectDefaults;  // ACCESS-GUESS public
}

// CREDITS scene: reads persistentDataPath + "/run2.txt" through a string property of its own (confirmed #30; its
// name is not known, so the plugin finds it by type), not through SaveManager's path getters.
public class SaveReader : MonoBehaviour
{
}
