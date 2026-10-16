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
    /// The way through the game, pressed as a player presses it: the menu, FIGHT, the arena's result card, MENU, the
    /// menu again, ALL, the free-for-all's result, AGAIN, its result again, MENU, and the menu once more. Each button is
    /// pressed through the panel (a submit, as a click is), so one that is hidden, switched off or missing is a dead
    /// end and fails the test, as does any error or exception in the console. Bouts are half a second at four times
    /// the speed, so the whole way takes about a minute.
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
                MeleeHud melee = Object.FindAnyObjectByType<MeleeHud>();
                yield return Until(() => melee.ResultOpen, 30f, "the free-for-all never put its result up");

                // AGAIN: the same free-for-all, to its result again.
                yield return Press(melee.Root, "again");
                Assert.AreNotEqual(FreeForAll.Stage.Over, rules.Phase, "AGAIN did not start the free-for-all again");
                yield return Until(() => melee.ResultOpen, 30f, "the free-for-all fought again never put its result up");

                // MENU on the result, back to the first screen.
                yield return Press(melee.Root, "leave");
                yield return Until(() => Object.FindAnyObjectByType<MenuView>() != null, 20f, "MENU on the free-for-all's result did not reach the menu");
                yield return Settle();

                // Errors and exceptions anywhere on the way already fail the test (the test framework's LogAssert does
                // that by itself); warnings do not, as the Inference Engine's job compiler writes some on a cold start.
                Assert.Less(Time.realtimeSinceStartup - started, Budget, "the way through took too long");
            }
            finally
            {
                Time.timeScale = 1f;
                MatchSelection.Clear();
            }
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
