using UnityEngine;
using UnityEngine.Rendering;
using PoBox.Sim;

namespace PoBox.Fx
{
    /// <summary>
    /// What a hit looks like. The particle systems sit in the scene (so their look is edited in the
    /// inspector, not here); this decides how many particles, where, and which way they go, from what was
    /// measured.
    ///
    /// Sweat leaves the head the way the glove was travelling, not straight out of the surface: each
    /// droplet is given the glove's own velocity, carried through and fanned out, at a speed that follows
    /// the glove's, so a hook throws it sideways and an uppercut throws it up. The droplets are stretched
    /// along their flight, fall under gravity and stop at the canvas. A ring snaps outward at the point of
    /// contact. Dust comes off the canvas where a body lands, and a small puff where a boot is planted hard.
    ///
    /// A punch harder than these two usually land, and a knockdown, also pulse the picture for a quarter of a
    /// second: <see cref="pulse"/> is a volume laid over the house look (more bloom, a little lens distortion,
    /// fringing and motion blur) whose weight is how far out of the ordinary the punch was. And a boxer whose
    /// tank is running down steams from the head, more the emptier it is.
    /// </summary>
    public class ImpactVfx : MonoBehaviour
    {
        [Tooltip("Droplets thrown off along the glove's path.")]
        public ParticleSystem sweat;
        [Tooltip("A flat ring that snaps outward at the point of contact.")]
        public ParticleSystem shock;
        [Tooltip("Dust off the canvas when a body lands, and under a planted boot.")]
        public ParticleSystem dust;
        [Tooltip("The burst on a knockdown.")]
        public ParticleSystem stars;
        [Tooltip("Vapour off the head of a boxer who is tiring.")]
        public ParticleSystem steam;
        [Tooltip("The volume laid over the house look for a moment after a punch out of the ordinary. Its weight is set here.")]
        public Volume pulse;
        [Tooltip("Seconds the pulse takes to go.")]
        public float pulseSeconds = 0.28f;
        [Tooltip("Tank (0 to 1) under which a boxer starts to steam, and the tank at which it steams fully. A bout between trained boxers takes the tank to about 0.8.")]
        public Vector2 steamTank = new Vector2(0.97f, 0.6f);
        [Tooltip("Puffs a second when steaming fully.")]
        public float steamRate = 9f;
        [Tooltip("Impulse of a body landing on the canvas that raises the most dust, N s.")]
        public float fullFall = 120f;
        [Tooltip("Impulse that gives the full effect, N s.")]
        public float fullImpulse = 22f;
        [Tooltip("Speed a boot has to come down at to raise dust, m/s.")]
        public float stepDustSpeed = 1.4f;
        [Tooltip("Height of the canvas.")]
        public float canvasY = 0f;

        void OnEnable()
        {
            SimBus.Hit += OnHit;
            SimBus.Knockdown += OnKnockdown;
            SimBus.FloorImpact += OnFloor;
            SimBus.FootStep += OnFootStep;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.Knockdown -= OnKnockdown;
            SimBus.FloorImpact -= OnFloor;
            SimBus.FootStep -= OnFootStep;
        }

        float _pulse;
        /// <summary>The weight of the pulse at this moment, 0 to 1.</summary>
        public float Pulse => _pulse;
        public bool HasPulse => pulse != null;
        readonly float[] _steamOwed = new float[2];

        void Update()
        {
            _pulse = Mathf.MoveTowards(_pulse, 0f, Time.unscaledDeltaTime / Mathf.Max(0.05f, pulseSeconds));
            if (pulse != null) pulse.weight = _pulse;

            Bout bout = Bout.Instance;
            if (steam == null || bout == null || !Bout.SimRunning) return;
            for (int i = 0; i < 2; i++)
            {
                Fighter f = i == 0 ? bout.red : bout.blue;
                if (f == null) continue;
                _steamOwed[i] += Mathf.InverseLerp(steamTank.x, steamTank.y, f.Stamina) * steamRate * Time.deltaTime;
                for (; _steamOwed[i] >= 1f; _steamOwed[i] -= 1f)
                {
                    var puff = new ParticleSystem.EmitParams
                    {
                        position = f.HeadPosition + Random.insideUnitSphere * 0.07f + Vector3.up * 0.08f,
                        velocity = f.HeadVelocity * 0.3f + new Vector3(Random.Range(-0.08f, 0.08f), Random.Range(0.25f, 0.5f), Random.Range(-0.08f, 0.08f)),
                    };
                    steam.Emit(puff, 1);
                }
            }
        }

