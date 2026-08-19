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

            // One of each roster entry, in slot order.
            int slotCount = _spawner.SlotCount;
            var defaults = new int[slotCount];
            int rosterLength = _spawner.Roster.Length;
            for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                defaults[slotIndex] = rosterLength > 0 ? slotIndex % rosterLength : -1;
            }
            _spawner.SpawnAndBegin(defaults);
        }
    }
}
