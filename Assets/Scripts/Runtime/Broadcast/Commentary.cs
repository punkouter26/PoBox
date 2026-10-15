using UnityEngine;
using PoBox.Sim;

namespace PoBox.Broadcast
{
    /// <summary>
    /// The ticker's writer. Turns what the simulation measured into one line at a time: who landed what and
    /// how hard, who is on the canvas, and, when nothing has happened for a while, a number worth knowing
    /// (accuracy, the joint working hardest, how much energy has been spent). A boxer trained to tire gets a
    /// line as its muscles' reserve (MjBoxer.Reserve) falls through half, where its drives start to weaken,
    /// and another through a fifth: once each time it goes down through one, not again until it has rested
    /// back above it. Every figure it quotes is one the telemetry holds; it makes nothing up.
    /// </summary>
    public class Commentary : MonoBehaviour
    {
        public Bout bout;
        [Tooltip("Clean impulse that gets a line of its own, N s.")]
        public float callImpulse = 8f;
        [Tooltip("Seconds without an event before a statistic is read out.")]
        public float idleSeconds = 7f;

        float _lastLine, _lastHitLine;
        int _idleIndex;
        /// <summary>The reserve a tiring boxer's line is read at, and what it has to climb back over before it is read again.</summary>
        static readonly double[] TiredAt = { 0.5, 0.2 };
        const double Rested = 0.1;
        readonly bool[,] _tiredSaid = new bool[2, 2];

        void OnEnable()
        {
            SimBus.Hit += OnHit;
            SimBus.Knockdown += OnKnockdown;
            SimBus.GotUp += OnGotUp;
            SimBus.Line += OnLine;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.Knockdown -= OnKnockdown;
            SimBus.GotUp -= OnGotUp;
            SimBus.Line -= OnLine;
        }

        void OnLine(string text, int priority) => _lastLine = Time.unscaledTime;

        void OnHit(HitEvent e)
        {
            if (Time.unscaledTime - _lastHitLine < 1.4f) return;
            string hand = e.hand < 0 ? "left" : "right";
            string punch = PunchName(e.punch);

            if (e.clean && e.impulse >= callImpulse)
            {
                _lastHitLine = Time.unscaledTime;
                string where = e.zone == PartKind.Head ? "to the head" : "to the body";
                // Big is big for these two: a fifth harder than they usually land.
                Excitement ex = Excitement.Instance;
                bool big = e.impulse >= (ex != null ? ex.UsualImpulse * 1.2f : callImpulse * 2f);
                string verb = big ? "lands a big" : "lands a";
                // "A right to the body", not "a right body shot to the body".
                string what = e.punch == PunchType.Body ? hand : $"{hand} {punch}";
                SimBus.Say($"{e.attacker.displayName} {verb} {what} {where} · {e.impulse:0} N·s", 1);
            }
            else if (!e.clean && e.impulse >= callImpulse * 1.5f)
            {
                _lastHitLine = Time.unscaledTime;
                SimBus.Say($"{e.victim.displayName} takes the {punch} on the gloves · {e.impulse:0} N·s", 0);
            }
        }

        void OnKnockdown(Fighter f, HitEvent cause)
        {
            if (cause.attacker != null)
                SimBus.Say($"DOWN! {f.displayName} is on the canvas · {cause.impulse:0} N·s {PunchName(cause.punch)}", 3);
            else
                SimBus.Say($"{f.displayName} loses their feet", 2);
        }

        void OnGotUp(Fighter f)
        {
            int count = bout != null ? bout.Count : 0;
            SimBus.Say(count > 0 ? $"{f.displayName} is up at {count}" : $"{f.displayName} beats the count", 2);
        }

        void Update()
        {
            if (bout == null || bout.Phase != BoutPhase.Fight) return;
            Fighter a = bout.red, b = bout.blue;
            if (a == null || b == null) return;
            Tiring(0, a);
            Tiring(1, b);
            if (Time.unscaledTime - _lastLine < idleSeconds) return;

            string line = null;
            for (int tries = 0; tries < 4 && line == null; tries++)
            {
                switch (_idleIndex++ % 4)
                {
                    case 0:
                    {
                        Fighter f = a.stats.thrown >= b.stats.thrown ? a : b;
                        if (f.stats.thrown >= 3)
                            line = $"{f.displayName} has thrown {f.stats.thrown}, landed {f.stats.landed} ({100f * f.stats.landed / f.stats.thrown:0}%)";
                        break;
                    }
                    case 1:
                    {
                        Fighter f = a.PeakStress >= b.PeakStress ? a : b;
                        if (f.PeakStressPart != null && f.PeakStress > 0.35f)
                            line = $"{f.displayName}'s {JointName(f.PeakStressPart)} is at {f.PeakStress * 100f:0}% of its torque limit";
                        break;
                    }
                    case 2:
                    {
                        Fighter f = a.stats.energyJ >= b.stats.energyJ ? a : b;
                        if (f.stats.energyJ > 400f)
                            line = $"{f.displayName} has spent {f.stats.energyJ / 1000f:0.0} kJ, tank at {f.Stamina * 100f:0}%";
                        break;
                    }
                    default:
                    {
                        Fighter f = a.stats.peakImpulse >= b.stats.peakImpulse ? a : b;
                        if (f.stats.peakImpulse > 3f)
                            line = $"Hardest shot so far: {f.displayName}, {f.stats.peakImpulse:0} N·s";
                        break;
                    }
                }
            }

            _lastLine = Time.unscaledTime;
            if (line != null) SimBus.Say(line, 0);
        }

        /// <summary>The reserve falling through a line is said once; climbing back over it (with some rest to spare) lets it be said again.</summary>
        void Tiring(int corner, Fighter f)
        {
            Mj.MjBoxer body = f.boxer;
            if (body == null || !body.Bound || body.Cfg.fatigue_j <= 0.0) return;
            for (int t = 0; t < TiredAt.Length; t++)
            {
                if (_tiredSaid[corner, t]) { if (body.Reserve > TiredAt[t] + Rested) _tiredSaid[corner, t] = false; continue; }
                if (body.Reserve >= TiredAt[t]) continue;
                _tiredSaid[corner, t] = true;
                SimBus.Say(t == 0 ? $"{f.displayName}'s arms are getting heavy" : $"{f.displayName} is running on empty", 1);
            }
        }

        static string PunchName(PunchType t)
        {
            switch (t)
            {
                case PunchType.Jab: return "jab";
                case PunchType.Cross: return "cross";
                case PunchType.Hook: return "hook";
                case PunchType.Uppercut: return "uppercut";
                default: return "body shot";
            }
        }

        public static string JointName(BodyPart part)
        {
            string side = part.side < 0 ? "left " : part.side > 0 ? "right " : "";
            switch (part.kind)
            {
                case PartKind.UpperArm: return side + "shoulder";
                case PartKind.Forearm: return side + "elbow";
                case PartKind.Thigh: return side + "hip";
                case PartKind.Shin: return side + "knee";
                case PartKind.Foot: return side + "ankle";
                case PartKind.Torso: return "waist";
                case PartKind.Head: return "neck";
                default: return "pelvis";
            }
        }
    }
}
