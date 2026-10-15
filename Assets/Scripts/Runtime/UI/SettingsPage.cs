using UnityEngine;
using UnityEngine.UIElements;
using PoBox.Audio;
using PoBox.Diag;
using PoBox.League;

namespace PoBox.UI
{
    /// <summary>
    /// The settings page (Assets/UI/Settings.uxml), the same on the HUD's menu and on the first screen: sound,
    /// quality, the commentator's voice, the buzz of a hit, the frame rate, and throwing the ladder away. Every
    /// choice is kept on the device. RESET LADDER is only offered where there is a ladder.
    /// </summary>
    public sealed class SettingsPage
    {
        readonly Button _sound, _quality, _voice, _buzz, _fps, _reset;
        readonly Label _soundWord, _qualityBadge, _voiceWord, _buzzWord, _resetWord;
        readonly Glyph _resetGlyph;
        readonly LeagueTable _league;
        float _armedUntil;

        /// <summary>RESET LADDER has been pressed once and is waiting for the second: the page needs refreshing until it lapses.</summary>
        public bool Armed => Time.unscaledTime < _armedUntil + 0.3f;

        public SettingsPage(VisualElement root, LeagueTable league)
        {
            _league = league;
            _sound = root.Q<Button>("s-sound"); _soundWord = root.Q<Label>("s-sound-word");
            _quality = root.Q<Button>("s-quality"); _qualityBadge = root.Q<Label>("s-quality-badge");
            _voice = root.Q<Button>("s-voice"); _voiceWord = root.Q<Label>("s-voice-word");
            _buzz = root.Q<Button>("s-buzz"); _buzzWord = root.Q<Label>("s-buzz-word");
            _fps = root.Q<Button>("s-fps");
            _reset = root.Q<Button>("s-reset"); _resetWord = root.Q<Label>("s-reset-word"); _resetGlyph = root.Q<Glyph>("s-reset-glyph");
            if (_reset != null) _reset.EnableInClassList("hidden", league == null);

            Hook(_sound, () => AudioDirector.Muted = !AudioDirector.Muted);
            Hook(_voice, () => SpeechCommentary.Enabled = !SpeechCommentary.Enabled);
            Hook(_buzz, () => Haptics.Enabled = !Haptics.Enabled);
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
            Refresh();
        }

        void Hook(Button b, System.Action change)
        {
            if (b != null) b.clicked += () => { change(); Save(); Refresh(); };
        }

        /// <summary>What was chosen last time, put back. Run once a scene, before anything makes a sound.</summary>
        public static void Load()
        {
            AudioDirector.Muted = PlayerPrefs.GetInt("pobox.muted", 0) == 1;
            SpeechCommentary.Enabled = PlayerPrefs.GetInt("pobox.voice", 1) == 1;
            Haptics.Enabled = PlayerPrefs.GetInt("pobox.haptics", 1) == 1;
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf != null) perf.SetTargetFps(PlayerPrefs.GetInt("pobox.fps", perf.targetFps));
        }

        static void Save()
        {
            PlayerPrefs.SetInt("pobox.muted", AudioDirector.Muted ? 1 : 0);
            PlayerPrefs.SetInt("pobox.voice", SpeechCommentary.Enabled ? 1 : 0);
            PlayerPrefs.SetInt("pobox.haptics", Haptics.Enabled ? 1 : 0);
            if (PerfTelemetry.Instance != null) PlayerPrefs.SetInt("pobox.fps", PerfTelemetry.Instance.targetFps);
            PlayerPrefs.Save();
        }

        public void Refresh()
        {
            Toggle(_sound, _soundWord, "SOUND", !AudioDirector.Muted);
            Toggle(_voice, _voiceWord, "VOICE", SpeechCommentary.Enabled);
            Toggle(_buzz, _buzzWord, "BUZZ", Haptics.Enabled);
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

        static void Toggle(Button b, Label word, string label, bool on)
        {
            if (b == null) return;
            if (word != null) word.text = label + (on ? " ON" : " OFF");
            b.EnableInClassList("btn--on", on);
        }
    }
}
