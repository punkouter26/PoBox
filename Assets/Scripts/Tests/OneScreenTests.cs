using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using PoBox.Sim;
using PoBox.UI;

namespace PoBox.Tests
{
    /// <summary>
    /// The one-screen rule as a test. Loads the arena, points the HUD at a texture the shape of a phone, and
    /// fails if anything is drawn off the screen, anything scrolls, any text is under the legibility floor,
    /// any label is cut off, any button is smaller than a thumb, two overlays (or two pieces of furniture)
    /// lie over each other, one of the five frame anchors (title, fps, menu, debug, version) is missing or has
    /// left its corner, the way on is out of the thumb's reach, a button's word is more than a word, or the UI
    /// writes a warning to the log. Run for every shape in <see cref="Phones"/> (16:9 to 21:9 phones, notches, a
    /// punch hole, a home bar, a tablet and a foldable), through every state the HUD has: the walk-on, the round's banner, the fight
    /// with the menu and each settings tab, the debug panel and each graph, a count with its inset, the round
    /// break and the result. The first screen (the boxers, the settings' tabs, the cup, the debug panel), the
    /// free-for-all (the settings' tabs, the debug panel, the result) and the testbed get the same treatment,
    /// and the arena is checked to field the pair that was picked.
    /// </summary>
    public class OneScreenTests
    {
        /// <summary>A screen and what is cut out of it, in the phone's own pixels.</summary>
        public sealed class Phone
        {
            public readonly Vector2Int size;
            public readonly string cut;
            readonly int _top, _bottom, _left, _right;

            public Phone(int w, int h, string cut = "plain", int top = 0, int bottom = 0, int left = 0, int right = 0)
            {
                size = new Vector2Int(w, h);
                this.cut = cut;
                _top = top; _bottom = bottom; _left = left; _right = right;
            }

            public int x => size.x;
            public int y => size.y;

            /// <summary>
            /// The safe area in the screen pixels it is given in, or none for a screen with nothing cut out. The panel is
            /// drawn into a texture the shape of the phone but measured against the game view's width, so a phone's pixel
            /// is the game view's width over the phone's.
            /// </summary>
            public Rect? Safe()
            {
                if (_top == 0 && _bottom == 0 && _left == 0 && _right == 0) return null;
                float k = (float)Screen.width / size.x;
                return new Rect(_left * k, _bottom * k, Screen.width - (_left + _right) * k, Screen.height - (_top + _bottom) * k);
            }

            public override string ToString() => $"{size.x}x{size.y} {cut}";
        }

        /// <summary>
        /// 16:9 small and reference, 19.5:9, 20:9, 21:9, plain and with a notch (130 off the top, a home bar 80 off the
        /// bottom); a home bar alone; a punch-hole camera with curved edges (110 top, 60 bottom, 40 a side); a 3:4
        /// tablet plain and with its bar; a foldable's inner screen (about 5:6) plain and with a cutout down each side.
        /// </summary>
        static readonly Phone[] Phones =
        {
            new Phone(720, 1280), new Phone(720, 1280, "notch", 130, 80),
            new Phone(1080, 1920), new Phone(1080, 1920, "notch", 130, 80),
            new Phone(1080, 2340), new Phone(1080, 2340, "notch", 130, 80),
            new Phone(1080, 2400), new Phone(1080, 2400, "notch", 130, 80),
            new Phone(1080, 2520), new Phone(1080, 2520, "notch", 130, 80),
            new Phone(720, 1280, "bar", 0, 48),
            new Phone(1080, 2400, "hole", 110, 60, 40, 40),
            new Phone(1536, 2048), new Phone(1536, 2048, "bar", 0, 40),
            new Phone(1812, 2176), new Phone(1812, 2176, "sides", 60, 48, 60, 60),
        };

