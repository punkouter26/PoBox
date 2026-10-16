using UnityEngine;
using UnityEngine.UIElements;
using PoBox.Audio;
using PoBox.Diag;
using PoBox.Fx;
using PoBox.League;
using PoBox.Net;

namespace PoBox.UI
{
    /// <summary>
    /// The settings (Assets/UI/Settings.uxml), the same on the HUD's menu, the first screen and the free-for-all: three
    /// tabs on one sheet. SOUND: sound and quality. DISPLAY: the commentator's voice, the buzz of a hit, bullet time and
    /// the frame rate. DEBUG: the collision shapes, the brain cam, the readings, the Twitch channel for chat plays, and
    /// throwing the ladder away. Every choice is kept on the device. RESET LADDER is only offered where there is a
    /// ladder, BRAIN only where there is a brain cam (the arena).
    /// </summary>
    public sealed class SettingsPage
    {
        static readonly string[] TabNames = { "sound", "display", "debug" };

        /// <summary>The brain cam over the arena's picture (HudView draws it).</summary>
        public static bool BrainCam;

        readonly Button[] _tabs = new Button[3];
        readonly VisualElement[] _pages = new VisualElement[3];
        readonly Button _sound, _quality, _voice, _buzz, _bullet, _fps, _xray, _brain, _reset;
        readonly Label _qualityBadge, _resetWord;
        readonly Glyph _resetGlyph;
        readonly TextField _twitch;
        readonly LeagueTable _league;
        float _armedUntil;

        /// <summary>RESET LADDER has been pressed once and is waiting for the second: the page needs refreshing until it lapses.</summary>
        public bool Armed => Time.unscaledTime < _armedUntil + 0.3f;
        public int Tab { get; private set; }

        /// <param name="readings">What READINGS does: opens the screen's debug panel. Null hides the button.</param>
        /// <param name="brain">The screen has a brain cam, so BRAIN is offered.</param>
        public SettingsPage(VisualElement root, LeagueTable league, System.Action readings = null, bool brain = false)
        {
            _league = league;
            for (int i = 0; i < 3; i++)
            {
                int tab = i;
                _tabs[i] = root.Q<Button>("s-tab-" + TabNames[i]);
                _pages[i] = root.Q<VisualElement>("s-page-" + TabNames[i]);
                if (_tabs[i] != null) _tabs[i].clicked += () => ShowTab(tab);
            }
            _sound = root.Q<Button>("s-sound");
            _quality = root.Q<Button>("s-quality"); _qualityBadge = root.Q<Label>("s-quality-badge");
            _voice = root.Q<Button>("s-voice");
            _buzz = root.Q<Button>("s-buzz");
            _bullet = root.Q<Button>("s-bullet");
            _fps = root.Q<Button>("s-fps");
            _xray = root.Q<Button>("s-xray");
            _brain = root.Q<Button>("s-brain");
            _reset = root.Q<Button>("s-reset"); _resetWord = root.Q<Label>("s-reset-word"); _resetGlyph = root.Q<Glyph>("s-reset-glyph");
            _twitch = root.Q<TextField>("s-twitch");
            _reset?.EnableInClassList("hidden", league == null);
            _brain?.EnableInClassList("hidden", !brain);

            Hook(_sound, () => AudioDirector.Muted = !AudioDirector.Muted);
            Hook(_voice, () => SpeechCommentary.Enabled = !SpeechCommentary.Enabled);
            Hook(_buzz, () => Haptics.Enabled = !Haptics.Enabled);
            Hook(_bullet, () => ArenaMood.BulletTime = !ArenaMood.BulletTime);
            Hook(_xray, () => PhysicsXray.On = !PhysicsXray.On);
            Hook(_brain, () => BrainCam = !BrainCam);
            Hook(_quality, () =>
            {
                int n = QualitySettings.names.Length;
                if (n > 0) QualitySettings.SetQualityLevel((QualitySettings.GetQualityLevel() + 1) % n, true);
            });
            Hook(_fps, () =>
            {
                PerfTelemetry perf = PerfTelemetry.Instance;
                if (perf != null) perf.SetTargetFps(perf.targetFps >= 60 ? 30 : 60);
            });
            Button telemetry = root.Q<Button>("s-telemetry");
            if (telemetry != null)
            {
                telemetry.EnableInClassList("hidden", readings == null);
                if (readings != null) telemetry.clicked += readings;
            }
            if (_twitch != null)
            {
                _twitch.value = PlayerPrefs.GetString(TwitchChat.PrefKey, "");
                _twitch.RegisterValueChangedCallback(e =>
                {
                    PlayerPrefs.SetString(TwitchChat.PrefKey, (e.newValue ?? "").Trim().TrimStart('#').ToLowerInvariant());
                    PlayerPrefs.Save();
                });
            }
            if (_reset != null) _reset.clicked += () =>
            {
                // Twice, because it throws away every result the ladder has.
                if (Time.unscaledTime < _armedUntil)
                {
                    if (_league != null) _league.ResetAll();
                    _armedUntil = 0f;
                }
                else _armedUntil = Time.unscaledTime + 3f;
                Refresh();
            };
            ShowTab(0);
        }

