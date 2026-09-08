using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Who a spawned fighter is, for everything that has to name or colour it.
    ///
    /// Two problems this replaces. First, every presentation system worked out
    /// the display name by string surgery on the GameObject name
    /// (<c>name.Replace("Contest_", "")</c>) — repeated in the two referees, the
    /// announcer and the drama camera, and the drama camera's variant used
    /// Contains(), so a round won by "Standard" focused on whichever of
    /// "Standard" and "Standard2" it happened to walk past first.
    ///
    /// Second, the scoreboard had no way to say WHICH fighter a plate belonged
    /// to. Grandma and Grandpa carry their own textures and are deliberately
    /// left untinted, so a ring holding "Grandma" and "Grandma2" showed two
    /// identical-looking fighters and two identically-styled plates. The
    /// spawner knows the tint and the copy wash it applied, so it is the only
    /// place that can answer this — it records the result here and the plates
    /// wear it as a swatch.
    ///
    /// Added by <see cref="Systems_ContestSpawner"/> at spawn time; test-scene
    /// harness only, so training scenes never carry one.
    /// </summary>
    public sealed class Systems_FighterIdentity : MonoBehaviour
    {
        [SerializeField] private string _displayName;
        [SerializeField] private Color _plateColor = Color.white;
        [SerializeField] private int _rosterIndex = -1;

        /// <summary>Name shown on plates, callouts and the match scoreboard.</summary>
        public string DisplayName => _displayName;

        /// <summary>Swatch colour that identifies this fighter in the ring.</summary>
        public Color PlateColor => _plateColor;

        /// <summary>
        /// Which <see cref="ContestRosterEntry"/> this fighter was spawned
        /// from, or -1 for a fighter placed by hand. It is the only reliable
        /// route from a body in the ring back to the BRAIN it was given, which
        /// is what <see cref="Systems_TaleOfTheTape"/> needs: the display name
        /// cannot do it, because a ring holding two of a kind names them
        /// "Grandma" and "Grandma2" and only the first matches any roster entry.
        /// </summary>
        public int RosterIndex => _rosterIndex;

        public void Initialize(string displayName, Color plateColor, int rosterIndex)
        {
            _displayName = displayName;
            _plateColor = plateColor;
            _rosterIndex = rosterIndex;
        }

        /// <summary>
        /// Identity of the fighter <paramref name="rig"/> belongs to, falling
        /// back to the old name-munging for anything not placed by the spawner
        /// (a fighter dropped into the scene by hand while testing).
        /// </summary>
        public static void Resolve(Component rig, out string displayName, out Color plateColor)
        {
            var identity = rig.GetComponent<Systems_FighterIdentity>();
            if (identity != null && !string.IsNullOrEmpty(identity.DisplayName))
            {
                displayName = identity.DisplayName;
                plateColor = identity.PlateColor;
                return;
            }
            displayName = rig.gameObject.name.Replace("Contest_", "");
            plateColor = Color.white;
        }
    }
}