        [UnityTest]
        public IEnumerator HudFitsOneScreenWithNoScrolling([ValueSource(nameof(Phones))] Phone phone)
        {
            yield return SceneManager.LoadSceneAsync("Arena", LoadSceneMode.Single);
            yield return null;

            HudView hud = Object.FindAnyObjectByType<HudView>();
            Assert.IsNotNull(hud, "the Arena scene has no HudView");
            UIDocument document = hud.GetComponent<UIDocument>();
            PanelSettings panel = document.panelSettings;
            Assert.IsNotNull(panel, "the HUD has no panel settings");

            RenderTexture previous = panel.targetTexture;
            var target = new RenderTexture(phone.x, phone.y, 24);
            panel.targetTexture = target;
            var warnings = new UiWarnings();
            string at = phone.Safe() == null ? "" : phone.cut + " ";
            try
            {
                HudView.SafeAreaOverride = phone.Safe();
                // The walk-on, then each thing that can open over the picture.
                yield return Settle();
                Bout bout = Bout.Instance;
                Assert.IsNotNull(bout, "the Arena scene has no Bout");
                Check(hud.Root, phone, at + (bout.Phase == BoutPhase.WalkOn ? "walk-on" : "fight"));

                for (int tab = 0; tab < 3; tab++)
                {
                    hud.OpenMenu(tab);
                    yield return Settle();
                    Check(hud.Root, phone, at + "menu tab " + tab);
                }
                yield return CheckSettingsTabs(hud.Settings, hud.Root, phone, at + "menu, settings");
                // The one overlay rule: a tap on what is left of the picture puts the menu or the panel away.
                VisualElement picture = hud.Root.Q<VisualElement>("picture");
                yield return Tap(picture);
                Assert.IsFalse(hud.MenuOpen, "a tap on the picture did not close the menu");

                hud.ToggleDebug();
                yield return Settle();
                Check(hud.Root, phone, at + "debug panel");
                yield return Tap(picture);
                Assert.IsFalse(hud.DebugOpen, "a tap on the picture did not close the debug panel");

                // With the settings' STATS on: the frame rate, DEBUG, the version and the numbers in their places too.
                bool stats = SettingsPage.Stats;
                SettingsPage.Stats = true;
                Ui.Frame(hud.Root);
                yield return Settle();
                Check(hud.Root, phone, at + "stats on");
                SettingsPage.Stats = stats;
                Ui.Frame(hud.Root);

                // The round's banner, the fight, a count with its inset, a round break and the result, quickly:
                // two half-second rounds at four times the speed, off the owner's ladder.
                bout.league = null;
                bout.userTimeScale = 4f;
                bout.rounds = 2;
                yield return Until(() => bout.Phase == BoutPhase.Intro);
                yield return Settle();
                Check(hud.Root, phone, at + "round banner");
                yield return UntilTheBell();
                yield return Settle();
                Check(hud.Root, phone, at + "fight");

                bout.red.ForceDown();
                yield return Settle();
                Assert.AreEqual(BoutPhase.Count, bout.Phase, "a knockdown did not start a count");
                Check(hud.Root, phone, at + "count");

                bout.roundSeconds = 0.5f;           // set now: the count above needs a round that is still going
                bout.Restart();
                yield return Until(() => bout.Phase == BoutPhase.RoundBreak);
                yield return Settle();
                Assert.AreEqual(BoutPhase.RoundBreak, bout.Phase, "the first round never ended");
                Check(hud.Root, phone, at + "round break");

                yield return Until(() => bout.Phase == BoutPhase.Results);
                yield return Settle();
                Assert.AreEqual(BoutPhase.Results, bout.Phase, "the bout never reached a result");
                Check(hud.Root, phone, at + "results");
                warnings.AssertNone(at + "HUD");
            }
            finally
            {
                warnings.Dispose();
                HudView.SafeAreaOverride = null;
                panel.targetTexture = previous;
                target.Release();
                Object.Destroy(target);
            }
        }

