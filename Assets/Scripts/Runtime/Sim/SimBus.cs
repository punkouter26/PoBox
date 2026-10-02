using System;
using UnityEngine;

namespace PoBox.Sim
{
    public enum PartKind { Pelvis, Torso, Head, UpperArm, Forearm, Thigh, Shin, Foot }

    public enum PunchType { Jab, Cross, Hook, Uppercut, Body }

    /// <summary>
    /// One glove landing on the other fighter, measured from the contact itself: the impulse is what the
    /// solver applied over the first 50 ms of the touch, in newton-seconds, not a number the punch asked for.
    /// </summary>
    public struct HitEvent
    {
        public Fighter attacker;
        public Fighter victim;
        public PartKind zone;
        public PunchType punch;
        public int hand;            // -1 left, +1 right
        public Vector3 point;
        public Vector3 normal;      // points out of the victim
        public float impulse;       // N s
        public float gloveSpeed;    // m/s at first touch
        public float damage;        // health points taken, filled in by the victim
        public bool clean;          // head or body rather than arms
        public Vector3 velocity;    // the glove's own velocity at first touch, m/s (which way the sweat flies)
        public float time;          // bout clock, seconds
    }

    /// <summary>
    /// The one place the broadcast layer listens. The simulation raises what happened; the cameras, the
    /// effects, the sound, the commentary and the HUD each subscribe to what they need, so none
    /// of them knows about any of the others.
    /// </summary>
    public static class SimBus
    {
        public static event Action<HitEvent> Hit;
        public static event Action<Fighter, PunchType, int> PunchThrown;
        public static event Action<Fighter, HitEvent> Knockdown;
        public static event Action<Fighter> GotUp;
        public static event Action<Vector3, float> FloorImpact;      // point, impulse N s
        public static event Action<Vector3, float> FootStep;         // where a foot came down, how fast it was moving m/s
        public static event Action<BoutPhase, BoutPhase> PhaseChanged; // from, to
        public static event Action<string, int> Line;                // commentary text, priority

        public static void RaiseFootStep(Vector3 p, float speed) => FootStep?.Invoke(p, speed);
        public static void RaiseHit(in HitEvent e) => Hit?.Invoke(e);
        public static void RaisePunch(Fighter f, PunchType t, int hand) => PunchThrown?.Invoke(f, t, hand);
        public static void RaiseKnockdown(Fighter f, in HitEvent cause) => Knockdown?.Invoke(f, cause);
        public static void RaiseGotUp(Fighter f) => GotUp?.Invoke(f);
        public static void RaiseFloorImpact(Vector3 p, float impulse) => FloorImpact?.Invoke(p, impulse);
        public static void RaisePhase(BoutPhase from, BoutPhase to) => PhaseChanged?.Invoke(from, to);
        public static void Say(string text, int priority = 1) => Line?.Invoke(text, priority);

        // Statics outlive a play session when domain reload is off; a subscriber from the last run would
        // then be called on a destroyed object.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            Hit = null; PunchThrown = null; Knockdown = null; GotUp = null;
            FloorImpact = null; FootStep = null; PhaseChanged = null; Line = null;
        }
    }
}
