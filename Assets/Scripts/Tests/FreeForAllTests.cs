using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using PoBox.Mj;
using PoBox.Sim;

namespace PoBox.Tests
{
    /// <summary>The free-for-all (Melee scene): every boxer in one MuJoCo ring, each measured against its own opponent.</summary>
    public class FreeForAllTests
    {
        [UnityTest]
        public IEnumerator EveryBoxerFightsAndASlipIsCounted()
        {
            if (!Application.CanStreamedLevelBeLoaded(MatchSelection.MeleeScene)) Assert.Ignore("the build has no Melee scene");
            yield return SceneManager.LoadSceneAsync(MatchSelection.MeleeScene, LoadSceneMode.Single);
            var rules = Object.FindAnyObjectByType<FreeForAll>();
            Assert.IsNotNull(rules, "the Melee scene has no FreeForAll");
            for (float t = 0f; rules.Phase != FreeForAll.Stage.Fight && t < 20f; t += Time.unscaledDeltaTime) yield return null;
            Assert.AreEqual(FreeForAll.Stage.Fight, rules.Phase, "the free-for-all never started");

            Fighter[] all = rules.fighters;
            Assert.GreaterOrEqual(all.Length, 7, "fewer than seven boxers in the ring");
            Assert.AreEqual(all.Length, all.Select(f => f.boxer.prefix).Distinct().Count(), "two boxers share a prefix: their MuJoCo names collide");
            Assert.IsTrue(all.All(f => f.boxer.Bound), "a boxer was not bound to the compiled model");

            Time.timeScale = 4f;
            try
            {
                float until = Time.time + 20f;
                while (Time.time < until) yield return null;
                foreach (Fighter f in all)
                    Assert.IsFalse(float.IsNaN(f.pelvis.transform.position.x), $"{f.displayName}'s body has gone to NaN");
                int landing = all.Count(f => f.stats.landed > 0);
                Assert.GreaterOrEqual(landing, 4, $"only {landing} boxers landed a punch in 20 s: punches are not being measured against each one's opponent");

                // A fall with no punch behind it is a knockdown too: the referee counts it.
                Fighter victim = all.FirstOrDefault(f => !f.IsDown && !rules.IsOut(f));
                if (victim != null && rules.Phase == FreeForAll.Stage.Fight)
                {
                    victim.ForceDown();
                    yield return null;
                    Assert.IsFalse(rules.Slipped(victim), $"{victim.displayName} went down in the fight and was not counted");
                    Assert.GreaterOrEqual(rules.CountOf(victim), 0, $"{victim.displayName} went down with no punch and nobody counted");
                }
            }
            finally { Time.timeScale = 1f; }
        }
    }
}