        [UnityTest]
        public IEnumerator FreeForAllHudFitsOneScreen([ValueSource(nameof(Phones))] Phone phone)
        {
            if (!Application.CanStreamedLevelBeLoaded(MatchSelection.MeleeScene)) Assert.Ignore("the build has no Melee scene");
            yield return SceneManager.LoadSceneAsync(MatchSelection.MeleeScene, LoadSceneMode.Single);
            yield return null;

            MeleeView hud = Object.FindAnyObjectByType<MeleeView>();
            Assert.IsNotNull(hud, "the Melee scene has no MeleeView");
            PanelSettings panel = hud.GetComponent<UIDocument>().panelSettings;
            RenderTexture previous = panel.targetTexture;
            var target = new RenderTexture(phone.x, phone.y, 24);
            panel.targetTexture = target;
            var warnings = new UiWarnings();
            try
            {
                HudView.SafeAreaOverride = phone.Safe();
                yield return Settle();
                Check(hud.Root, phone, "free-for-all");
                hud.ToggleSettings();
                yield return Settle();
                Assert.IsTrue(hud.SettingsOpen, "the settings icon did not open the settings");
                yield return CheckSettingsTabs(hud.Settings, hud.Root, phone, "free-for-all, settings");
                yield return Tap(hud.Root.Q<VisualElement>("picture"));
                Assert.IsFalse(hud.SettingsOpen, "a tap on the picture did not close the settings");
                hud.ToggleDebug();
                yield return Settle();
                Check(hud.Root, phone, "free-for-all, debug panel");
                yield return Tap(hud.Root.Q<VisualElement>("picture"));
                Assert.IsFalse(hud.DebugOpen, "a tap on the picture did not close the debug panel");

                // The result card, quickly: one half-second round at four times the speed.
                FreeForAll rules = FreeForAll.Instance;
                yield return Until(() => rules.Phase != FreeForAll.Stage.Waiting);
                rules.rounds = 1;
                rules.roundSeconds = 0.5f;
                rules.Restart();
                Time.timeScale = 4f;
                yield return Until(() => hud.ResultOpen);
                yield return Settle();
                Assert.IsTrue(hud.ResultOpen, "the free-for-all never reached its result");
                Check(hud.Root, phone, "free-for-all, result");
                hud.ToggleSettings();
                yield return Settle();
                Assert.IsFalse(hud.ResultOpen, "the settings and the result card are both up");
                warnings.AssertNone("free-for-all");
            }
            finally
            {
                Time.timeScale = 1f;
                warnings.Dispose();
                HudView.SafeAreaOverride = null;
                panel.targetTexture = previous;
                target.Release();
                Object.Destroy(target);
            }
        }

        [UnityTest]
        public IEnumerator BoxerMenuFitsOneScreenAndKeepsTheCornersDifferent([ValueSource(nameof(Phones))] Phone phone)
        {
            if (!MatchSelection.HasMenu) Assert.Ignore("no trained boxers, so the build has no menu scene");
            yield return SceneManager.LoadSceneAsync(MatchSelection.MenuScene, LoadSceneMode.Single);
            yield return null;

            MenuView menu = Object.FindAnyObjectByType<MenuView>();
            Assert.IsNotNull(menu, "the Menu scene has no MenuView");
            PanelSettings panel = menu.GetComponent<UIDocument>().panelSettings;
            Assert.IsNotNull(panel, "the menu has no panel settings");

            RenderTexture previous = panel.targetTexture;
            var target = new RenderTexture(phone.x, phone.y, 24);
            panel.targetTexture = target;
            var warnings = new UiWarnings();
            try
            {
                HudView.SafeAreaOverride = phone.Safe();
                yield return Settle();
                Check(menu.Root, phone, "boxer menu");

                menu.ToggleSettings();
                yield return Settle();
                Assert.IsTrue(menu.SettingsOpen, "the settings icon did not open the settings");
                yield return CheckSettingsTabs(menu.Settings, menu.Root, phone, "boxer menu, settings");
                menu.ToggleCup();
                yield return Settle();
                Assert.IsFalse(menu.SettingsOpen, "the settings and the cup are both open");
                Check(menu.Root, phone, "boxer menu, cup");
                menu.ToggleDebug();
                yield return Settle();
                Assert.IsFalse(menu.CupOpen, "the cup and the debug panel are both open");
                Check(menu.Root, phone, "boxer menu, debug panel");
                yield return Tap(menu.Root.Q<VisualElement>("picture"));
                Assert.IsFalse(menu.DebugOpen, "a tap outside did not close the debug panel");

                if (menu.boxers.Length > 1)
                {
                    Assert.AreNotEqual(menu.Red, menu.Blue, "the menu opened with the same boxer in both corners");
                    int red = menu.Red, blue = menu.Blue;
                    menu.Pick(0, blue);
                    Assert.AreEqual(blue, menu.Red, "red did not take the boxer that was tapped");
                    Assert.AreEqual(red, menu.Blue, "tapping the other corner's boxer did not swap the two");
                    yield return Settle();
                    Check(menu.Root, phone, "boxer menu, corners swapped");
                }
                if (menu.boxers.Length > 2)
                {
                    // Tap order: whichever corner's turn it is takes the first card, the other corner the second.
                    int first = menu.Turn;
                    int a = (menu.Red + 1) % menu.boxers.Length;
                    while (a == menu.Blue) a = (a + 1) % menu.boxers.Length;
                    menu.Tap(a);
                    Assert.AreEqual(a, first == 0 ? menu.Red : menu.Blue, "the first tap did not fill the corner whose turn it was");
                    int b = (a + 1) % menu.boxers.Length;
                    while (b == menu.Red || b == menu.Blue) b = (b + 1) % menu.boxers.Length;
                    menu.Tap(b);
                    Assert.AreEqual(b, first == 0 ? menu.Blue : menu.Red, "the second tap did not fill the other corner");
                    Assert.AreNotEqual(menu.Red, menu.Blue);
                }
                warnings.AssertNone("boxer menu");
            }
            finally
            {
                warnings.Dispose();
                HudView.SafeAreaOverride = null;
                panel.targetTexture = previous;
                target.Release();
                Object.Destroy(target);
            }
        }

