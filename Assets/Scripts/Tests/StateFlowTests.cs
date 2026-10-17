using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using PoBox.League;
using PoBox.Mj;
using PoBox.Sim;
using PoBox.UI;

namespace PoBox.Tests
{
    /// <summary>
    /// The way through the game, pressed as a player presses it: the menu, FIGHT, the arena's result card, MENU, the
    /// menu again, ALL, the free-for-all's result, AGAIN, its result again, MENU, and the menu once more. Each button is
    /// pressed through the panel (a submit, as a click is), so one that is hidden, switched off or missing is a dead
    /// end and fails the test, as does any error, exception or warning in the console (bar the Inference Engine's on a
    /// cold start). Bouts are half a second at four times the speed, so the whole way takes about a minute. A second
    /// test takes the ways out of the middle of things: the back button, the cup, NEXT, a replay cut short by the
    /// menu's NEXT, leaving during a count, and leaving the free-for-all in the middle of its bullet time.
    /// </summary>
    public class StateFlowTests
    {
        const float Budget = 90f;

        [UnityTest]
        public IEnumerator MenuArenaResultMenuMeleeAgainMenuHasNoDeadEnds()
        {
            if (!MatchSelection.HasMenu || !Application.CanStreamedLevelBeLoaded(MatchSelection.MeleeScene))
                Assert.Ignore("the build has no menu or no free-for-all");
            float started = Time.realtimeSinceStartup;
            var warnings = new Warnings();
            try
            {
                yield return SceneManager.LoadSceneAsync(MatchSelection.MenuScene, LoadSceneMode.Single);
                yield return Settle();
                MenuView menu = Object.FindAnyObjectByType<MenuView>();
                Assert.IsNotNull(menu, "the Menu scene has no MenuView");

                // FIGHT, to the result card.
                yield return Press(menu.Root, "fight");
                yield return Until(() => Bout.Instance != null && Bout.Instance.Phase == BoutPhase.Fight, 30f, "FIGHT never reached the first bell");
                Bout bout = Bout.Instance;
                bout.league = null;             // half a second is not a result for the owner's ladder
                bout.rounds = 1;
                bout.roundSeconds = 0.5f;
                bout.userTimeScale = 4f;
                HudView hud = Object.FindAnyObjectByType<HudView>();
                yield return Until(() => hud.ResultsOpen, 20f, "the bout never put its result card up");

                // MENU on the card, back to the first screen.
                yield return Press(hud.Root, "res-boxers");
                yield return Until(() => Object.FindAnyObjectByType<MenuView>() != null, 20f, "MENU on the arena's result did not reach the menu");
                Assert.AreEqual(1f, Time.timeScale, "the menu was left running at the fight's speed");
                yield return Settle();
                menu = Object.FindAnyObjectByType<MenuView>();

                // ALL, to the free-for-all's result.
                yield return Press(menu.Root, "melee");
                yield return Until(() => FreeForAll.Instance != null && FreeForAll.Instance.Phase != FreeForAll.Stage.Waiting, 30f, "ALL never reached the free-for-all");
                FreeForAll rules = FreeForAll.Instance;
                rules.rounds = 1;
                rules.roundSeconds = 0.5f;
                rules.introSeconds = 0.5f;      // unscaled, and a background editor runs at a few frames a second
                rules.Restart();
                Time.timeScale = 4f;
                MeleeView melee = Object.FindAnyObjectByType<MeleeView>();
                yield return Until(() => melee.ResultOpen, 30f, "the free-for-all never put its result up");

                // AGAIN: the same free-for-all, to its result again.
                yield return Press(melee.Root, "again");
                Assert.AreNotEqual(FreeForAll.Stage.Over, rules.Phase, "AGAIN did not start the free-for-all again");
                yield return Until(() => melee.ResultOpen, 30f, "the free-for-all fought again never put its result up");

                // MENU on the result, back to the first screen.
                yield return Press(melee.Root, "leave");
                yield return Until(() => Object.FindAnyObjectByType<MenuView>() != null, 20f, "MENU on the free-for-all's result did not reach the menu");
                yield return Settle();
                Assert.AreEqual(1f, Time.timeScale, "the menu was left running at the free-for-all's speed");

                // Errors and exceptions anywhere on the way already fail the test (the test framework's LogAssert does
                // that by itself); warnings are caught here.
                warnings.AssertNone();
                Assert.Less(Time.realtimeSinceStartup - started, Budget, "the way through took too long");
            }
            finally
            {
                warnings.Dispose();
                Time.timeScale = 1f;
                MatchSelection.Clear();
            }
        }

