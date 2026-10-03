using System.Collections;
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
    /// any label is cut off, or one of the five frame anchors has left its corner. Run for a small 9:16
    /// phone, the 1080 x 1920 reference and a tall 9:20 phone, with the menu and the debug panel open as
    /// well as closed. The first screen, where the boxers are chosen, gets the same treatment, and the
    /// arena is checked to field the pair that was picked.
    /// </summary>
    public class OneScreenTests
    {
        static readonly Vector2Int[] Phones =
        {
            new Vector2Int(720, 1280),
            new Vector2Int(1080, 1920),
            new Vector2Int(1080, 2400),
        };

        [UnityTest]
        public IEnumerator HudFitsOneScreenWithNoScrolling([ValueSource(nameof(Phones))] Vector2Int phone)
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
            try
            {
                // The fight itself, then with each thing that can open over it.
                yield return Settle();
                Check(hud.Root, phone, "fight");

                for (int tab = 0; tab < 3; tab++)
                {
                    hud.OpenMenu(tab);
                    yield return Settle();
                    Check(hud.Root, phone, "menu tab " + tab);
                }
                hud.CloseMenu();

                hud.ToggleDebug();
                yield return Settle();
                Check(hud.Root, phone, "debug panel");
                hud.ToggleDebug();

                for (int page = 0; page < 3; page++)
                {
                    hud.SetStripPage(page);
                    yield return Settle();
                    Check(hud.Root, phone, "graph page " + page);
                }
            }
            finally
            {
                panel.targetTexture = previous;
                target.Release();
                Object.Destroy(target);
            }
        }

        [UnityTest]
        public IEnumerator BoxerMenuFitsOneScreenAndKeepsTheCornersDifferent([ValueSource(nameof(Phones))] Vector2Int phone)
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
            try
            {
                yield return Settle();
                Check(menu.Root, phone, "boxer menu", LayoutAudit.MenuAnchors);

                if (menu.boxers.Length > 1)
                {
                    Assert.AreNotEqual(menu.Red, menu.Blue, "the menu opened with the same boxer in both corners");
                    int red = menu.Red, blue = menu.Blue;
                    menu.Pick(0, blue);
                    Assert.AreEqual(blue, menu.Red, "red did not take the boxer that was tapped");
                    Assert.AreEqual(red, menu.Blue, "tapping the other corner's boxer did not swap the two");
                    yield return Settle();
                    Check(menu.Root, phone, "boxer menu, corners swapped", LayoutAudit.MenuAnchors);
                }
            }
            finally
            {
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
                Assert.AreEqual(red, bout.red.boxer.Cfg.name, "the red corner is not the boxer that was picked for it");
                Assert.AreEqual(blue, bout.blue.boxer.Cfg.name, "the blue corner is not the boxer that was picked for it");
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

        static IEnumerator Settle()
        {
            for (int i = 0; i < 4; i++) yield return null;
        }

        /// <summary>Waits out the walk-on and the introduction: until the first bell, or twenty seconds.</summary>
        static IEnumerator UntilTheBell()
        {
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline && (Bout.Instance == null || Bout.Instance.Phase != BoutPhase.Fight)) yield return null;
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

        static void Check(VisualElement root, Vector2Int phone, string what, string[] anchors = null)
        {
            LayoutAudit.Report report = LayoutAudit.Check(root, anchors);
            Assert.IsTrue(report.Passed, $"{what} at {phone.x}x{phone.y}:\n{report.Summary()}");
        }
    }
}