        void OnHit(HitEvent e)
        {
            float k = Mathf.Clamp01(e.impulse / fullImpulse);
            if (e.clean)
            {
                Excitement ex = Excitement.Instance;
                float usual = ex != null ? ex.UsualImpulse : 12f;
                _pulse = Mathf.Max(_pulse, Mathf.Clamp01((e.impulse - usual * 1.1f) / Mathf.Max(1f, usual * 0.5f)));
            }
            Vector3 normal = e.normal.sqrMagnitude > 0.01f ? e.normal.normalized : Vector3.up;

            if (shock != null && k > 0.12f)
            {
                var ring = new ParticleSystem.EmitParams
                {
                    position = e.point,
                    startSize = Mathf.Lerp(0.25f, 0.9f, k),
                    velocity = Vector3.zero,
                };
                shock.Emit(ring, 1);
            }

            // Sweat only comes off skin, and more of it off a head than a body.
            if (sweat == null || !e.clean) return;
            int n = Mathf.RoundToInt(Mathf.Lerp(4f, 34f, k) * (e.zone == PartKind.Head ? 1f : 0.5f));
            // Carried on by the glove: its direction, with some of the surface's own and a little lift.
            Vector3 along = e.velocity.sqrMagnitude > 0.25f ? e.velocity.normalized : -normal;
            Vector3 dir = (along * 0.75f + normal * 0.35f + Vector3.up * 0.25f).normalized;
            float speed = Mathf.Lerp(2f, 7f, Mathf.Clamp01(e.gloveSpeed / 9f));
            for (int i = 0; i < n; i++)
            {
                // A cone of about thirty degrees round the carry direction.
                Vector3 spread = Random.insideUnitSphere * 0.55f;
                var drop = new ParticleSystem.EmitParams
                {
                    position = e.point + normal * 0.03f,
                    velocity = (dir + spread).normalized * speed * Random.Range(0.45f, 1f),
                };
                sweat.Emit(drop, 1);
            }
        }

        void OnKnockdown(Fighter f, HitEvent cause)
        {
            _pulse = 1f;
            if (stars == null || f == null) return;
            stars.transform.position = f.HeadPosition + Vector3.up * 0.25f;
            stars.Emit(14);
        }

        void OnFloor(Vector3 point, float impulse)
        {
            if (dust == null) return;
            dust.transform.position = new Vector3(point.x, canvasY + 0.03f, point.z);
            dust.Emit(Mathf.RoundToInt(Mathf.Lerp(2f, 26f, Mathf.Clamp01(impulse / fullFall))));
        }

        void OnFootStep(Vector3 point, float speed)
        {
            if (dust == null || speed < stepDustSpeed) return;
            int n = Mathf.RoundToInt(Mathf.Lerp(1f, 4f, Mathf.InverseLerp(stepDustSpeed, 4f, speed)));
            for (int i = 0; i < n; i++)
            {
                Vector2 off = Random.insideUnitCircle * 0.06f;
                var puff = new ParticleSystem.EmitParams
                {
                    position = new Vector3(point.x + off.x, canvasY + 0.02f, point.z + off.y),
                    velocity = new Vector3(off.x * 6f, Random.Range(0.1f, 0.3f), off.y * 6f),
                    startSize = Random.Range(0.10f, 0.22f),
                    startLifetime = Random.Range(0.4f, 0.8f),
                };
                dust.Emit(puff, 1);
            }
        }
    }
}
