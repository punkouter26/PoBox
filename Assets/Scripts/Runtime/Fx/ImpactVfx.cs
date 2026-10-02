using UnityEngine;
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

        void OnHit(HitEvent e)
        {
            float k = Mathf.Clamp01(e.impulse / fullImpulse);
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
            if (stars == null || f == null) return;
            stars.transform.position = f.HeadPosition + Vector3.up * 0.25f;
            stars.Emit(14);
        }

        void OnFloor(Vector3 point, float impulse)
        {
            if (dust == null) return;
            dust.transform.position = new Vector3(point.x, canvasY + 0.03f, point.z);
            dust.Emit(Mathf.RoundToInt(Mathf.Lerp(6f, 26f, Mathf.Clamp01(impulse / 80f))));
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