        [UnityTest]
        public IEnumerator TestbedViewFitsOneScreen([ValueSource(nameof(Phones))] Phone phone)
        {
#if UNITY_EDITOR
            // The testbed is an editor tool, not in the build, so it is loaded by its path.
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/Testbed.unity", new LoadSceneParameters(LoadSceneMode.Single));
#else
            Assert.Ignore("the testbed is not in the build");
#endif
            yield return null;
            var hud = Object.FindAnyObjectByType<PoBox.UI.TestbedView>();
            Assert.IsNotNull(hud, "the Testbed scene has no TestbedView");
            PanelSettings panel = hud.GetComponent<UIDocument>().panelSettings;
            RenderTexture previous = panel.targetTexture;
            var target = new RenderTexture(phone.x, phone.y, 24);
            panel.targetTexture = target;
            var warnings = new UiWarnings();
            try
            {
                HudView.SafeAreaOverride = phone.Safe();
                yield return Settle();
                Check(hud.Root, phone, "testbed, readings");
                yield return Tap(hud.Root.Q<VisualElement>("picture"));
                Check(hud.Root, phone, "testbed");
                warnings.AssertNone("testbed");
            }
            finally
            {
                warnings.Dispose();
                HudView.SafeAreaOverride = null;
                panel.targetTexture = previous;
                target.Release();
                Object.Destroy(target);
            }
        }

        [UnityTest]
        public IEnumerator TheArenaFieldsThePairTheMenuPicked()
        {
            yield return SceneManager.LoadSceneAsync("Arena", LoadSceneMode.Single);
            yield return null;
            MatchSetup setup = Object.FindAnyObjectByType<MatchSetup>();
            if (setup == null || setup.boxers.Length < 2) Assert.Ignore("fewer than two trained boxers in the arena");

            // The default pair, the other way round.
            string red = setup.defaultBlue, blue = setup.defaultRed;
            MatchSelection.Choose(red, blue);
            try
            {
                yield return SceneManager.LoadSceneAsync("Arena", LoadSceneMode.Single);
                yield return UntilTheBell();
                for (int i = 0; i < 120; i++) yield return new WaitForFixedUpdate();

                Bout bout = Bout.Instance;
                Assert.IsNotNull(bout, "the Arena scene has no Bout");
                Assert.AreEqual(red, bout.red.boxer.Config.name, "the red corner is not the boxer that was picked for it");
                Assert.AreEqual(blue, bout.blue.boxer.Config.name, "the blue corner is not the boxer that was picked for it");
                Assert.IsTrue(bout.red.boxer.Bound && bout.blue.boxer.Bound, "the picked pair is not running on MuJoCo");
                foreach (Fighter f in new[] { bout.red, bout.blue })
                    Assert.IsFalse(f.IsDown, f.displayName + " fell over without being hit");
            }
            finally
            {
                MatchSelection.Clear();
            }
        }

