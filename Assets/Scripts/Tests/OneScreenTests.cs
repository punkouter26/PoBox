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
    /// well as closed.
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
                Check(hud, phone, "fight");

                for (int tab = 0; tab < 3; tab++)
                {
                    hud.OpenMenu(tab);
                    yield return Settle();
                    Check(hud, phone, "menu tab " + tab);
                }
                hud.CloseMenu();

                hud.ToggleDebug();
                yield return Settle();
                Check(hud, phone, "debug panel");
                hud.ToggleDebug();

                for (int page = 0; page < 3; page++)
                {
                    hud.SetStripPage(page);
                    yield return Settle();
                    Check(hud, phone, "graph page " + page);
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
        public IEnumerator FightersStayOnTheirFeetThroughTheIntro()
        {
            yield return SceneManager.LoadSceneAsync("Arena", LoadSceneMode.Single);
            for (int i = 0; i < 120; i++) yield return new WaitForFixedUpdate();

            Bout bout = Bout.Instance;
            Assert.IsNotNull(bout, "the Arena scene has no Bout");
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

        static void Check(HudView hud, Vector2Int phone, string what)
        {
            LayoutAudit.Report report = LayoutAudit.Check(hud.Root);
            Assert.IsTrue(report.Passed, $"{what} at {phone.x}x{phone.y}:\n{report.Summary()}");
        }
    }
}
