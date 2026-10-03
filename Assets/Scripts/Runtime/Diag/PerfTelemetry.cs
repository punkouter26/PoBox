using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PoBox.Diag
{
    /// <summary>
    /// Real-time performance readings for the HUD's frame-rate chip, its debug chip and the frame-time graph:
    /// frame time, set-pass calls, triangles, garbage per frame and memory from the engine's own profiler
    /// counters (which work in a release build), and the physics cost from a stopwatch round the step.
    ///
    /// It also holds the frame rate: on a phone, if the frame time sits over budget for two seconds the
    /// render scale steps down (never below <see cref="minScale"/>), and steps back up once there has been
    /// headroom for five. That is the heat governor. With the Adaptive Performance package's Android
    /// provider switched on it could act on the device's own thermal warning instead of on the symptom.
    /// </summary>
    public class PerfTelemetry : MonoBehaviour
    {
        public static PerfTelemetry Instance { get; private set; }

        public int targetFps = 60;
        [Header("Render-scale governor")]
        [Tooltip("Off in the editor unless Govern In Editor is set: it changes the pipeline asset, which in the editor is the one on disk (it is put back when play stops).")]
        public bool governor = true;
        [Tooltip("Lets the governor run in the editor, to watch it work.")]
        public bool governInEditor;
        /// <summary>Times the governor has stepped the render scale down, and up, since play began.</summary>
        public int StepsDown { get; private set; }
        public int StepsUp { get; private set; }
        [Range(0.5f, 1f)] public float minScale = 0.6f;

        public const int HistoryLength = 120;
        public readonly float[] frameHistory = new float[HistoryLength];
        public int HistoryHead { get; private set; }

        public float Fps { get; private set; }
        public float FrameMs { get; private set; }
        public float WorstMs { get; private set; }
        public float PhysicsMs { get; private set; }
        public long DrawCalls { get; private set; }
        public long Batches { get; private set; }
        public long SetPass { get; private set; }
        public long Triangles { get; private set; }
        public float GcKb { get; private set; }
        /// <summary>Garbage a frame, averaged over the last second or so. The single-frame figure jumps whenever anything is asked of the editor.</summary>
        public float GcAverageKb { get; private set; }
        public float MemoryMb { get; private set; }
        public float RenderScale { get; private set; } = 1f;
        /// <summary>0 good, 1 watch, 2 bad: the colour of the dot on the debug chip.</summary>
        public int Grade => FrameMs <= Budget * 1.1f ? 0 : FrameMs <= Budget * 1.6f ? 1 : 2;
        float Budget => 1000f / Mathf.Max(15, targetFps);

        ProfilerRecorder _draw, _batches, _setPass, _tris, _gc, _memory;
        float _smoothed = 16.6f, _over, _under, _originalScale = -1f;
        UniversalRenderPipelineAsset _asset;
        Mj.MjRing _ring;

        void Awake() => Instance = this;

        void OnEnable()
        {
            Application.targetFrameRate = targetFps;
            _draw = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _tris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            _memory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");

            _asset = UniversalRenderPipeline.asset;
            if (_asset != null)
            {
                _originalScale = _asset.renderScale;
                RenderScale = _originalScale;
            }
        }

        void OnDisable()
        {
            _draw.Dispose(); _batches.Dispose(); _setPass.Dispose();
            _tris.Dispose(); _gc.Dispose(); _memory.Dispose();
            if (_asset != null && _originalScale > 0f) _asset.renderScale = _originalScale;
            if (Instance == this) Instance = null;
        }

        /// <summary>Puts the render scale back where it started.</summary>
        public void RestoreScale()
        {
            if (_asset == null || _originalScale <= 0f) return;
            RenderScale = _originalScale;
            _asset.renderScale = _originalScale;
            _over = 0f; _under = 0f;
        }

        public void SetTargetFps(int fps)
        {
            targetFps = fps;
            Application.targetFrameRate = fps;
        }

        void Update()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            _smoothed += (ms - _smoothed) * 0.1f;
            FrameMs = _smoothed;
            Fps = _smoothed > 0.01f ? 1000f / _smoothed : 0f;

            frameHistory[HistoryHead] = ms;
            HistoryHead = (HistoryHead + 1) % HistoryLength;
            float worst = 0f;
            for (int i = 0; i < HistoryLength; i++) if (frameHistory[i] > worst) worst = frameHistory[i];
            WorstMs = worst;

            if (_draw.Valid) DrawCalls = _draw.LastValue;
            if (_batches.Valid) Batches = _batches.LastValue;
            if (_setPass.Valid) SetPass = _setPass.LastValue;
            if (_tris.Valid) Triangles = _tris.LastValue;
            if (_gc.Valid) GcKb = _gc.LastValue / 1024f;
            GcAverageKb += (GcKb - GcAverageKb) * 0.02f;
            if (_memory.Valid) MemoryMb = _memory.LastValue / (1024f * 1024f);
            // MuJoCo's own time this frame, timed by the ring round its steps.
            if (_ring == null) _ring = FindAnyObjectByType<Mj.MjRing>();
            PhysicsMs = _ring != null ? (float)_ring.PhysicsMs : 0f;

            Govern(ms);
        }

        void Govern(float ms)
        {
            if (!governor || _asset == null || (Application.isEditor && !governInEditor)) return;
            float budget = Budget;
            float dt = Time.unscaledDeltaTime;

            if (ms > budget * 1.15f) { _over += dt; _under = 0f; }
            else if (ms < budget * 0.8f) { _under += dt; _over = 0f; }
            else { _over = 0f; _under = 0f; }

            if (_over > 2f && RenderScale > minScale)
            {
                RenderScale = Mathf.Max(minScale, RenderScale - 0.1f);
                _asset.renderScale = RenderScale;
                _over = 0f;
                StepsDown++;
            }
            else if (_under > 5f && RenderScale < _originalScale)
            {
                RenderScale = Mathf.Min(_originalScale, RenderScale + 0.05f);
                _asset.renderScale = RenderScale;
                _under = 0f;
                StepsUp++;
            }
        }

        /// <summary>Frame-time sample <paramref name="i"/>, oldest first.</summary>
        public float Sample(int i) => frameHistory[(HistoryHead + i) % HistoryLength];
    }
}
