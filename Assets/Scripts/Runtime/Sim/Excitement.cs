using UnityEngine;

namespace PoBox.Sim
{
    /// <summary>
    /// Two readings the whole broadcast hangs off.
    ///
    /// <b>Excitement</b> (0..1) is how much is happening right now, built only from things the physics
    /// measured: how fast a glove is closing on the other fighter's head, how near either fighter is to
    /// losing its balance or its legs, how close they are, and a spike for every punch that lands harder
    /// than these two usually hit. "Than usual" matters: trained fighters trade a punch and a half a second
    /// each, and a meter that jumped for every one of them would sit at the top all night and tell the
    /// cameras nothing. The cameras cut on it, the crowd bounces to it and the sound bed rides it, so they
    /// all agree about when the fight got good.
    ///
    /// <b>Win probability</b> (0..1, red's share) is who is going to win it, as far as anything in the ring
    /// can tell. It starts from the judges' cards as they stand (a lead counts for more the less time there
    /// is left to overturn it), adds what each fighter has left (health, how dazed, whether one is on the
    /// canvas) and then what the fighters themselves expect: each trained policy carries a critic, the
    /// network that learned in training to predict how the next couple of seconds will go for it, and the
    /// difference between the two critics' outlooks moves the bar before anything has landed. With the
    /// scripted stand-ins there is no critic and the bar is the cards and the health alone.
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
        public float spikeDecay = 1.6f;
        /// <summary>The clean impulse these two usually land, N s: a running average.</summary>
        public float UsualImpulse { get; private set; } = 10f;
        /// <summary>
        /// How much is landing now against how much usually does: clean impulse a second over the last
        /// second and a half, over the same thing over the last half minute. 1 is the bout's usual pace.
        /// </summary>
        public float Flurry
        {
            get
            {
                // Both averages start a bout empty and fill at their own pace; each is read as a share of
                // how full it could be by now, or the first half minute of every bout would read as a flurry.
                if (_fightTime < 4f) return 1f;
                float fast = _fast / (1f - Mathf.Exp(-_fightTime / 1.5f)), slow = _slow / (1f - Mathf.Exp(-_fightTime / 30f));
                return slow > 0.5f ? fast / slow : 1f;
            }
        }

        float _fast, _slow, _fightTime;

        public const int HistoryLength = 240;
        public const float SampleInterval = 0.25f;
        public readonly float[] excitementHistory = new float[HistoryLength];
        public readonly float[] shareHistory = new float[HistoryLength];
        public int HistoryHead { get; private set; }
        public int HistoryCount { get; private set; }

        public float ClosingSpeed { get; private set; }
        /// <summary>The two critics' outlooks against each other, -1 (blue's is better) to +1. 0 without critics.</summary>
        public float CriticEdge { get; private set; }
        public bool HasCritic { get; private set; }
        [Tooltip("Seconds over which a critic's usual level is learned; its outlook is how far it is from that.")]
        public float criticMemory = 15f;

