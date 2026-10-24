// Stubs: dialogue / story (docs/gamemap/03, 04; docs/confirm answers).
using System.Collections;
using UnityEngine;

public enum Speakers
{
    Unknown,
    Luke,
}

public class DialogueLine : ScriptableObject
{
    // ACCESS-GUESS public: DialogueManager reads these.
    public Speakers speaker;
    public string dialogueText;
    public DialogueLine goesTo;
    public string invokeStart, invokeEnd;  // DialogueManager method names, run via Invoke
    public ChallengeSet challengeSetToLoad;  // a dialogue can load a challenge set too
    public bool showEvenIfServerCut, showIfBountiesRestored;
}

// OnTriggerEnter("First Person Character") -> StartDialogue; deactivates itself, SaveRun.
public class DialogueTrigger : MonoBehaviour
{
    private void OnTriggerEnter(Collider other) { throw null; }
}

public class DialogueManager : MonoBehaviour
{
    public static DialogueManager instance;

    public void StartDialogue(DialogueLine line, float delay) { throw null; }
    public void StartDialogue(DialogueLine line, float delay, string videoToPlay) { throw null; }
    public void NextButtonPressed() { throw null; }  // dialogue end; calls SaveRun
    private void LockPlayer(bool locking) { throw null; }  // sets MoveAndHitController.m_moveLocked
    public void PlayNoTextDialogue(Transform lookat, float time, string videoToPlay, string gateToOpen, DialogueLine dialogueAfter) { throw null; }
    private IEnumerator PadlockSequence(string gateToOpen) { throw null; }
    public IEnumerator OpenSequence() { throw null; }
    public IEnumerator TryTrueEnding() { throw null; }  // the only caller of SaveManager.UnlockNewBall
    private IEnumerator ServerSequence() { throw null; }
    private IEnumerator BunkerSequence() { throw null; }
    private IEnumerator FinalDoorSequence() { throw null; }

    // Some of the ~100 private parameterless story methods invoked by name (DialogueLine.invokeStart/invokeEnd,
    // Challenge.functionOnStart/functionOnComplete). Call them by name: DialogueManager.instance.Invoke(name, 0f).
    // With state effects:
    private void HideCar() { throw null; }                 // decoration "Car" off
    private void CloseGates() { throw null; }              // SetGates("securityGate", false)
    private void OpenGates() { throw null; }               // SetGates("securityGate", true)
    private void UnlockStance() { throw null; }
    private void UnlockPractice() { throw null; }
    private void UnlockWind() { throw null; }              // StartWind (not idempotent) + m_windUnlocked + SaveRun
    private void UnlockSpin() { throw null; }
    private void UnlockAdjust() { throw null; }
    private void CompletedDemo() { throw null; }           // also unlocks the club panel
    private void OpenShop() { throw null; }                // pro-shop door objects + DEMO / BOUNTYTUTE triggers
    private void OpenHole1Gates() { throw null; }
    private void OpenHole2Gates() { throw null; }
    private void EnableGreenTeeShort() { throw null; }     // GreatConnector.ActivateTee
    private void EnableGreenTeeLong() { throw null; }
    private void EnableRedTeeForMultiShot() { throw null; }
    private void EnableWedgeTutorial2Tee() { throw null; }
    private void EnableWedgeTutorial3Tee() { throw null; }
    private void EnableHybridTutorial2Tee() { throw null; }
    private void UnlockBounties1() { throw null; }
    private void UnlockBounties2() { throw null; }
    private void UnlockBounties3() { throw null; }
    private void DriverAvailable() { throw null; }         // ObjectStates.SetDriverAvailable
    private void WedgeAvailable() { throw null; }
    private void HybridAvailable() { throw null; }
    private void TicketAvailable() { throw null; }
    private void UnlockLMUGC() { throw null; }
    private void TicketReady() { throw null; }             // trigger "LMUGCREADY"
    private void TeleportCarpark() { throw null; }
    private void TeleportMD() { throw null; }
    private void TeleportMDTrap() { throw null; }
    private void EnableTrap() { throw null; }              // trigger "MarketingTrap"
    private void StartTeleporterDialogue() { throw null; }
    // Trigger "WEDGEAVAILABLE", doors hole5Shed + bathroom, LoadNewChallengeSet, adds a completed bounty
    // (m_crossCountyChallenge) and completes the cross-country pad.
    private void UnlockTeleporter() { throw null; }
    private void OpenBarDoor() { throw null; }             // door "bar" + trigger "STAIRS"
    private void EnableTopOfMountain() { throw null; }
    private void OpenMarketingDept() { throw null; }       // sliding door "MarketingDoor" + trigger + SaveRun
    private void RestoreBounties() { throw null; }         // ServerRestore + server screens
    private void EndLukeFadeDown() { throw null; }         // LukeVideo.LukeLeaveSequence: the true ending
    private void DeductMoney() { throw null; }             // only m_bank.ChangeBalance(-100000, true, false); no SaveRun
    private void ReturnMoney() { throw null; }             // only m_bank.ChangeBalance(+100000, true, false); no SaveRun

    // Presentation only (m_luke.PlayVideo(clip) and camera / UI): LookAt*, ShowNullRef, ShowConfettiNullRef,
    // ShowPowerRequirement / HidePowerRequirement, Luke*, EndLuke*, ReturnLuke* (ReturnLuke0, 1, 1a, 2, 3, 4).
    private void Luke1() { throw null; }
    private void Luke2() { throw null; }
    private void Luke3() { throw null; }
    private void Luke4() { throw null; }
    private void Luke5() { throw null; }
    private void Luke6() { throw null; }
    private void Luke7() { throw null; }
    private void Luke8() { throw null; }
    private void Luke9() { throw null; }
    private void Luke10() { throw null; }
    private void Luke11() { throw null; }
    private void Luke12() { throw null; }
    private void Luke13() { throw null; }
    private void Luke14() { throw null; }
    private void Luke15() { throw null; }
    private void Luke16() { throw null; }
    private void EndLuke0() { throw null; }
    private void EndLuke1() { throw null; }
    private void EndLuke2() { throw null; }
    private void EndLuke3() { throw null; }
    private void EndLuke4() { throw null; }
    private void EndLuke5() { throw null; }
    private void EndLuke6() { throw null; }
    private void EndLuke7() { throw null; }
    private void EndLuke8() { throw null; }
    private void EndLuke9() { throw null; }
    private void EndLuke10() { throw null; }
    private void EndLuke11() { throw null; }
    private void EndLuke12() { throw null; }
    private void EndLuke13() { throw null; }
    private void ReturnLuke0() { throw null; }
    private void ReturnLuke1() { throw null; }
    private void ReturnLuke1a() { throw null; }
    private void ReturnLuke2() { throw null; }
    private void ReturnLuke3() { throw null; }
    private void ReturnLuke4() { throw null; }

    public void PlayCarDialogue2() { throw null; }         // sets m_playedCar2
    public void ResetShot() { throw null; }
    public void UnlockDemoPads() { throw null; }
}
