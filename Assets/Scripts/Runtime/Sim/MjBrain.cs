using PoBox.Mj;
using UnityEngine;

namespace PoBox.Sim
{
    /// <summary>
    /// What a trained boxer's fighter needs from its body on the MuJoCo plugin, around the policy that
    /// <see cref="MjRing"/> runs for it:
    ///   * the fighter's hurt goes into the drives (its DriveScale), and whether it is down into the ring,
    ///     which then lets the other boxer pass through it;
    ///   * while the referee counts over the other boxer, this one is held in its guard turned towards them,
    ///     and let go when they are up: the first moment of a training episode;
    ///   * a boxer down for long enough gets up with its get-up policy if it has one, shown a stand-in of its
    ///     opponent where the opponent stands (training/envs/getup.py), and the match policy has it back once it
    ///     has stood long enough (the fighter decides when);
    ///   * punches are recognised as they are thrown (a glove moving at the opponent's head faster than a guard
    ///     moves), and the ring's measured landings, stops and footsteps become the fight's events.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public class MjBrain : MonoBehaviour
    {
        public Fighter fighter;
        public MjBoxer boxer;
        public MjRing ring;
        [Tooltip("A glove closing on the opponent's head faster than this, m/s, is a punch being thrown.")]
        public float punchSpeed = 3.5f;

        public bool GettingUp { get; private set; }
        /// <summary>The critic's value of the present state, in the trainer's reward units: what the win-probability bar reads.</summary>
        public float Value => boxer != null ? boxer.Value : 0f;
        public bool HasValue => boxer != null && boxer.Running != null;

        bool _holding, _releaseNext;
        readonly float[] _punchNotedAt = { -9f, -9f };
        Transform[] _gloves;
        float _prevPelvisY;
        bool _landedThisFall;

        void OnEnable()
        {
            if (ring == null) return;
            ring.PunchLanded += OnLanded;
            ring.PunchStopped += OnStopped;
            ring.FootStep += OnFoot;
        }

        void OnDisable()
        {
            if (ring == null) return;
            ring.PunchLanded -= OnLanded;
            ring.PunchStopped -= OnStopped;
            ring.FootStep -= OnFoot;
        }

        void Start()
        {
            if (boxer.matchPolicy != null) boxer.SetPolicy(boxer.matchPolicy);
            _gloves = new[] { boxer.transform.FindDeep(boxer.prefix + "glove_l"), boxer.transform.FindDeep(boxer.prefix + "glove_r") };
        }

        /// <summary>A glove (0 left, 1 right): what the camera and the excitement read it by.</summary>
        public Transform Glove(int hand) => _gloves != null ? _gloves[hand] : null;

        void OnLanded(MjBoxer attacker, int hand, bool head, float speed, Vector3 at)
        {
            if (attacker == boxer) fighter.OnPunchLanded(hand == 0 ? -1 : 1, head, speed, at, ring.GloveVelocity(boxer, hand));
        }

        void OnStopped(MjBoxer defender, float speed)
        {
            if (defender == boxer && Bout.Fighting) fighter.stats.blocked++;
        }

        void OnFoot(MjBoxer whose, Vector3 at, float speed)
        {
            if (whose == boxer && !fighter.IsDown) SimBus.RaiseFootStep(at, speed);
        }

        /// <summary>Sent by the fighter when its time on the canvas is up. Answers only if there is a policy for getting up.</summary>
        public void BeginGetUp()
        {
            if (boxer.getUpPolicy == null) return;
            fighter.AcceptGetUp();
            GettingUp = true;
            boxer.SetPolicy(boxer.getUpPolicy);
            // The get-up stage shows a stand-in of the opponent in its guard where the opponent stands.
            Vector3 them = fighter.opponent != null ? fighter.opponent.pelvis.transform.position : boxer.transform.position + boxer.Forward;
            boxer.SetEpisodeAt(MjBoxer.Stand, 0, 0, 0, them.x, them.z);
        }

        /// <summary>Sent by the fighter when it is back on its feet: the match policy takes the body over where the get-up left it.</summary>
        public void EndGetUp() => BackToTheBout();

        /// <summary>Called when the fighter is stood back up: the policy starts from a clean slate, as an episode does.</summary>
        public void ResetBrain() => BackToTheBout();

        void BackToTheBout()
        {
            GettingUp = false;
            if (boxer.matchPolicy != null) boxer.SetPolicy(boxer.matchPolicy);
            boxer.SetEpisodeAt(MjBoxer.Match, 0, 0, 0, 0, 0);
        }

        void FixedUpdate()
        {
            if (!ring.Ready || fighter == null) return;
            boxer.DriveScale = fighter.DriveScale;
            ring.SetDown(boxer, fighter.IsDown);
            FloorImpact();
            if (!Bout.SimRunning) return;

            // While the referee counts over the other fighter this one stands in its guard, turned towards them,
            // and waits. When they are up it is let go from that stance, with a clear head.
            Fighter them = fighter.opponent;
            if (them != null && !fighter.IsDown)
            {
                Vector3 towards = them.pelvis.transform.position - fighter.pelvis.transform.position;
                if (them.IsDown)
                {
                    ring.Hold(boxer, towards);
                    _holding = true;
                    return;
                }
                if (_holding)
                {
                    ring.Hold(boxer, towards);      // one last look at where they have been stood, then go
                    _holding = false;
                    _releaseNext = true;
                    return;
                }
                if (_releaseNext)
                {
                    _releaseNext = false;
                    ring.Release(boxer);
                    ResetBrain();
                }
            }
            NotePunches();
        }

        /// <summary>
        /// A policy announces nothing: a punch is recognised by what it is, a glove moving at the opponent's head
        /// faster than a guard moves. Named by how the glove travels: straight, up into it, round at it, or low.
        /// </summary>
        void NotePunches()
        {
            if (fighter.IsDown || fighter.opponent == null || _gloves == null) return;
            Vector3 head = fighter.opponent.HeadPosition;
            float bodyY = fighter.opponent.torso != null ? fighter.opponent.torso.transform.position.y : head.y - 0.5f;
            for (int hand = 0; hand < 2; hand++)
            {
                Transform glove = _gloves[hand];
                if (glove == null || Time.time - _punchNotedAt[hand] < 0.35f) continue;
                Vector3 to = head - glove.position;
                float distance = to.magnitude;
                if (distance < 0.05f || distance > 1.3f) continue;
                Vector3 v = ring.GloveVelocity(boxer, hand);
                float closing = Vector3.Dot(v, to / distance);
                if (closing < punchSpeed) continue;
                _punchNotedAt[hand] = Time.time;
                float speed = Mathf.Max(0.01f, v.magnitude);
                PunchType type;
                if (glove.position.y < bodyY + 0.12f) type = PunchType.Body;
                else if (v.y / speed > 0.6f) type = PunchType.Uppercut;
                else if (closing / speed < 0.75f) type = PunchType.Hook;
                else type = hand == 0 ? PunchType.Jab : PunchType.Cross;
                fighter.NotePunch(hand == 0 ? -1 : 1, type);
            }
        }

        /// <summary>A body landing on the canvas, for the sound and the cameras: the pelvis coming down hard near the floor, once a fall.</summary>
        void FloorImpact()
        {
            float y = fighter.PelvisHeight;
            float falling = (_prevPelvisY - y) / Mathf.Max(1e-4f, Time.fixedDeltaTime);
            _prevPelvisY = y;
            if (!fighter.IsDown) { _landedThisFall = false; return; }
            if (_landedThisFall || y > 0.35f || falling < 1.2f) return;
            _landedThisFall = true;
            SimBus.RaiseFloorImpact(fighter.pelvis.transform.position, falling * fighter.totalMass * 0.1f);
        }
    }

    static class TransformSearch
    {
        /// <summary>A descendant by name, however deep.</summary>
        public static Transform FindDeep(this Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t)
            {
                Transform found = c.FindDeep(name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
