using UnityEngine;

namespace PoBox.Sim
{
    /// <summary>
    /// Two readings the whole broadcast hangs off.
    ///
    /// <b>Excitement</b> (0..1) is how much is happening right now, built only from things the physics
    /// measured: how fast a glove is closing on the other fighter's head, how far either centre of mass has
    /// strayed from its feet, how close they are, and a spike for every impulse that lands. The cameras cut
    /// on it, the crowd bounces to it, the sound bed rides it and the highlight reel ranks moments by it,
    /// so they all agree about when the fight got good.
    ///
    /// <b>Momentum</b> (0..1, red's share) is who is winning it: damage done, health left, who has scored
    /// lately and whether somebody is on the canvas. It is a stand-in for a trained critic's value estimate;
    /// when a policy with a value head is running, that number goes here instead.
    ///
    /// Both keep a short history for the HUD's graphs.
    /// </summary>
    public class Excitement : MonoBehaviour
    {
        public static Excitement Instance { get; private set; }
        public static float Value => Instance != null ? Instance._value : 0f;
        /// <summary>Red's chance of winning as it stands, 0..1.</summary>
        public static float RedShare => Instance != null ? Instance._share : 0.5f;

        public Bout bout;
        [Tooltip("Glove closing speed that counts as flat out, m/s.")]
        public float fullClosingSpeed = 8f;
        [Tooltip("Impulse that pins the meter on its own, N s.")]
        public float fullImpulse = 28f;
        [Tooltip("Seconds for a spike to fall to a third.")]
        public float spikeDecay = 2.5f;

        public const int HistoryLength = 240;
        public const float SampleInterval = 0.25f;
        public readonly float[] excitementHistory = new float[HistoryLength];
        public readonly float[] shareHistory = new float[HistoryLength];
        public int HistoryHead { get; private set; }
        public int HistoryCount { get; private set; }

        public float ClosingSpeed { get; private set; }

        float _value, _share = 0.5f, _spike, _sampleTimer, _recentRed, _recentBlue;

        void Awake() => Instance = this;

        void OnEnable()
        {
            SimBus.Hit += OnHit;
            SimBus.Knockdown += OnKnockdown;
            SimBus.PhaseChanged += OnPhase;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.Knockdown -= OnKnockdown;
            SimBus.PhaseChanged -= OnPhase;
            if (Instance == this) Instance = null;
        }

        void OnHit(HitEvent e)
        {
            _spike += Mathf.Clamp01(e.impulse / fullImpulse) * (e.clean ? 0.7f : 0.25f);
            if (bout == null) return;
            if (e.attacker == bout.red) _recentRed += e.damage; else _recentBlue += e.damage;
        }

        void OnKnockdown(Fighter f, HitEvent cause) => _spike = Mathf.Max(_spike, 1.2f);

        void OnPhase(BoutPhase from, BoutPhase to)
        {
            if (to == BoutPhase.Intro && bout != null && bout.Round == 1)
            {
                HistoryHead = 0; HistoryCount = 0;
                _share = 0.5f; _recentRed = 0f; _recentBlue = 0f; _spike = 0f;
            }
        }

        void Update()
        {
            if (bout == null || bout.red == null || bout.blue == null) return;
            float dt = Time.deltaTime;
            Fighter red = bout.red, blue = bout.blue;

            if (Bout.SimRunning)
            {
                float closing = Mathf.Max(Closing(red, blue), Closing(blue, red));
                ClosingSpeed = closing;
                Vector3 gap = red.pelvis.transform.position - blue.pelvis.transform.position;
                gap.y = 0f;
                float proximity = 1f - Mathf.Clamp01((gap.magnitude - 0.8f) / 2f);
                float wobble = 1f - Mathf.Min(red.BalanceMargin, blue.BalanceMargin);

                _spike *= Mathf.Exp(-dt / spikeDecay);
                float target = 0.12f * proximity
                             + 0.35f * Mathf.Clamp01(closing / fullClosingSpeed)
                             + 0.2f * wobble
                             + _spike;
                if (bout.Phase == BoutPhase.Count) target = Mathf.Max(target, 0.75f);
                if (bout.Phase == BoutPhase.Intro) target *= 0.4f;
                target = Mathf.Clamp01(target);

                // Up fast, down slowly: a camera that calmed down the instant a punch ended would never
                // stay on anything.
                float rate = target > _value ? 9f : 1.1f;
                _value += (target - _value) * Mathf.Clamp01(dt * rate);

                float fade = Mathf.Exp(-dt / 8f);
                _recentRed *= fade;
                _recentBlue *= fade;

                float x = (Bout.Score(red) - Bout.Score(blue)) / 22f
                        + (red.Health - blue.Health) / 30f
                        + (_recentRed - _recentBlue) / 10f
                        + (blue.IsDown ? 1.6f : 0f) - (red.IsDown ? 1.6f : 0f);
                float share = 1f / (1f + Mathf.Exp(-x));
                _share += (share - _share) * Mathf.Clamp01(dt * 2.5f);

                _sampleTimer += dt;
                if (_sampleTimer >= SampleInterval)
                {
                    _sampleTimer = 0f;
                    excitementHistory[HistoryHead] = _value;
                    shareHistory[HistoryHead] = _share;
                    HistoryHead = (HistoryHead + 1) % HistoryLength;
                    if (HistoryCount < HistoryLength) HistoryCount++;
                }
            }
            else if (bout.Phase == BoutPhase.Results)
            {
                float settled = bout.Winner == null ? 0.5f : bout.Winner == red ? 1f : 0f;
                _share += (settled - _share) * Mathf.Clamp01(Time.unscaledDeltaTime * 2f);
            }
        }

        /// <summary>Fastest of the attacker's gloves towards the defender's head, m/s. Never negative.</summary>
        static float Closing(Fighter attacker, Fighter defender)
        {
            if (attacker.IsDown) return 0f;
            Vector3 head = defender.HeadPosition;
            Vector3 headVelocity = defender.HeadVelocity;
            return Mathf.Max(GloveClosing(attacker.forearmL, head, headVelocity), GloveClosing(attacker.forearmR, head, headVelocity));
        }

        static float GloveClosing(BodyPart forearm, Vector3 head, Vector3 headVelocity)
        {
            if (forearm == null || forearm.strike == null) return 0f;
            Vector3 glove = forearm.strike.bounds.center;
            Vector3 to = head - glove;
            float d = to.magnitude;
            if (d < 0.01f || d > 1.4f) return 0f;
            Vector3 v = forearm.body.GetPointVelocity(glove) - headVelocity;
            return Mathf.Max(0f, Vector3.Dot(v, to / d));
        }

        /// <summary>History sample <paramref name="i"/> of <paramref name="buffer"/>, oldest first.</summary>
        public float Sample(float[] buffer, int i)
        {
            int start = HistoryCount < HistoryLength ? 0 : HistoryHead;
            return buffer[(start + i) % HistoryLength];
        }
    }
}
