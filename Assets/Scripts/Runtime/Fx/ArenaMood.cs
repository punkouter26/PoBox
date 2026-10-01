using UnityEngine;
using PoBox.Sim;

namespace PoBox.Fx
{
    /// <summary>
    /// The room reacting to the fight. Pushes the excitement reading to the crowd shader (how hard the
    /// stands bounce), a short burst on every big moment (they jump), and the same reading into the
    /// overhead lamps, which lift a little with the action and flash on a knockdown.
    /// </summary>
    public class ArenaMood : MonoBehaviour
    {
        [Tooltip("The spots over the ring. Their authored intensity is the resting level.")]
        public Light[] ringLights = new Light[0];
        [Tooltip("Lamp housings whose emission follows the lights.")]
        public Renderer[] lampRenderers = new Renderer[0];
        [Tooltip("How much the lamps lift at full excitement, as a fraction of their resting level.")]
        public float lift = 0.35f;
        [Tooltip("Clean impulse that makes the crowd jump, N s.")]
        public float burstImpulse = 12f;

        static readonly int MoodId = Shader.PropertyToID("_PoBoxCrowdMood");
        static readonly int BurstId = Shader.PropertyToID("_PoBoxCrowdBurst");
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        /// <summary>0..1, decays over a second or two. The sound uses it too.</summary>
        public static float Burst { get; private set; }

        float[] _rest;
        float _flash;
        MaterialPropertyBlock _block;

        void Awake()
        {
            _rest = new float[ringLights.Length];
            for (int i = 0; i < ringLights.Length; i++) _rest[i] = ringLights[i] != null ? ringLights[i].intensity : 0f;
            _block = new MaterialPropertyBlock();
            Burst = 0f;
        }

        void OnEnable()
        {
            SimBus.Hit += OnHit;
            SimBus.Knockdown += OnKnockdown;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.Knockdown -= OnKnockdown;
            Shader.SetGlobalFloat(MoodId, 0.15f);
            Shader.SetGlobalFloat(BurstId, 0f);
        }

        void OnHit(HitEvent e)
        {
            if (e.clean && e.impulse >= burstImpulse) Burst = Mathf.Max(Burst, Mathf.Clamp01(e.impulse / 25f));
        }

        void OnKnockdown(Fighter f, HitEvent cause)
        {
            Burst = 1f;
            _flash = 1f;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            Burst = Mathf.MoveTowards(Burst, 0f, dt * 0.6f);
            _flash = Mathf.MoveTowards(_flash, 0f, dt * 2.5f);

            float mood = Mathf.Clamp01(0.15f + Excitement.Value * 0.85f);
            Shader.SetGlobalFloat(MoodId, mood);
            Shader.SetGlobalFloat(BurstId, Burst);

            float gain = 1f + lift * Excitement.Value + _flash * 0.8f;
            for (int i = 0; i < ringLights.Length; i++)
                if (ringLights[i] != null) ringLights[i].intensity = _rest[i] * gain;

            if (lampRenderers.Length > 0)
            {
                _block.SetColor(EmissionId, new Color(1f, 0.96f, 0.88f) * (2.2f * gain));
                foreach (Renderer r in lampRenderers)
                    if (r != null) r.SetPropertyBlock(_block);
            }
        }
    }
}