        [UnityTest]
        public IEnumerator FightersStayOnTheirFeetThroughTheIntro()
        {
            yield return SceneManager.LoadSceneAsync("Arena", LoadSceneMode.Single);
            // Through the walk-on, in which nothing is simulated, and into the introduction, in which it is.
            Bout bout = null;
            for (int i = 0; i < 1200 && (bout == null || bout.Phase == BoutPhase.WalkOn); i++) { yield return null; bout = Bout.Instance; }
            for (int i = 0; i < 120; i++) yield return new WaitForFixedUpdate();

            Assert.IsNotNull(bout, "the Arena scene has no Bout");
            Assert.AreNotEqual(BoutPhase.WalkOn, bout.Phase, "the walk-on never ended");
            foreach (Fighter f in new[] { bout.red, bout.blue })
            {
                Assert.IsFalse(f.IsDown, f.displayName + " fell over without being hit");
                Assert.Greater(f.PelvisHeight, f.standingPelvisHeight * 0.75f, f.displayName + " is not standing");
                Assert.Less(f.PelvisHeight, f.standingPelvisHeight * 1.25f, f.displayName + " has left the canvas");
            }
        }

        [UnityTest]
        public IEnumerator AKnockdownIsFeltOnTheCanvasAndInThePicture()
        {
            yield return SceneManager.LoadSceneAsync("Arena", LoadSceneMode.Single);
            yield return UntilTheBell();
            var vfx = Object.FindAnyObjectByType<PoBox.Fx.ImpactVfx>();
            Assert.IsTrue(vfx.HasPulse, "the arena has no hit-pulse volume");
            Assert.IsNotNull(vfx.steam, "the arena has no steam");

            float hardest = 0f, pulse = 0f;
            System.Action<Vector3, float> heard = (at, impulse) => hardest = Mathf.Max(hardest, impulse);
            SimBus.FloorImpact += heard;
            Bout.Instance.red.ForceDown();
            for (int i = 0; i < 180; i++) { yield return null; pulse = Mathf.Max(pulse, vfx.Pulse); }
            SimBus.FloorImpact -= heard;

            // What MuJoCo put through the floor into a body of 80 kg coming down: tens of newton-seconds.
            Assert.Greater(hardest, 10f, "a boxer went down and MuJoCo's contacts reported no landing");
            Assert.Greater(pulse, 0.5f, "a knockdown did not pulse the picture");
        }

