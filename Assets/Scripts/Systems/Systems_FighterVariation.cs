using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Per-fighter body variation ranges. The editor scene builders apply
    /// these deterministically (seed + grid index), so rebuilding a scene
    /// always produces the same 16 slightly-different fighters. Static
    /// config only — runtime never mutates this.
    /// </summary>
    [CreateAssetMenu(menuName = "PoBox/Fighter Variation")]
    public sealed class Systems_FighterVariation : ScriptableObject
    {
        [SerializeField] private bool _enabled = true;
        [SerializeField] private int _seed = 1000;
        [Tooltip("±fraction applied to every body mass, one factor per fighter.")]
        [Range(0f, 0.3f)]
        [SerializeField] private float _massRange = 0.08f;
        [Tooltip("±fraction applied to joint spring/damper/max force, one factor per fighter.")]
        [Range(0f, 0.3f)]
        [SerializeField] private float _strengthRange = 0.10f;

        public bool Enabled => _enabled;
        public int Seed => _seed;
        public float MassRange => _massRange;
        public float StrengthRange => _strengthRange;
    }
}
