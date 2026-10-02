using System;
using System.Collections;
using System.IO;
using System.Text;
using NUnit.Framework;
using PoBox.Mj;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoBox.Tests
{
    /// <summary>
    /// The gate (tasks.md, C3): a trained policy does in Unity what it did in plain C MuJoCo. Three checks,
    /// against recordings made by training/tools/footwork_c.py with the same policy:
    ///   1. replay: shown the recorded observations, Unity's copy of the policy returns the recorded actions;
    ///   2. closed loop: run from the same start, it stays up as long, steps at the same cadence and works
    ///      its joints as hard;
    ///   3. the same episodes: put in the starts the recording began from, shoved and hit with a cube at the
    ///      same moments, it ends on the floor as often.
    /// The files are put beside the boxer by MjRetrofit.Promote.
    /// </summary>
    public class MjGateTests
    {
        const string Scene = "Assets/Scenes/Testbed.unity", Boxer = "Assets/Boxers/Matt";

        static IEnumerator Load()
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(Scene, new LoadSceneParameters(LoadSceneMode.Single));
#endif
            yield return null;
            yield return new WaitForFixedUpdate();
        }

        static MjTestbed Testbed()
        {
            var testbed = UnityEngine.Object.FindAnyObjectByType<MjTestbed>();
            Assert.IsTrue(testbed.Ready, "the testbed's boxer is not bound to the model");
            Assert.IsNotNull(testbed.boxer.policy, "the boxer has no policy");
            Assert.AreEqual("policy", testbed.boxer.policy.name, "the boxer is not running its trained policy (MjRetrofit.Promote)");
            testbed.autoReset = false;
            testbed.shoves = false;
            return testbed;
        }

        static void Report(string file, StringBuilder sb)
        {
            Debug.Log(sb.ToString());
            Directory.CreateDirectory("Temp");
            File.WriteAllText("Temp/" + file, sb.ToString());
        }

        [UnityTest]
        public IEnumerator ThePolicyAnswersInUnityAsItDidInTraining()
        {
            yield return Load();
            MjBoxer boxer = Testbed().boxer;
            var reference = ReferenceTrajectory.FromJson(File.ReadAllText(Boxer + "/reference_trajectory.json"));
            var obs = new float[MjBoxer.ObservationSize];
            var action = new float[boxer.Joints];
            double worst = 0;
            foreach (var row in reference.rows)
            {
                for (int i = 0; i < obs.Length; i++) obs[i] = (float)row.obs[i];
                boxer.Infer(obs, action);
                for (int i = 0; i < action.Length; i++) worst = Math.Max(worst, Math.Abs(action[i] - row.action[i]));
            }
            Report("gate_replay.txt", new StringBuilder($"replay of {reference.rows.Length} recorded observations: the largest difference in an action is {worst:E1}\n"));
            Assert.Less(worst, 1e-4, "Unity's copy of the policy does not return the trainer's actions");
        }

        [UnityTest]
        public IEnumerator TheSameRunInUnityIsTheRunInMuJoCo()
        {
            yield return Load();
            MjTestbed testbed = Testbed();
            MjBoxer boxer = testbed.boxer;
            var reference = ReferenceTrajectory.FromJson(File.ReadAllText(Boxer + "/reference_trajectory.json"));
            int rows = reference.rows.Length, n = boxer.Joints, step = 0;
            var joint = new double[rows];
            double sqUnity = 0, sqRef = 0, wayUnity = 0, wayRef = 0;
            int[] stepsUnity = new int[2], stepsRef = new int[2];
            bool[] wasUnity = { true, true }, wasRef = { true, true };
            int upUnity = -1, upRef = -1;
            var pos = new double[3]; var quat = new double[4]; var lin = new double[3]; var ang = new double[3];
            double[] last = { 0, 0 }, lastRef = { 0, 0 };

            Action begins = () =>
            {
                foreach (var e in reference.events)
                    if (e.cube >= 0 && (int)Math.Round(e.t / reference.control_dt) == step)
                        testbed.cubes.Fire(e.qpos[0], e.qpos[1], e.qpos[2], e.qvel[0], e.qvel[1], e.qvel[2]);
            };
            Action done = () =>
            {
                if (step >= rows) return;
                var row = reference.rows[step];
                for (int i = 0; i < n; i++)
                {
                    joint[step] = Math.Max(joint[step], Math.Abs(boxer.JointPosition(i) - row.joint_pos[i]));
                    sqUnity += boxer.Torque(i) * boxer.Torque(i);
                    sqRef += row.torque[i] * row.torque[i];
                }
                for (int f = 0; f < 2; f++)
                {
                    bool down = boxer.FootDown(f);
                    if (down && !wasUnity[f]) stepsUnity[f]++;
                    if (row.foot_contact[f] && !wasRef[f]) stepsRef[f]++;
                    wasUnity[f] = down; wasRef[f] = row.foot_contact[f];
                }
                boxer.RootState(pos, quat, lin, ang);
                if (step > 0)
                {
                    wayUnity += Math.Sqrt((pos[0] - last[0]) * (pos[0] - last[0]) + (pos[1] - last[1]) * (pos[1] - last[1]));
                    wayRef += Math.Sqrt((row.root_pos[0] - lastRef[0]) * (row.root_pos[0] - lastRef[0]) + (row.root_pos[1] - lastRef[1]) * (row.root_pos[1] - lastRef[1]));
                }
                last[0] = pos[0]; last[1] = pos[1]; lastRef[0] = row.root_pos[0]; lastRef[1] = row.root_pos[1];
                if (boxer.Fallen && upUnity < 0) upUnity = step;
                if (row.fell && upRef < 0) upRef = step;
                step++;
            };

            testbed.ResetBoxer();
            boxer.SetEpisodeAt(reference.kind, reference.command[0], reference.command[1], reference.command[2], reference.stand_in_xy[0], reference.stand_in_xy[1]);
            testbed.ControlStepBegins += begins;
            testbed.ControlStepDone += done;
            Time.timeScale = 4f;
            try { while (step < rows) yield return new WaitForFixedUpdate(); }
            finally { Time.timeScale = 1f; testbed.ControlStepBegins -= begins; testbed.ControlStepDone -= done; }

            double seconds = rows * reference.control_dt;
            double rmsUnity = Math.Sqrt(sqUnity / (rows * n)), rmsRef = Math.Sqrt(sqRef / (rows * n));
            double cadenceUnity = (stepsUnity[0] + stepsUnity[1]) / seconds, cadenceRef = (stepsRef[0] + stepsRef[1]) / seconds;
            int parted = Array.FindIndex(joint, x => x > 0.05);
            string Up(int s) => s < 0 ? $"the whole {seconds:0.0} s" : $"{s * reference.control_dt:0.00} s";
            var sb = new StringBuilder($"closed loop, {seconds:0.0} s, kind {reference.kind}, command {reference.command[0]:0.00} {reference.command[1]:0.00} {reference.command[2]:0.00}; Unity against C MuJoCo {reference.mujoco}:\n");
            sb.AppendLine($"  on its feet:   Unity {Up(upUnity)}, MuJoCo {Up(upRef)}");
            sb.AppendLine($"  cadence:       Unity {cadenceUnity:0.00} footfalls a second, MuJoCo {cadenceRef:0.00}");
            sb.AppendLine($"  torque (RMS):  Unity {rmsUnity:0.00} N m, MuJoCo {rmsRef:0.00}");
            sb.AppendLine($"  ground covered: Unity {wayUnity:0.000} m, MuJoCo {wayRef:0.000} m");
            sb.AppendLine(parted < 0 ? $"  the joints never part by 0.05 rad (largest difference {Max(joint):E1} rad)" : $"  the joints part by 0.05 rad at {parted * reference.control_dt:0.00} s");
            Report("gate_closed_loop.txt", sb);

            Assert.AreEqual(upRef, upUnity, 5, "it does not stay on its feet as long in Unity as in MuJoCo");
            Assert.LessOrEqual(Math.Abs(cadenceUnity - cadenceRef), 0.10 * Math.Max(cadenceRef, 1.0 / seconds), "the cadence is not within 10%");
            Assert.LessOrEqual(Math.Abs(rmsUnity - rmsRef), 0.15 * rmsRef, "the RMS torque is not within 15%");
        }

        static double Max(double[] a) { double m = 0; foreach (double x in a) m = Math.Max(m, x); return m; }

        [UnityTest]
        public IEnumerator ItEndsOnTheFloorAsOftenInUnityAsInMuJoCo()
        {
            yield return Load();
            MjTestbed testbed = Testbed();
            MjBoxer boxer = testbed.boxer;
            var set = FallEpisodes.FromJson(File.ReadAllText(Boxer + "/falls.json"));
            int shoveStep = (int)Math.Round(set.shove_at / set.control_dt), cubeStep = (int)Math.Round(set.cube_at / set.control_dt);
            int fellHere = 0, same = 0, step = 0;
            bool fell = false;
            FallEpisodes.Episode ep = null;
            var chest = new double[3];
            Action begins = () =>
            {
                if (step == shoveStep) testbed.Shove(ep.shove[0], ep.shove[1], ep.shove[2]);
                if (step == cubeStep) { boxer.Chest(chest); testbed.cubes.Throw(chest, 5.0, ep.cube_bearing, 2.5, 0.2); }
            };
            Action done = () => { step++; if (boxer.Fallen) fell = true; };
            testbed.ControlStepBegins += begins;
            testbed.ControlStepDone += done;
            Time.timeScale = 20f;
            try
            {
                foreach (var e in set.rows)
                {
                    ep = e;
                    testbed.ResetBoxer();
                    boxer.SetState(e.root_pos, e.root_quat, e.root_linvel, e.joint_pos);
                    boxer.SetEpisodeAt(e.kind, e.command[0], e.command[1], e.command[2], e.stand_in_xy[0], e.stand_in_xy[1]);
                    step = 0; fell = false;
                    int limit = (int)Math.Round(8.0 / set.control_dt);
                    while (step < limit && !fell) yield return new WaitForFixedUpdate();
                    if (fell) fellHere++;
                    if (fell == e.fell) same++;
                }
            }
            finally { Time.timeScale = 1f; testbed.ControlStepBegins -= begins; testbed.ControlStepDone -= done; }

            double here = (double)fellHere / set.rows.Length;
            Report("gate_falls.txt", new StringBuilder(
                $"{set.rows.Length} episodes begun as they began in C MuJoCo {set.mujoco}, a shove at {set.shove_at:0.0} s and a cube at {set.cube_at:0.0} s:\n" +
                $"  on the floor at the end: Unity {here:P0}, MuJoCo {set.fall_rate:P0}; the same ending in {same} of {set.rows.Length}\n"));
            Assert.LessOrEqual(Math.Abs(here - set.fall_rate), 0.05, "the fall rate in Unity is not within 0.05 of MuJoCo's");
        }
    }
}