        float _value, _share = 0.5f, _spike, _sampleTimer, _recentRed, _recentBlue;
        readonly Rl.PolicyBrain[] _brains = new Rl.PolicyBrain[2];
        readonly Fighter[] _brainOf = new Fighter[2];
        readonly float[] _mean = new float[2], _var = new float[2], _seen = new float[2];

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
            // Trained fighters trade a punch and a half a second each, all of them hard. One more of those
            // is not news. What is: more landing than usual (the flurry reading, below), and a punch well
            // above what these two usually land, which is rare by construction.
            float over = Mathf.Clamp01((e.impulse - 1.25f * UsualImpulse) / Mathf.Max(1f, 0.5f * UsualImpulse));
            if (e.clean)
            {
                _spike = Mathf.Min(1.2f, _spike + over * 0.6f);
                UsualImpulse += (e.impulse - UsualImpulse) * 0.04f;
                // Each landed impulse goes into both averages; what differs is how quickly they forget it.
                _fast += e.impulse / 1.5f;
                _slow += e.impulse / 30f;
            }
            if (bout == null) return;
            if (e.attacker == bout.red) _recentRed += e.damage; else _recentBlue += e.damage;
        }

        void OnKnockdown(Fighter f, HitEvent cause) => _spike = Mathf.Max(_spike, 1.2f);

        void OnPhase(BoutPhase from, BoutPhase to)
        {
            // A new bout starts from nothing: at its walk-on, or at its first introduction if it has none.
            if (to == BoutPhase.WalkOn || (to == BoutPhase.Intro && from != BoutPhase.WalkOn && bout != null && bout.Round == 1))
            {
                _value = 0.1f;
                HistoryHead = 0; HistoryCount = 0;
                _share = 0.5f; _recentRed = 0f; _recentBlue = 0f; _spike = 0f; _fast = 0f; _slow = 0f; _fightTime = 0f;
                _seen[0] = _seen[1] = 0f;
                CriticEdge = 0f;
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
                _fast *= Mathf.Exp(-dt / 1.5f);
                _slow *= Mathf.Exp(-dt / 30f);
                if (bout.Phase == BoutPhase.Fight) _fightTime += dt;
                float flurry = Mathf.Clamp01((Flurry - 1.15f) / 0.9f);
                // How near the nearer of the two is to losing its legs: nothing below half way, then steeply.
                float danger = Mathf.Clamp01((Mathf.Max(red.Daze01, blue.Daze01) - 0.5f) / 0.5f);
                float target = 0.10f * proximity
                             + 0.10f * Mathf.Clamp01(closing / fullClosingSpeed)
                             + 0.20f * wobble
                             + 0.40f * danger * danger
                             + 0.45f * flurry
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

                // The cards: a lead is worth more the less of the bout there is left to overturn it in.
                float boutSeconds = Mathf.Max(1f, bout.rounds * bout.roundSeconds);
                float progress = Mathf.Clamp01(((bout.Round - 1) * bout.roundSeconds + (bout.roundSeconds - bout.RoundTimeLeft)) / boutSeconds);
                float x = bout.judges.Lead(bout.rounds) * (3f + 5f * progress)
                        + (red.Health - blue.Health) / 40f
                        + (blue.Daze01 - red.Daze01) * 1.5f
                        + (_recentRed - _recentBlue) / 14f
                        + (blue.IsDown ? 2.5f : 0f) - (red.IsDown ? 2.5f : 0f)
                        + 0.8f * ReadCritics(red, blue, dt);
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

        /// <summary>
        /// What the two critics expect, against each other. A critic's value is in its own training's reward
        /// units and each fighter's sits at its own level, so each is read as a departure from its own
        /// recent average, in units of its own recent spread: a fighter whose critic has just dropped two
        /// spreads below its usual level expects trouble, whatever the number itself is.
        /// </summary>
        float ReadCritics(Fighter red, Fighter blue, float dt)
        {
            float edge = 0f;
            bool both = true;
            for (int k = 0; k < 2; k++)
            {
                Fighter f = k == 0 ? red : blue;
                if (_brainOf[k] != f) { _brainOf[k] = f; _brains[k] = f.GetComponent<Rl.PolicyBrain>(); _seen[k] = 0f; }
                Rl.PolicyBrain brain = _brains[k];
                if (brain == null || !brain.HasValue) { both = false; continue; }
                // On the canvas the match policy is not being asked anything, and its last answer is stale.
                if (f.IsDown || bout.Phase != BoutPhase.Fight) continue;
                float v = brain.Value;
                if (_seen[k] <= 0f) { _mean[k] = v; _var[k] = 1f; }
                float a = Mathf.Clamp01(dt / Mathf.Max(1f, criticMemory));
                float d = v - _mean[k];
                _mean[k] += a * d;
                _var[k] += a * (d * d - _var[k]);
                _seen[k] += dt;
                // Not until its usual level is known: three seconds of looking.
                float z = _seen[k] > 3f ? Mathf.Clamp(d / Mathf.Sqrt(_var[k] + 1e-3f), -3f, 3f) : 0f;
                edge += k == 0 ? z : -z;
            }
            HasCritic = both;
            float target = both ? Mathf.Clamp(edge / 3f, -1f, 1f) : 0f;
            CriticEdge += (target - CriticEdge) * Mathf.Clamp01(dt * 6f);
            return CriticEdge;
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