        /// <summary>
        /// Menu, fight, a count cut short by RESTART, the result, the next bout, back to the menu, and in again,
        /// with every button that changes scene tapped twice. Any error or exception in the console fails it.
        /// </summary>
        [UnityTest]
        public IEnumerator TheLoopFromTheMenuToAResultAndBackLeavesNothingBehind()
        {
            if (!MatchSelection.HasMenu) Assert.Ignore("no trained boxers, so the build has no menu scene");
            int arenas = 0;
            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> counted = (s, m) => { if (s.name == MatchSelection.ArenaScene) arenas++; };
            SceneManager.sceneLoaded += counted;
            try
            {
                yield return SceneManager.LoadSceneAsync(MatchSelection.MenuScene, LoadSceneMode.Single);
                yield return null;
                MenuView menu = Object.FindAnyObjectByType<MenuView>();
                Assert.IsNotNull(menu, "the Menu scene has no MenuView");
                menu.Fight();
                menu.Fight();
                yield return UntilTheBell();
                yield return Settle();
                Assert.AreEqual(1, arenas, "a double tap on FIGHT loaded the arena more than once");

                Bout bout = Bout.Instance;
                HudView hud = Object.FindAnyObjectByType<HudView>();
                Assert.IsNotNull(bout, "FIGHT did not reach the arena");
                Assert.AreEqual(BoutPhase.Fight, bout.Phase, "the first bell never rang");
                bout.league = null;             // a half-second round is not a result for the owner's ladder
                bout.rounds = 1;
                bout.roundSeconds = 0.5f;

                bout.red.ForceDown();
                yield return Settle();
                Assert.AreEqual(BoutPhase.Count, bout.Phase, "a knockdown did not start a count");
                bout.Restart();
                yield return Settle();
                Assert.AreEqual(BoutPhase.Intro, bout.Phase, "RESTART during a count did not start the bout again");
                Assert.IsNull(bout.Downed, "RESTART left the referee counting");
                Assert.IsFalse(bout.red.IsDown, "RESTART left a boxer on the canvas");

                yield return Until(() => bout.Phase == BoutPhase.Results);
                yield return Settle();
                Assert.AreEqual(BoutPhase.Results, bout.Phase, "the bout never reached a result");
                Assert.IsTrue(hud.ResultsOpen, "the result card is not up");
                hud.OpenMenu(0);
                Assert.IsFalse(hud.ResultsOpen, "the menu and the result card are both up");
                hud.CloseMenu();
                Assert.IsTrue(hud.ResultsOpen, "closing the menu did not bring the result card back");

                int number = bout.BoutNumber;
                bout.NewBout();
                yield return Settle();
                Assert.AreEqual(number + 1, bout.BoutNumber);
                Assert.IsFalse(hud.ResultsOpen, "the result card stayed up into the next bout");

                bout.userTimeScale = 2f;
                yield return null;
                hud.ToBoxers();
                hud.ToBoxers();
                yield return Until(() => Object.FindAnyObjectByType<MenuView>() != null);
                yield return Settle();
                Assert.IsNull(Bout.Instance, "the bout outlived its scene");
                Assert.IsFalse(Bout.SimRunning, "the simulation is still marked running on the menu");
                Assert.AreEqual(1f, Time.timeScale, "the menu was left running at the fight's speed");

                Object.FindAnyObjectByType<MenuView>().Fight();
                yield return UntilTheBell();
                Assert.AreEqual(2, arenas, "the second FIGHT did not load the arena exactly once");
                Assert.IsNotNull(Bout.Instance, "the second FIGHT did not reach the arena");
                Assert.AreEqual(1, Bout.Instance.BoutNumber);
                Assert.AreEqual(BoutPhase.Fight, Bout.Instance.Phase, "the second visit never reached the bell");
            }
            finally
            {
                SceneManager.sceneLoaded -= counted;
                MatchSelection.Clear();
            }
        }

        /// <summary>Waits for something, for twenty seconds at most.</summary>
        static IEnumerator Until(System.Func<bool> done)
        {
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline && !done()) yield return null;
        }

        static IEnumerator Settle()
        {
            for (int i = 0; i < 4; i++) yield return null;
        }

