using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace NGGMultiplayer.Game.Menus
{
    /// <summary>
    /// Left / Right on a selected option button ("Who Can Join: &lt; Friends &gt;") changes its value, like the game's
    /// resolution arrows; the button's own Explicit navigation has no left / right target, so the event is free.
    /// </summary>
    internal sealed class SelectorInput : MonoBehaviour, IMoveHandler
    {
        public Action<int> Changed;

        public void OnMove(AxisEventData eventData)
        {
            if (eventData == null)
                return;
            int step = eventData.moveDir == MoveDirection.Left ? -1 : eventData.moveDir == MoveDirection.Right ? 1 : 0;
            if (step == 0)
                return;
            try
            {
                Changed?.Invoke(step);
            }
            catch (Exception e)
            {
                Core.Diagnostics.NetLog.Error($"[Menus] Option change failed: {e}");
            }
        }
    }
}
