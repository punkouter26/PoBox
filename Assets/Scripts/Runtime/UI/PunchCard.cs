using PoBox.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.UI
{
    /// <summary>
    /// The punch card: a heavy clean punch taken apart the way baseball's Statcast takes apart a pitch, read off the
    /// physics and nothing else. Every control step each arm's kinetic chain is sampled (MjBoxer.Chain: how fast the
    /// hip, the shoulder, the elbow and the glove are moving, and the floor's push through the feet); when a clean
    /// punch with at least <see cref="minImpulse"/> lands, the half second before it is read back: each link's peak
    /// speed and how long before the landing it came. A punch thrown from the floor up peaks in that order, each link
    /// faster than the one before, and the card says so (IN SEQUENCE) or not (ARMS ONLY). Under it: the glove's speed
    /// at the landing in km/h, the impulse, and the floor's hardest push in body weights.
    ///
    /// Shown for <see cref="showSeconds"/> over the picture, bottom right, at most once
    /// every <see cref="cooldown"/> seconds, never during a replay, on the results or with the brain cam on (which
    /// has the same strip of screen, from the left).
    /// </summary>
    public class PunchCard : MonoBehaviour
    {
        public Bout bout;
        [Tooltip("Impulse a clean punch needs for a card, N s (2.2 kg x glove speed; 11 is 5 m/s, about what the boxers land on average).")]
        public float minImpulse = 11f;
        public float showSeconds = 4f;
        public float cooldown = 12f;

        static readonly string[] Links = { "HIP", "SHOULDER", "ELBOW", "GLOVE" };
        const int Steps = 25;                       // half a second of control steps
        // [corner, hand, step, link]: the four speeds, then the floor's push.
        readonly float[,,,] _hist = new float[2, 2, Steps, 5];
        readonly float[] _speeds = new float[4];
        int _at;
        float _hideAt, _nextAt;

        VisualElement _root, _card;
        Label _title, _verdict, _foot;
        readonly VisualElement[] _bars = new VisualElement[4];
        readonly Label[] _values = new Label[4], _agos = new Label[4];

        UIDocument _doc;

        void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            SimBus.Hit += OnHit;
        }

        void OnDisable() => SimBus.Hit -= OnHit;

        /// <summary>Built on the HUD's tree, and again whenever the document gives it a new one (as HudView does).</summary>
        void Build(VisualElement root)
        {
            _root = root;
            _card = null;
            VisualElement safe = root?.Q<VisualElement>("safe");
            if (safe == null) return;
            _card = new VisualElement { name = "punch-card", pickingMode = PickingMode.Ignore };
            _card.AddToClassList("punch-card");
            _card.AddToClassList("hidden");
            _title = Add(_card, "punch-card-title");
            for (int i = 0; i < 4; i++)
            {
                var row = new VisualElement { pickingMode = PickingMode.Ignore };
                row.AddToClassList("punch-card-row");
                Add(row, "punch-card-link").text = Links[i];
                var track = new VisualElement { pickingMode = PickingMode.Ignore };
                track.AddToClassList("punch-card-track");
                _bars[i] = new VisualElement { pickingMode = PickingMode.Ignore };
                _bars[i].AddToClassList("punch-card-bar");
                track.Add(_bars[i]);
                row.Add(track);
                _values[i] = Add(row, "punch-card-value");
                _agos[i] = Add(row, "punch-card-ago");
                _card.Add(row);
            }
            _verdict = Add(_card, "punch-card-verdict");
            _foot = Add(_card, "punch-card-foot");
            safe.Add(_card);
        }

        static Label Add(VisualElement parent, string cls)
        {
            var l = new Label { pickingMode = PickingMode.Ignore };
            l.AddToClassList(cls);
            parent.Add(l);
            return l;
        }

        void FixedUpdate()
        {
            if (bout == null || bout.Phase != BoutPhase.Fight) return;
            for (int c = 0; c < 2; c++)
            {
                Fighter f = c == 0 ? bout.red : bout.blue;
                if (f == null || f.boxer == null || !f.boxer.Bound) continue;
                for (int h = 0; h < 2; h++)
                {
                    f.boxer.Chain(h, _speeds, out float floor);
                    for (int i = 0; i < 4; i++) _hist[c, h, _at, i] = _speeds[i];
                    _hist[c, h, _at, 4] = floor;
                }
            }
            _at = (_at + 1) % Steps;
        }

        void Update()
        {
            VisualElement root = _doc != null ? _doc.rootVisualElement : null;
            if (root != _root) Build(root);
            if (_card == null) return;
            bool show = Time.unscaledTime < _hideAt && bout != null && bout.Phase != BoutPhase.Results && !Mj.MjReplay.Playing
                && !SettingsPage.BrainCam;
            _card.EnableInClassList("hidden", !show);
        }

        void OnHit(HitEvent e)
        {
            if (_card == null || bout == null || !e.clean || e.impulse < minImpulse || Time.unscaledTime < _nextAt) return;
            int c = e.attacker == bout.red ? 0 : e.attacker == bout.blue ? 1 : -1;
            if (c < 0) return;
            int h = e.hand < 0 ? 0 : 1;
            // Each link's peak over the half second, and how many steps before now it came (the newest sample is _at - 1).
            float floor = 0f;
            var peak = new float[4];
            var ago = new int[4];
            for (int back = 0; back < Steps; back++)
            {
                int s = (_at - 1 - back + 2 * Steps) % Steps;
                for (int i = 0; i < 4; i++)
                    if (_hist[c, h, s, i] > peak[i]) { peak[i] = _hist[c, h, s, i]; ago[i] = back; }
                floor = Mathf.Max(floor, _hist[c, h, s, 4]);
            }
            float dt = Time.fixedDeltaTime, top = Mathf.Max(0.1f, peak[3]);
            for (int i = 0; i < 4; i++)
            {
                _bars[i].style.width = Length.Percent(Mathf.Clamp01(peak[i] / top) * 100f);
                _values[i].text = $"{peak[i]:0.0} m/s";
                _agos[i].text = $"-{ago[i] * dt:0.00} s";
            }
            // In sequence: each link peaks no earlier than the one before it and faster than it.
            bool chain = true;
            for (int i = 1; i < 4; i++) chain &= ago[i] <= ago[i - 1] && peak[i] > peak[i - 1];
            string side = e.hand < 0 ? "LEFT" : "RIGHT";
            _title.text = $"{e.attacker.displayName} · {side} {e.punch.ToString().ToUpperInvariant()}";
            _verdict.text = chain ? "IN SEQUENCE: HIP TO GLOVE" : "ARMS ONLY: CHAIN OUT OF ORDER";
            _verdict.EnableInClassList("punch-card-verdict--off", !chain);
            float weight = Mathf.Max(1f, e.attacker.totalMass * 9.81f);
            _foot.text = $"{e.gloveSpeed * 3.6f:0} km/h · {e.impulse:0.0} N s · floor {floor / weight:0.0}x body weight";
            _card.EnableInClassList("punch-card--blue", c == 1);
            _hideAt = Time.unscaledTime + showSeconds;
            _nextAt = Time.unscaledTime + cooldown;
        }
    }
}
