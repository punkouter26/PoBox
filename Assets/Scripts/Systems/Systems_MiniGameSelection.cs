using UnityEngine;

namespace PoBox
{
    /// <summary>Which mini-game the menu launched.</summary>
    public enum MiniGameKind
    {
        Balance = 0,
        Walk = 1
    }

    /// <summary>
    /// Carries the menu's picks from SCN_MENU into the mini-game scene it
    /// loads. A ScriptableObject rather than a singleton or static: both the
    /// menu and the receiving spawner hold a serialized reference to the same
    /// asset, so the hand-off survives the scene load with no global state and
    /// no DontDestroyOnLoad object (project rule: no singletons).
    ///
    /// Holds runtime state, so it is cleared on load — an asset edited in play
    /// mode keeps its values in the Editor, and a stale selection would make
    /// the mini-game skip its own menu on the next cold start.
    /// </summary>
    [CreateAssetMenu(menuName = "PoBox/Mini Game Selection")]
    public sealed class Systems_MiniGameSelection : ScriptableObject
    {
        [SerializeField] private MiniGameKind _game;
        [SerializeField] private int[] _picks = System.Array.Empty<int>();
        [SerializeField] private bool _hasSelection;
        // Survives Clear so a rematch can field the same line-up. See LastPicks.
        [SerializeField] private int[] _lastPicks = System.Array.Empty<int>();

        public MiniGameKind Game => _game;

        /// <summary>Roster index per slot; -1 leaves that slot empty.</summary>
        public int[] Picks => _picks;

        /// <summary>False on a cold start, so a mini-game scene run directly still shows its own menu.</summary>
        public bool HasSelection => _hasSelection;

        /// <summary>
        /// The most recent line-up this session, still readable after
        /// <see cref="Clear"/>.
        ///
        /// The match director restarts a decided match by reloading the scene,
        /// and the launcher's Start runs again against a selection the launcher
        /// itself consumed on the way in — so the rematch fell through to the
        /// default line-up and silently threw away what the player picked. Pick
        /// eight Grandmas, win the match, and round one of the rematch is the
        /// stock roster. Clearing HasSelection is still right (it is what stops
        /// a stale pick skipping the menu on a cold start); forgetting the picks
        /// themselves was not.
        /// </summary>
        public int[] LastPicks => _lastPicks;

        public void Set(MiniGameKind game, int[] picks)
        {
            _game = game;
            _picks = picks ?? System.Array.Empty<int>();
            _lastPicks = _picks;
            _hasSelection = true;
        }

        /// <summary>Consumed by the receiving scene so the selection is used exactly once.</summary>
        public void Clear()
        {
            _picks = System.Array.Empty<int>();
            _hasSelection = false;
        }

        private void OnEnable()
        {
            // Runtime state must not persist across sessions.
            _hasSelection = false;
            _picks = System.Array.Empty<int>();
            _lastPicks = System.Array.Empty<int>();
        }
    }
}
