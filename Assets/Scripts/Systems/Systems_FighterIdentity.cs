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
        /// <summary>
        /// The line-up the menu offers, in pick order — and the ONE place that
        /// order is written down.
        ///
        /// It used to be written down twice and independently: as
        /// <c>_fighterNames</c> on the menu, and as the <c>_roster</c> array on
        /// each contest spawner. They agreed on the first five names and the
        /// menu had a sixth, "Nick", that no roster has any prefab for — so a
        /// player who picked Nick in a slot sent a roster index the receiving
        /// scene could not resolve and the slot came up empty, silently. Two
        /// lists of one thing is the defect; this is the list.
        ///
        /// The order must match the order the ships' scenes are laid out in, so
        /// that the default line-up lands on the marks the fighters are already
        /// standing on. See Systems_ContestSpawner.SpawnAndBegin, which adopts
        /// an author-placed fighter for a slot rather than spawning a duplicate.
        ///
        /// BOT IS FOURTH ON PURPOSE. The walk race fields the first four of this
        /// list, because four abreast is what its camera can hold on a phone (see
        /// Systems_RaceCamera), and every app in this line ships one code-driven
        /// bot — so the bot has to be in the default four rather than the sixth
        /// name that misses the cut. On the ring, which fields all six, the only
        /// effect is that the bot and the raptor swap two marks in a 2x4 grid.
        /// </summary>
        public static readonly string[] PickableNames =
        {
            "Standard", "Grandma", "Grandpa", "Bot", "Raptor", "Nick"
        };

        /// <summary>The menu's "leave this slot empty" choice. Never a fighter name.</summary>
        public const string EmptyPick = "Empty";

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
        /// route from a body in the ring back to the roster entry it came from:
        /// the display name cannot do it, because a ring holding two of a kind
        /// names them "Nick" and "Nick2" and only the first matches any entry.
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
