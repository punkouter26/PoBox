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
        [SerializeField] private string[] _picks = System.Array.Empty<string>();
        [SerializeField] private bool _hasSelection;
        // Survives Clear so a rematch can field the same line-up. See LastPicks.
        [SerializeField] private string[] _lastPicks = System.Array.Empty<string>();

        public MiniGameKind Game => _game;

        /// <summary>
        /// One fighter NAME per slot, or <see cref="Systems_FighterIdentity.EmptyPick"/>
        /// to leave that slot empty.
        ///
        /// NAMES, NOT ROSTER INDICES, and that is the fix rather than a
        /// preference. A pick used to travel as an index into the menu's own
        /// fighter list, which the receiving scene read as an index into ITS
        /// roster. The two lists were written down separately, agreed on five
        /// names and disagreed on the sixth, so picking the sixth silently
        /// emptied that slot. A name is either something the scene can field or
        /// it is not; an index is only meaningful beside the list it came from,
        /// and the list did not travel with it.
        /// </summary>
        public string[] Picks => _picks;

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
        public string[] LastPicks => _lastPicks;

        public void Set(MiniGameKind game, string[] picks)
        {
            _game = game;
            _picks = picks ?? System.Array.Empty<string>();
            _lastPicks = _picks;
            _hasSelection = true;
        }

        /// <summary>Consumed by the receiving scene so the selection is used exactly once.</summary>
        public void Clear()
        {
            _picks = System.Array.Empty<string>();
            _hasSelection = false;
        }

        private void OnEnable()
        {
            // Runtime state must not persist across sessions.
            _hasSelection = false;
            _picks = System.Array.Empty<string>();
            _lastPicks = System.Array.Empty<string>();
        }
    }
}
