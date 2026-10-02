using UnityEngine;
using UnityEngine.Rendering.Universal;
using PoBox.Sim;

namespace PoBox.Fx
{
    /// <summary>
    /// What a fight leaves on the canvas. A pool of decal projectors sits in the scene; this lays them
    /// down where things happened and lets them fade.
    ///
    /// A clean punch throws sweat, and where that sweat comes down (the point of contact carried on by the
    /// glove's velocity for the time it takes to fall from head height) a wet spot is left: darker and
    /// bigger for a harder punch. A body landing leaves a scuff where it hit. A boot planted hard leaves a
    /// small one. Spots dry over a round or so; the oldest is taken when the pool runs out.
    /// </summary>
    public class CanvasMarks : MonoBehaviour
    {
        [Tooltip("The pool: decal projectors with the wet-spot material, switched off.")]
        public DecalProjector[] spots = new DecalProjector[0];
        [Tooltip("The pool: decal projectors with the scuff material, switched off.")]
        public DecalProjector[] scuffs = new DecalProjector[0];
        public Vector3 ringCentre;
        public float ringHalf = 3.05f;
        public float canvasY = 0f;
        [Tooltip("Seconds a wet spot takes to dry, and a scuff to be trodden out.")]
        public float spotSeconds = 45f, scuffSeconds = 90f;
        [Tooltip("Impulse that leaves the biggest spot, N s.")]
        public float fullImpulse = 22f;

        float[] _spotLeft = new float[0], _scuffLeft = new float[0], _spotFull = new float[0], _scuffFull = new float[0];
        int _nextSpot, _nextScuff;

        void Awake()
        {
            _spotLeft = new float[spots.Length]; _spotFull = new float[spots.Length];
            _scuffLeft = new float[scuffs.Length]; _scuffFull = new float[scuffs.Length];
            foreach (DecalProjector d in spots) if (d != null) d.enabled = false;
            foreach (DecalProjector d in scuffs) if (d != null) d.enabled = false;
        }

        void OnEnable()
        {
            SimBus.Hit += OnHit;
            SimBus.FloorImpact += OnFloor;
            SimBus.FootStep += OnFootStep;
            SimBus.PhaseChanged += OnPhase;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.FloorImpact -= OnFloor;
            SimBus.FootStep -= OnFootStep;
            SimBus.PhaseChanged -= OnPhase;
        }

        void OnPhase(BoutPhase from, BoutPhase to)
        {
            // A new bout is fought on a clean canvas.
            if (to != BoutPhase.WalkOn && !(to == BoutPhase.Intro && Bout.Instance != null && Bout.Instance.Round == 1)) return;
            for (int i = 0; i < spots.Length; i++) { _spotLeft[i] = 0f; if (spots[i] != null) spots[i].enabled = false; }
            for (int i = 0; i < scuffs.Length; i++) { _scuffLeft[i] = 0f; if (scuffs[i] != null) scuffs[i].enabled = false; }
        }

        void OnHit(HitEvent e)
        {
            if (!e.clean || e.zone != PartKind.Head || spots.Length == 0) return;
            float k = Mathf.Clamp01(e.impulse / fullImpulse);
            if (k < 0.35f) return;
            // Where the spray comes down: carried by a share of the glove's speed for as long as it takes to fall.
            float fall = Mathf.Sqrt(2f * Mathf.Max(0.1f, e.point.y - canvasY) / 9.81f);
            Vector3 carry = e.velocity * 0.35f * fall;
            Place(spots, _spotLeft, _spotFull, ref _nextSpot, e.point + carry, Mathf.Lerp(0.16f, 0.42f, k), spotSeconds, Random.Range(0f, 360f));
        }

        void OnFloor(Vector3 point, float impulse)
        {
            if (scuffs.Length == 0) return;
            Place(scuffs, _scuffLeft, _scuffFull, ref _nextScuff, point, Mathf.Lerp(0.4f, 0.9f, Mathf.Clamp01(impulse / 80f)), scuffSeconds, Random.Range(0f, 360f));
        }

        void OnFootStep(Vector3 point, float speed)
        {
            if (scuffs.Length == 0 || speed < 2.2f) return;
            Place(scuffs, _scuffLeft, _scuffFull, ref _nextScuff, point, Random.Range(0.16f, 0.24f), scuffSeconds * 0.5f, Random.Range(0f, 360f));
        }

        void Place(DecalProjector[] pool, float[] left, float[] full, ref int next, Vector3 at, float size, float seconds, float turn)
        {
            float limit = ringHalf - 0.1f;
            if (Mathf.Abs(at.x - ringCentre.x) > limit || Mathf.Abs(at.z - ringCentre.z) > limit) return;
            int i = next;
            next = (next + 1) % pool.Length;
            DecalProjector d = pool[i];
            if (d == null) return;
            // A projector looks along its own forward: straight down at the canvas.
            // A shallow box sitting on the cloth: it marks the canvas and not the boots standing on it.
            d.transform.SetPositionAndRotation(new Vector3(at.x, canvasY + 0.03f, at.z), Quaternion.Euler(90f, turn, 0f));
            d.size = new Vector3(size, size, 0.06f);
            d.pivot = new Vector3(0f, 0f, 0.03f);
            d.fadeFactor = 1f;
            d.enabled = true;
            left[i] = seconds;
            full[i] = seconds;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            Fade(spots, _spotLeft, _spotFull, dt);
            Fade(scuffs, _scuffLeft, _scuffFull, dt);
        }

        static void Fade(DecalProjector[] pool, float[] left, float[] full, float dt)
        {
            for (int i = 0; i < pool.Length; i++)
            {
                if (left[i] <= 0f || pool[i] == null) continue;
                left[i] -= dt;
                if (left[i] <= 0f) { pool[i].enabled = false; continue; }
                // Full for the first half of its life, then gone by the end.
                pool[i].fadeFactor = Mathf.Clamp01(left[i] / (full[i] * 0.5f));
            }
        }
    }
}