        /// <summary>A finger down and up on the middle of an element, then the frames the panel takes to answer.</summary>
        static IEnumerator Tap(VisualElement target)
        {
            Assert.IsNotNull(target, "nothing to tap");
            Vector2 at = target.worldBound.center;
            using (PointerDownEvent down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, mousePosition = at, button = 0, clickCount = 1 }))
                target.SendEvent(down);
            using (PointerUpEvent up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, mousePosition = at, button = 0, clickCount = 1 }))
                target.SendEvent(up);
            yield return Settle();
        }

        /// <summary>Every warning UI Toolkit or the game's interface writes while it is listening.</summary>
        sealed class UiWarnings : System.IDisposable
        {
            readonly List<string> _seen = new List<string>();
            public UiWarnings() => Application.logMessageReceived += Heard;
            public void Dispose() => Application.logMessageReceived -= Heard;

            void Heard(string text, string stack, LogType type)
            {
                if (type != LogType.Warning) return;
                if (text.Contains("UIElements") || text.Contains("UI Toolkit") || text.Contains(".uss") || text.Contains(".uxml")
                    || (stack != null && (stack.Contains("UnityEngine.UIElements") || stack.Contains("PoBox.UI"))))
                    _seen.Add(text);
            }

            public void AssertNone(string what) => Assert.IsEmpty(_seen, $"{what} wrote UI warnings:\n{string.Join("\n", _seen)}");
        }

        /// <summary>Waits out the walk-on and the introduction: until the first bell, or twenty seconds.</summary>
        static IEnumerator UntilTheBell()
        {
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline && (Bout.Instance == null || Bout.Instance.Phase != BoutPhase.Fight)) yield return null;
        }

        [Test]
        public void AButtonsWordIsOneShortWord()
        {
            foreach (string ok in new[] { "NEXT", "NEXT 7", "BOXERS ···", "SURE?", "READINGS", "SLOW-MO", "X-RAY", "" })
                Assert.IsFalse(LayoutAudit.Wordy(ok), $"'{ok}' was called wordy");
            foreach (string bad in new[] { "NEXT BOUT", "CHANGE BOXERS", "FREE-FOR-ALL", "KNOCKOUTS" })
                Assert.IsTrue(LayoutAudit.Wordy(bad), $"'{bad}' passed as one short word");
        }

        [Test]
        public void ThreeJudgesCanDisagree()
        {
            // Red lands a few heavy head shots, blue lands many light body shots and stops some of red's on its
            // gloves: the judge of power should have red, the judge of volume blue.
            var judges = new Judges { impulseFloor = 2.2f };
            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < 4; i++) judges.OnHit(0, new HitEvent { impulse = 19f, zone = PartKind.Head, clean = true });
                for (int i = 0; i < 14; i++) judges.OnHit(1, new HitEvent { impulse = 6f, zone = PartKind.Torso, clean = true });
                for (int i = 0; i < 20; i++) { judges.OnThrown(0); judges.OnThrown(1); }
                judges.CloseRound();
            }
            Assert.AreEqual(3, judges.RoundsScored);
            Assert.Greater(judges.total[0, 0], judges.total[0, 1], "the judge of power did not give it to the harder hitter");
            Assert.Greater(judges.total[1, 1], judges.total[1, 0], "the judge of volume did not give it to the busier fighter");
            string verdict = judges.Verdict(out int winner);
            StringAssert.Contains("SPLIT", verdict, "two judges one way and one the other is a split decision");
            Assert.AreNotEqual(-1, winner);
        }

        [Test]
        public void AKnockdownCostsAPoint()
        {
            var judges = new Judges { impulseFloor = 2.2f };
            for (int i = 0; i < 6; i++) judges.OnHit(0, new HitEvent { impulse = 12f, zone = PartKind.Head, clean = true });
            judges.OnKnockdown(1);
            judges.CloseRound();
            for (int j = 0; j < Judges.Count; j++)
            {
                Assert.AreEqual(10, judges.total[j, 0], Judges.Names[j] + " did not give the round to the fighter who scored the knockdown");
                Assert.AreEqual(8, judges.total[j, 1], Judges.Names[j] + " did not take a second point for the knockdown");
            }
        }

        [Test]
        public void RingGeneralshipOnlyBreaksALevelRound()
        {
            var judges = new Judges { impulseFloor = 2.2f };
            var hit = new HitEvent { impulse = 8f, zone = PartKind.Head, clean = true };
            // An even round in which red held the centre: all three give it to red.
            for (int i = 0; i < 5; i++) { judges.OnHit(0, hit); judges.OnHit(1, hit); }
            judges.OnCentre(0, 20f);
            judges.OnCentre(1, 5f);
            judges.CloseRound();
            Assert.AreEqual(0.8f, judges.LastCentreShare, 1e-4f);
            for (int j = 0; j < Judges.Count; j++)
                Assert.AreEqual("10-9", judges.Card(j), Judges.Names[j] + " did not break a level round for the fighter who held the centre");

            // A round blue won on punches stays blue's however long red held the centre.
            judges.OnHit(0, hit);
            for (int i = 0; i < 6; i++) judges.OnHit(1, hit);
            judges.OnCentre(0, 30f);
            judges.CloseRound();
            for (int j = 0; j < Judges.Count; j++)
                Assert.AreEqual("19-19", judges.Card(j), Judges.Names[j] + " let the centre outweigh the punches");
        }

        /// <summary>Each of the settings' three tabs, audited, ending back on the first.</summary>
        static IEnumerator CheckSettingsTabs(SettingsPage settings, VisualElement root, Phone phone, string what)
        {
            Assert.IsNotNull(settings, what + ": no settings page");
            for (int tab = 0; tab < 3; tab++)
            {
                settings.ShowTab(tab);
                yield return Settle();
                Check(root, phone, $"{what} tab {tab}");
            }
            settings.ShowTab(0);
        }

        static void Check(VisualElement root, Phone phone, string what)
        {
            LayoutAudit.Report report = LayoutAudit.Check(root);
            Assert.IsTrue(report.Passed, $"{what} at {phone.x}x{phone.y}:\n{report.Summary()}");
        }
    }
}
