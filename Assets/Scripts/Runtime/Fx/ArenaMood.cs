using UnityEngine;
using PoBox.Sim;

namespace PoBox.Fx
{
    /// <summary>
    /// The room reacting to the fight.
    ///
    /// The stands: the excitement reading sets how hard they bounce, a big moment makes them jump (the half
    /// of the hall behind the fighter who scored jumps highest), a punch harder than these two usually land
    /// sets cameras flashing, and during the walk-on, a count and the result the phone lights come out.
    ///
    /// The lamps: the four spots over the ring lift a little with the action and flash on a knockdown.
    /// <see cref="houseLights"/> dims them all for the walk-on, where the Timeline brings them down and back
    /// up. Two more spots, the sweeps, belong to the Timeline during the walk-on (it swings one onto each
    /// corner); when the bout is over they find the winner and stay on it.
    /// </summary>
    public class ArenaMood : MonoBehaviour
    {
        [Tooltip("The spots over the ring. Their authored intensity is the resting level.")]
        public Light[] ringLights = new Light[0];
        [Tooltip("Lamp housings whose emission follows the lights.")]
        public Renderer[] lampRenderers = new Renderer[0];
        [Tooltip("The two follow-spots. Off at rest; the walk-on Timeline and the winner's moment use them.")]
        public Light[] sweeps = new Light[0];
        [Tooltip("Intensity of a sweep when it is on the winner.")]
        public float sweepIntensity = 220f;
        [Tooltip("How much the lamps lift at full excitement, as a fraction of their resting level.")]
        public float lift = 0.35f;
        [Tooltip("Clean impulse that makes the crowd jump, N s.")]
        public float burstImpulse = 12f;
        [Tooltip("1 the hall as lit; towards 0 the ring lights come down. The walk-on Timeline animates it.")]
        [Range(0f, 1f)] public float houseLights = 1f;
        public Bout bout;

        static readonly int MoodId = Shader.PropertyToID("_PoBoxCrowdMood");
        static readonly int BurstId = Shader.PropertyToID("_PoBoxCrowdBurst");
        static readonly int FlashId = Shader.PropertyToID("_PoBoxCrowdFlash");
        static readonly int PhonesId = Shader.PropertyToID("_PoBoxCrowdPhones");
        static readonly int SideId = Shader.PropertyToID("_PoBoxCrowdSide");
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        /// <summary>0..1, decays over a second or two. The sound uses it too.</summary>
        public static float Burst { get; private set; }
        /// <summary>0..1: how many cameras are flashing in the stands.</summary>
        public static float Flash { get; private set; }

        float[] _rest;
        float _flash, _phones, _side, _nextPop;
        MaterialPropertyBlock _block;

        void Awake()
        {
            _rest = new float[ringLights.Length];
            for (int i = 0; i < ringLights.Length; i++) _rest[i] = ringLights[i] != null ? ringLights[i].intensity : 0f;
            _block = new MaterialPropertyBlock();
            Burst = 0f;
            Flash = 0f;
            foreach (Light s in sweeps) if (s != null) s.intensity = 0f;
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
            Shader.SetGlobalFloat(FlashId, 0f);
            Shader.SetGlobalFloat(PhonesId, 0f);
            Shader.SetGlobalFloat(SideId, 0f);
        }

        void OnHit(HitEvent e)
        {
            if (!e.clean) return;
            if (e.impulse >= burstImpulse) Burst = Mathf.Max(Burst, Mathf.Clamp01(e.impulse / 22f));
            // The cameras come out for what is out of the ordinary for these two.
            Excitement ex = Excitement.Instance;
            float usual = ex != null ? ex.UsualImpulse : burstImpulse;
            float surprise = Mathf.Clamp01((e.impulse - usual * 1.1f) / Mathf.Max(1f, usual * 0.5f));
            if (surprise > 0.1f)
            {
                Flash = Mathf.Max(Flash, surprise);
                if (bout != null) _side = e.attacker == bout.red ? -1f : 1f;
            }
        }

        void OnKnockdown(Fighter f, HitEvent cause)
        {
            Burst = 1f;
            Flash = 1f;
            _flash = 1f;
            if (bout != null) _side = f == bout.red ? 1f : -1f;      // the other corner's supporters
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            Burst = Mathf.MoveTowards(Burst, 0f, dt * 0.6f);
            Flash = Mathf.MoveTowards(Flash, 0f, dt * 0.9f);
            _flash = Mathf.MoveTowards(_flash, 0f, dt * 2.5f);
            _side = Mathf.MoveTowards(_side, 0f, dt * 0.5f);

            BoutPhase phase = bout != null ? bout.Phase : BoutPhase.Fight;
            bool show = phase == BoutPhase.WalkOn || phase == BoutPhase.Count || phase == BoutPhase.Results;
            _phones = Mathf.MoveTowards(_phones, show ? 1f : 0f, dt * 1.2f);
            // At the result the flashes keep coming, in bursts.
            if (phase == BoutPhase.Results && bout != null && bout.Winner != null && Time.unscaledTime >= _nextPop)
            {
                _nextPop = Time.unscaledTime + Random.Range(0.5f, 1.3f);
                Flash = Mathf.Max(Flash, Random.Range(0.5f, 1f));
                _side = bout.Winner == bout.red ? -1f : 1f;
            }

            float mood = Mathf.Clamp01(0.15f + Excitement.Value * 0.85f);
            Shader.SetGlobalFloat(MoodId, mood);
            Shader.SetGlobalFloat(BurstId, Burst);
            Shader.SetGlobalFloat(FlashId, Flash);
            Shader.SetGlobalFloat(PhonesId, _phones);
            Shader.SetGlobalFloat(SideId, _side);

            float gain = (1f + lift * Excitement.Value + _flash * 0.8f) * Mathf.Lerp(0.12f, 1f, houseLights);
            for (int i = 0; i < ringLights.Length; i++)
                if (ringLights[i] != null) ringLights[i].intensity = _rest[i] * gain;

            if (lampRenderers.Length > 0)
            {
                _block.SetColor(EmissionId, new Color(1f, 0.96f, 0.88f) * (2.2f * gain));
                foreach (Renderer r in lampRenderers)
                    if (r != null) r.SetPropertyBlock(_block);
            }

            // The sweeps: the Timeline's during the walk-on, the winner's at the result, off otherwise.
            if (phase == BoutPhase.WalkOn) return;
            Fighter winner = phase == BoutPhase.Results && bout != null ? bout.Winner : null;
            foreach (Light s in sweeps)
            {
                if (s == null) continue;
                if (winner != null)
                {
                    Vector3 target = winner.pelvis.transform.position + Vector3.up * 0.3f;
                    Quaternion aim = Quaternion.LookRotation(target - s.transform.position);
                    s.transform.rotation = Quaternion.Slerp(s.transform.rotation, aim, Mathf.Clamp01(dt * 3f));
                    s.intensity = Mathf.MoveTowards(s.intensity, sweepIntensity, dt * sweepIntensity * 1.5f);
                }
                else if (s.intensity > 0f) s.intensity = Mathf.MoveTowards(s.intensity, 0f, dt * sweepIntensity * 2f);
            }
        }
    }
}