        [UnityTest]
        public IEnumerator BackButtonCupNextReplayAndLeavingMidCountLeaveNothingBehind()
        {
            if (!MatchSelection.HasMenu || !Application.CanStreamedLevelBeLoaded(MatchSelection.MeleeScene))
                Assert.Ignore("the build has no menu or no free-for-all");
            // The cup is the owner's, on disk: kept aside and put back.
            string cupFile = Path.Combine(Application.persistentDataPath, "career.json");
            string cupSaved = File.Exists(cupFile) ? File.ReadAllText(cupFile) : null;
            var warnings = new Warnings();
            try
            {
                yield return SceneManager.LoadSceneAsync(MatchSelection.MenuScene, LoadSceneMode.Single);
                yield return Settle();
                MenuView menu = Object.FindAnyObjectByType<MenuView>();

                // Back puts away the settings, then the cup.
                menu.ToggleSettings();
                menu.Back();
                Assert.IsFalse(menu.SettingsOpen, "back did not put the settings away");
                menu.ToggleCup();
                menu.Back();
                Assert.IsFalse(menu.CupOpen, "back did not put the cup away");

                // CUP, NEW CUP, its FIGHT: the bout counts for the cup and says so on the card.
                menu.ToggleCup();
                yield return Press(menu.Root, "cup-new");
                yield return Press(menu.Root, "cup-fight");
                yield return Until(() => Bout.Instance != null && Bout.Instance.Phase == BoutPhase.Fight, 30f, "the cup's FIGHT never reached the first bell");
                Assert.IsTrue(Career.Live, "the cup's bout is not being counted for the cup");
                Bout bout = Bout.Instance;
                HudView hud = Object.FindAnyObjectByType<HudView>();
                Quick(bout);
                yield return Until(() => hud.ResultsOpen, 20f, "the cup's bout never put its result card up");
                Assert.IsFalse(Career.Live, "the cup's result was not taken");
                StringAssert.Contains("CUP", hud.Root.Q<Label>("res-extra").text, "the card does not say what the bout did in the cup");

                // NEXT on the card: the next bout, the card gone.
                int number = bout.BoutNumber;
                yield return Press(hud.Root, "res-next");
                Assert.AreEqual(number + 1, bout.BoutNumber, "NEXT did not start the next bout");
                Assert.IsFalse(hud.ResultsOpen, "the card stayed up into the next bout");

                // REPLAY, then the menu's NEXT while it plays: the replay is stopped and the next bout is on the live state.
                Quick(bout);
                yield return Until(() => hud.ResultsOpen, 30f, "the second bout never put its result card up");
                bout.autoAdvance = false;       // the card's own clock would start the next bout under the replay
                yield return Press(hud.Root, "res-replay");
                yield return Until(() => MjReplay.Playing, 5f, "REPLAY did not play");
                Assert.IsFalse(hud.ResultsOpen, "the card is up over the replay");
                hud.OpenMenu(0);
                yield return Press(hud.Root, "m-next");
                Assert.IsFalse(MjReplay.Playing, "the menu's NEXT left the replay playing");
                Assert.AreEqual(number + 2, bout.BoutNumber, "the menu's NEXT did not start the next bout");
                bout.autoAdvance = true;
                bout.roundSeconds = 60f;        // a round long enough to be knocked down in
                yield return Until(() => bout.Phase == BoutPhase.Fight, 30f, "the bout after the replay never reached the bell");
                foreach (Fighter f in new[] { bout.red, bout.blue })
                    Assert.IsFalse(f.IsDown, f.displayName + " started the bout after the replay on the canvas");

                // A knockdown, then back with nothing open: straight to the menu, nothing left counting or running.
                bout.red.ForceDown();
                yield return Settle();
                Assert.AreEqual(BoutPhase.Count, bout.Phase, "a knockdown did not start a count");
                hud.Back();
                yield return Until(() => Object.FindAnyObjectByType<MenuView>() != null, 20f, "back in the middle of a count did not reach the menu");
                yield return Settle();
                Assert.IsNull(Bout.Instance, "the bout outlived its scene");
                Assert.IsFalse(Bout.SimRunning, "the simulation is still marked running on the menu");
                Assert.AreEqual(1f, Time.timeScale, "the menu was left running at the fight's speed");

                // The free-for-all, left in the middle of its bullet time with the settings open: back closes the
                // settings, back again reaches the menu, and the menu runs at the right speed.
                menu = Object.FindAnyObjectByType<MenuView>();
                yield return Press(menu.Root, "melee");
                yield return Until(() => FreeForAll.Instance != null && FreeForAll.Instance.Phase == FreeForAll.Stage.Fight, 60f, "ALL never reached the fight");
                MeleeView melee = Object.FindAnyObjectByType<MeleeView>();
                Time.timeScale = 0.3f;          // what the bullet time sets in the free-for-all, where no Bout owns the clock
                melee.ToggleSettings();
                melee.Back();
                Assert.IsFalse(melee.SettingsOpen, "back did not put the free-for-all's settings away");
                melee.Back();
                yield return Until(() => Object.FindAnyObjectByType<MenuView>() != null, 20f, "back in the free-for-all did not reach the menu");
                yield return Settle();
                Assert.AreEqual(1f, Time.timeScale, "the menu was left in the free-for-all's bullet time");
                Assert.IsFalse(Bout.SimRunning, "the free-for-all is still marked running on the menu");
                warnings.AssertNone();
            }
            finally
            {
                warnings.Dispose();
                Time.timeScale = 1f;
                MatchSelection.Clear();
                Career.Live = false;
                if (cupSaved != null) File.WriteAllText(cupFile, cupSaved);
                else if (File.Exists(cupFile)) File.Delete(cupFile);
            }
        }

