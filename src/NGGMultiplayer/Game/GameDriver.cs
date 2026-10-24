using UnityEngine;

namespace NGGMultiplayer.Game
{
    /// <summary>Unity frame hooks for <see cref="GameContext"/>; lives on the plugin's persistent object.</summary>
    public sealed class GameDriver : MonoBehaviour
    {
        private GameContext _context;

        internal void Initialize(GameContext context)
        {
            _context = context;
        }

        private void Update() => _context?.Tick(Time.unscaledDeltaTime);

        private void LateUpdate() => _context?.RaiseLateTick();

        private void OnGUI() => _context?.DrawGui();
    }
}