        void Hook(Button b, System.Action change)
        {
            if (b != null) b.clicked += () => { change(); Save(); Refresh(); };
        }

        public void ShowTab(int tab)
        {
            Tab = Mathf.Clamp(tab, 0, 2);
            for (int i = 0; i < 3; i++)
            {
                _tabs[i]?.EnableInClassList("seg-btn--on", i == Tab);
                _pages[i]?.EnableInClassList("hidden", i != Tab);
            }
            Refresh();
        }

        /// <summary>What was chosen last time, put back. Run once a scene, before anything makes a sound.</summary>
        public static void Load()
        {
            AudioDirector.Muted = PlayerPrefs.GetInt("pobox.muted", 0) == 1;
            SpeechCommentary.Enabled = PlayerPrefs.GetInt("pobox.voice", 1) == 1;
            Haptics.Enabled = PlayerPrefs.GetInt("pobox.haptics", 1) == 1;
            ArenaMood.BulletTime = PlayerPrefs.GetInt("pobox.bullettime", 1) == 1;
            PhysicsXray.On = PlayerPrefs.GetInt("pobox.xray", 0) == 1;
            BrainCam = PlayerPrefs.GetInt("pobox.brain", 0) == 1;
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf != null) perf.SetTargetFps(PlayerPrefs.GetInt("pobox.fps", perf.targetFps));
        }

        static void Save()
        {
            PlayerPrefs.SetInt("pobox.muted", AudioDirector.Muted ? 1 : 0);
            PlayerPrefs.SetInt("pobox.voice", SpeechCommentary.Enabled ? 1 : 0);
            PlayerPrefs.SetInt("pobox.haptics", Haptics.Enabled ? 1 : 0);
            PlayerPrefs.SetInt("pobox.bullettime", ArenaMood.BulletTime ? 1 : 0);
            PlayerPrefs.SetInt("pobox.xray", PhysicsXray.On ? 1 : 0);
            PlayerPrefs.SetInt("pobox.brain", BrainCam ? 1 : 0);
            if (PerfTelemetry.Instance != null) PlayerPrefs.SetInt("pobox.fps", PerfTelemetry.Instance.targetFps);
            PlayerPrefs.Save();
        }

        public void Refresh()
        {
            Toggle(_sound, "Sound", !AudioDirector.Muted);
            Toggle(_voice, "Voice", SpeechCommentary.Enabled);
            Toggle(_buzz, "Buzz", Haptics.Enabled);
            Toggle(_bullet, "Bullet time", ArenaMood.BulletTime);
            Toggle(_xray, "X-ray", PhysicsXray.On);
            Toggle(_brain, "Brain cam", BrainCam);
            if (_qualityBadge != null)
            {
                string[] names = QualitySettings.names;
                int level = QualitySettings.GetQualityLevel();
                _qualityBadge.text = level >= 0 && level < names.Length ? names[level].ToUpperInvariant() : "-";
            }
            if (_fps != null) _fps.text = (PerfTelemetry.Instance != null ? PerfTelemetry.Instance.targetFps : 60) + " FPS";
            if (_reset != null)
            {
                bool armed = Time.unscaledTime < _armedUntil;
                if (_resetWord != null) _resetWord.text = armed ? "SURE?" : "RESET LADDER";
                _reset.EnableInClassList("btn--danger", armed);
                // The glyph takes the text colour, which the armed state changes.
                if (_resetGlyph != null) _resetGlyph.MarkDirtyRepaint();
            }
        }

        /// <summary>The dot is lit by btn--on; the tooltip says it in words.</summary>
        static void Toggle(Button b, string label, bool on)
        {
            if (b == null) return;
            b.EnableInClassList("btn--on", on);
            b.tooltip = label + (on ? " on" : " off");
        }
    }
}