        /// <summary>One half-second round at four times the speed, off the owner's ladder.</summary>
        static void Quick(Bout bout)
        {
            bout.league = null;
            bout.rounds = 1;
            bout.roundSeconds = 0.5f;
            bout.userTimeScale = 4f;
        }

        /// <summary>Every warning written while it listens, bar the Inference Engine's job compiler on a cold start.</summary>
        sealed class Warnings : System.IDisposable
        {
            readonly List<string> _seen = new List<string>();
            public Warnings() => Application.logMessageReceived += Heard;
            public void Dispose() => Application.logMessageReceived -= Heard;

            void Heard(string text, string stack, LogType type)
            {
                if (type != LogType.Warning) return;
                // Burst asked to compile one of the Inference Engine's generic jobs; it comes with no stack.
                if (text.Contains("Unity.InferenceEngine") && text.Contains("not a known Burst entry point")) return;
                _seen.Add(text);
            }

            public void AssertNone() => Assert.IsEmpty(_seen, "warnings on the way:\n" + string.Join("\n", _seen));
        }

        /// <summary>Presses a button through the panel, failing if it is missing, not drawn or switched off: a dead end.</summary>
        static IEnumerator Press(VisualElement root, string name)
        {
            Assert.IsNotNull(root, $"no interface to press {name} on");
            yield return Settle();
            Button button = root.Q<Button>(name);
            Assert.IsNotNull(button, $"there is no {name}: a dead end");
            for (VisualElement e = button; e != null; e = e.hierarchy.parent)
                Assert.AreNotEqual(DisplayStyle.None, e.resolvedStyle.display, $"{name} is not on the screen ({e.name} is hidden): a dead end");
            Assert.IsTrue(button.enabledInHierarchy, $"{name} is switched off: a dead end");
            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button;
                button.SendEvent(submit);
            }
            yield return Settle();
        }

        static IEnumerator Until(System.Func<bool> done, float seconds, string failure)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline && !done()) yield return null;
            Assert.IsTrue(done(), failure);
        }

        static IEnumerator Settle()
        {
            for (int i = 0; i < 4; i++) yield return null;
        }
    }
}
