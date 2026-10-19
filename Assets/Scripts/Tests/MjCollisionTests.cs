using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using PoBox.Mj;
using PoBox.Sim;

namespace PoBox.Tests
{
    /// <summary>
    /// Boxers do not go through each other: every shape of one against every shape of the other, by MuJoCo's own
    /// signed distance (<see cref="MjRing.MeasureOverlap"/>), over 2500 control steps (50 s) of fighting at four times the speed.
    ///
    /// Contacts are soft (MuJoCo's default solref 0.02 s, as the trainer has them), so a glove at 9 m/s, moving 4.5 cm a
    /// physics step, sinks a few centimetres into what it hits before it is pushed out: measured 4.7 to 6.3 cm at the
    /// worst for four pairs (7.1 cm once, feet trodden on), deeper than 3 cm in about 1% of control steps, nearly all of it a
    /// forearm into a trunk.
    /// Passing through is a body most of its thickness inside another (a trunk is 20 cm across), and that is what the
    /// limits here are set to catch. A boxer down passes through the one standing on purpose (envs/getup.py trains the
    /// get-up that way), so those pairs are not held to it.
    /// </summary>
    public class MjCollisionTests
    {
        const double OnTheirFeet = 0.10, OneDown = 0.20;     // metres: half a trunk, and a trunk
        const int Steps = 2500;

        [UnityTest]
        public IEnumerator TwoBoxersDoNotGoThroughEachOther()
        {
            yield return SceneManager.LoadSceneAsync(MatchSelection.ArenaScene, LoadSceneMode.Single);
            for (float t = 0f; (Bout.Instance == null || Bout.Instance.Phase != BoutPhase.Fight) && t < 30f; t += Time.unscaledDeltaTime) yield return null;
            Assert.IsNotNull(Bout.Instance, "the Arena scene has no Bout");
            yield return Measure(() => { Bout.Instance.userTimeScale = 4f; Bout.Instance.resultsSeconds = 0.5f; });
            Assert.Less(MjRing.DeepestOverlap[0], OnTheirFeet, "two boxers on their feet went into each other: " + MjRing.DeepestWhere[0]);
            Assert.Less(MjRing.DeepestOverlap[1], OnTheirFeet, "the boxer held over a count was put inside the other: " + MjRing.DeepestWhere[1]);
            Assert.Less(MjRing.Over3cm, MjRing.OverlapSteps * 0.03, $"{MjRing.Over3cm} of {MjRing.OverlapSteps} control steps had two boxers 3 cm inside each other");
        }

        [UnityTest]
        public IEnumerator EightBoxersDoNotGoThroughEachOther()
        {
            if (!Application.CanStreamedLevelBeLoaded(MatchSelection.MeleeScene)) Assert.Ignore("the build has no Melee scene");
            yield return SceneManager.LoadSceneAsync(MatchSelection.MeleeScene, LoadSceneMode.Single);
            for (float t = 0f; (FreeForAll.Instance == null || FreeForAll.Instance.Phase != FreeForAll.Stage.Fight) && t < 30f; t += Time.unscaledDeltaTime) yield return null;
            Assert.IsNotNull(FreeForAll.Instance, "the Melee scene has no FreeForAll");
            yield return Measure(() => Time.timeScale = 4f);
            Assert.Less(MjRing.DeepestOverlap[0], OnTheirFeet, "two boxers on their feet went into each other: " + MjRing.DeepestWhere[0]);
            Assert.Less(MjRing.DeepestOverlap[3], OneDown, "a boxer went through one on the canvas: " + MjRing.DeepestWhere[3]);
        }

        static IEnumerator Measure(System.Action fast)
        {
            MjRing.ResetOverlap();
            MjRing.MeasureOverlap = true;
            try
            {
                fast();
                // Counted in control steps: a bout over inside the distance stops the physics on its result card.
                float until = Time.realtimeSinceStartup + 240f;
                while (MjRing.OverlapSteps < Steps && Time.realtimeSinceStartup < until) yield return null;
                Assert.GreaterOrEqual(MjRing.OverlapSteps, Steps, "the physics did not run long enough to judge");
            }
            finally
            {
                MjRing.MeasureOverlap = false;
                Time.timeScale = 1f;
            }
            Debug.Log($"overlap over {MjRing.OverlapSteps} control steps: on their feet {MjRing.DeepestOverlap[0] * 100:0.0} cm ({MjRing.DeepestWhere[0]}), " +
                      $"held {MjRing.DeepestOverlap[1] * 100:0.0} cm, passing {MjRing.DeepestOverlap[2] * 100:0.0} cm, one down {MjRing.DeepestOverlap[3] * 100:0.0} cm ({MjRing.DeepestWhere[3]}); " +
                      $">1 cm {MjRing.Over1cm}, >3 cm {MjRing.Over3cm}");
        }
    }
}
