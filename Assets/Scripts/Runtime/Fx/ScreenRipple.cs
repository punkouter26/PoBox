using PoBox.Sim;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PoBox.Fx
{
    /// <summary>
    /// The picture ripples under a punch out of the ordinary: a clean punch harder than <see cref="minImpulse"/> and
    /// than 1.4 times what these two usually land (Excitement.UsualImpulse) sends a ring out from where it landed on
    /// screen, bending the picture as it passes (Shaders/ScreenRipple.shader), for <see cref="seconds"/>.
    ///
    /// The full-screen pass that draws it (a FullScreenPassRendererFeature on each URP renderer, which PoBox/Do All/Graphics
    /// adds with this object) copies the whole picture, so it is switched on only while a ripple runs, and off again whenever this
    /// is disabled, so the renderer assets are left as they were found.
    /// </summary>
    public class ScreenRipple : MonoBehaviour
    {
        [Tooltip("The ripple's pass, one on each renderer (mobile and PC).")]
        public ScriptableRendererFeature[] passes = new ScriptableRendererFeature[0];
        [Tooltip("Impulse a clean punch needs at the least, N s.")]
        public float minImpulse = 14f;
        public float seconds = 0.45f;
        [Tooltip("How far the picture is bent at the ring, in screen heights, for a punch just over the line.")]
        public float amplitude = 0.012f;

        static readonly int RippleId = Shader.PropertyToID("_PoBoxRipple");
        static readonly int AspectId = Shader.PropertyToID("_PoBoxRippleAspect");
        float _start = -1f, _amp;
        Vector2 _centre;

        void OnEnable()
        {
            SimBus.Hit += OnHit;
            SetOn(false);
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SetOn(false);
        }

        void SetOn(bool on)
        {
            foreach (ScriptableRendererFeature p in passes)
                if (p != null && p.isActive != on) p.SetActive(on);
        }

        void OnHit(HitEvent e)
        {
            if (!e.clean) return;
            Excitement ex = Excitement.Instance;
            float line = Mathf.Max(minImpulse, ex != null ? ex.UsualImpulse * 1.4f : 0f);
            if (e.impulse < line) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 vp = cam.WorldToViewportPoint(e.point);
            if (vp.z <= 0f) return;
            _centre = new Vector2(vp.x, vp.y);
            _amp = amplitude * Mathf.Clamp(e.impulse / line, 1f, 1.8f);
            _start = Time.unscaledTime;
            SetOn(true);
        }

        void Update()
        {
            if (_start < 0f) return;
            float p = (Time.unscaledTime - _start) / seconds;
            if (p >= 1f)
            {
                _start = -1f;
                SetOn(false);
                return;
            }
            Camera cam = Camera.main;
            Shader.SetGlobalVector(RippleId, new Vector4(_centre.x, _centre.y, p, _amp));
            Shader.SetGlobalFloat(AspectId, cam != null ? cam.aspect : 9f / 16f);
        }
    }
}
