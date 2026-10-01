using UnityEngine;
using PoBox.Sim;

namespace PoBox.Fx
{
    /// <summary>
    /// What a hit looks like. Four particle systems sit in the scene (so their look is edited in the
    /// inspector, not here); this aims them at the contact and emits a count scaled by the measured
    /// impulse, so a tap gives a puff and a knockdown shot gives a spray.
    /// </summary>
    public class ImpactVfx : MonoBehaviour
    {
        [Tooltip("Droplets thrown off along the contact normal.")]
        public ParticleSystem sweat;
        [Tooltip("A flat ring that snaps outward at the point of contact.")]
        public ParticleSystem shock;
        [Tooltip("Dust off the canvas when a body lands.")]
        public ParticleSystem dust;
        [Tooltip("The burst on a knockdown.")]
        public ParticleSystem stars;
        [Tooltip("Impulse that gives the full effect, N s.")]
        public float fullImpulse = 25f;

        void OnEnable()
        {
            SimBus.Hit += OnHit;
            SimBus.Knockdown += OnKnockdown;
            SimBus.FloorImpact += OnFloor;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.Knockdown -= OnKnockdown;
            SimBus.FloorImpact -= OnFloor;
        }

        void OnHit(HitEvent e)
        {
            float k = Mathf.Clamp01(e.impulse / fullImpulse);
            Vector3 normal = e.normal.sqrMagnitude > 0.01f ? e.normal : Vector3.up;

            if (shock != null && k > 0.12f)
            {
                shock.transform.SetPositionAndRotation(e.point, Quaternion.LookRotation(normal));
                var main = shock.main;
                main.startSize = Mathf.Lerp(0.25f, 0.9f, k);
                shock.Emit(1);
            }

            // Sweat only comes off skin, and more of it off a head than a body.
            if (sweat != null && e.clean)
            {
                sweat.transform.SetPositionAndRotation(e.point, Quaternion.LookRotation(normal));
                int n = Mathf.RoundToInt(Mathf.Lerp(4f, 36f, k) * (e.zone == PartKind.Head ? 1f : 0.5f));
                sweat.Emit(n);
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
            dust.transform.position = point + Vector3.up * 0.03f;
            dust.Emit(Mathf.RoundToInt(Mathf.Lerp(6f, 26f, Mathf.Clamp01(impulse / 80f))));
        }
    }
}
