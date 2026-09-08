using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Starts a mini-game scene from the picks SCN_MENU recorded in
    /// <see cref="Systems_MiniGameSelection"/>. When the scene is opened
    /// directly with no selection — the normal case while building or testing
    /// it in the Editor — it falls back to a default line-up so the scene is
    /// always playable on its own.
    /// </summary>
    public sealed class Systems_MiniGameLauncher : MonoBehaviour
    {
        [SerializeField] private Systems_ContestSpawner _spawner;
        [SerializeField] private Systems_MiniGameSelection _selection;

        // Called by the editor scene tool.
        public void EditorInitialize(Systems_ContestSpawner spawner, Systems_MiniGameSelection selection)
        {
            _spawner = spawner;
            _selection = selection;
        }

        private void Start()
        {
            if (_selection != null && _selection.HasSelection)
            {
                int[] picks = _selection.Picks;
                // Consumed so re-running this scene directly does not silently
                // reuse a stale line-up.
                _selection.Clear();
                _spawner.SpawnAndBegin(picks);
                return;
            }

            // Rematch: the match director reloads this scene after crowning a
            // champion, by which point the selection above has already been
            // consumed. Falling straight through to the defaults here is what
            // used to throw the player's line-up away between matches.
            if (_selection != null && _selection.LastPicks.Length > 0)
            {
                _spawner.SpawnAndBegin(_selection.LastPicks);
                return;
            }

            // ONE of each roster entry, in slot order -- which is what this
            // comment always claimed and the code did not do. `slotIndex %
            // rosterLength` filled every one of the 8 slots by wrapping, so a
            // 5-entry roster spawned 8 fighters: Standard, Grandma, Grandpa,
            // Raptor, Bot, and then Standard2, Grandma2, Grandpa2 as copies.
            // Duplicates make the contest unreadable -- two fighters with the
            // same brain on the same body are not a match-up -- and the round
            // log had to disambiguate them with a suffix. Unused slots take -1
            // and stay empty.
            int slotCount = _spawner.SlotCount;
            var defaults = new int[slotCount];
            int rosterLength = _spawner.Roster.Length;
            for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                defaults[slotIndex] = slotIndex < rosterLength ? slotIndex : -1;
            }
            _spawner.SpawnAndBegin(defaults);
        }
    }
}
