using System;
using System.Collections;
using System.IO;
using System.Text;
using Mujoco;
using NUnit.Framework;
using PoBox.Mj;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoBox.Tests
{
    /// <summary>
    /// The retrofit's parity checks on the testbed scene: Unity's own physics has no part in it, the model the
    /// MuJoCo plugin compiles is the trainer's, and a boxer run here does what the same boxer did in plain C
    /// MuJoCo (training/tools/footwork_c.py --reference), control step by control step.
    /// </summary>
    public class MjParityTests
    {
        const string Scene = "Assets/Scenes/Testbed.unity", Boxer = "Assets/Boxers/Matt";

        static IEnumerator Load()
        {
            MjTestbed.Pick = "Matt";   // the hold recording and the fingerprint check are Matt's
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(Scene, new LoadSceneParameters(LoadSceneMode.Single));
#endif
            yield return null;
            yield return new WaitForFixedUpdate();
        }

        [UnityTest]
        public IEnumerator TheTestbedHasNoUnityPhysicsInIt()
        {
            yield return Load();
            Assert.AreEqual(0, UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include).Length, "Rigidbody");
            Assert.AreEqual(0, UnityEngine.Object.FindObjectsByType<ArticulationBody>(FindObjectsInactive.Include).Length, "ArticulationBody");
            Assert.AreEqual(0, UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include).Length, "Collider");
            Assert.AreEqual(0, UnityEngine.Object.FindObjectsByType<Joint>(FindObjectsInactive.Include).Length, "Joint");
            Assert.AreEqual(SimulationMode.Script, Physics.simulationMode, "Unity's physics must only step when a script asks, and none does");
        }

        [UnityTest]
        public IEnumerator UnityCompilesTheTrainersModel()
        {
            yield return Load();
            CompareModel();
        }

        static unsafe void CompareModel()
        {
            var diffs = ModelFingerprint.FromJson(File.ReadAllText(Boxer + "/fingerprint.json")).Differences(MjScene.Instance.Model);
            Assert.IsEmpty(diffs, string.Join("\n", diffs));
            Assert.AreEqual(0.005, MjScene.Instance.Model->opt.timestep, 0.0, "MuJoCo's step");
        }

        /// <summary>
        /// The zero brain: the policy file that always answers zero, so the guard is held by the joint drives
        /// alone, with one cube thrown at 2 s. Asked of it: the first observation is the trainer's (the wiring
        /// of all 103 numbers), each joint is where the trainer's was through the first second (joint order,
        /// signs, rest pose, gains), and the pelvis is at the same height.
        /// </summary>
        [UnityTest]
        public IEnumerator TheGuardHeldInUnityIsTheGuardHeldInMuJoCo()
        {
            yield return Load();
            var reference = ReferenceTrajectory.FromJson(File.ReadAllText(Boxer + "/reference_hold.json"));
            var testbed = UnityEngine.Object.FindAnyObjectByType<MjTestbed>();
            Assert.IsTrue(testbed.Ready, "the testbed's boxer is not bound to the model");
            MjBoxer boxer = testbed.boxer;
            testbed.autoReset = false;
            testbed.shoves = false;
            Assert.AreEqual(reference.control_dt, testbed.ControlDt, 1e-12, "control step");
            CollectionAssert.AreEqual(reference.joint_order, boxer.Cfg.joint_order, "joint order");
#if UNITY_EDITOR
            // Whatever rung the boxer has been promoted to, this is the zero brain's recording.
            boxer.SetPolicy(UnityEditor.AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>("Assets/Boxers/zero_policy.onnx"));
#endif

            int step = 0, rows = reference.rows.Length, n = boxer.Joints;
            double[] height = new double[rows], joint = new double[rows], obs = new double[rows], torque = new double[rows];
            int worstObs = -1;
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
                for (int i = 0; i < MjBoxer.ObservationSize; i++)
                {
                    double d = Math.Abs(boxer.Obs[i] - row.obs[i]);
                    if (d > obs[step]) { obs[step] = d; if (step == 0) worstObs = i; }
                }
                for (int i = 0; i < n; i++)
                {
                    joint[step] = Math.Max(joint[step], Math.Abs(boxer.JointPosition(i) - row.joint_pos[i]));
                    torque[step] = Math.Max(torque[step], Math.Abs(boxer.Torque(i) - row.torque[i]));
                }
                height[step] = Math.Abs(boxer.PelvisHeight - row.root_pos[2]);
                step++;
            };
            testbed.ResetBoxer();
            testbed.ControlStepBegins += begins;
            testbed.ControlStepDone += done;
            Time.timeScale = 4f;
            try { while (step < rows) yield return new WaitForFixedUpdate(); }
            finally { Time.timeScale = 1f; testbed.ControlStepBegins -= begins; testbed.ControlStepDone -= done; }

            int second = (int)Math.Round(1.0 / reference.control_dt);
            double Max(double[] a, int from, int to) { double m = 0; for (int i = from; i < Math.Min(to, a.Length); i++) m = Math.Max(m, a[i]); return m; }
            int parted = Array.FindIndex(joint, x => x > 0.01);
            var sb = new StringBuilder($"zero-action guard, Unity (MuJoCo plugin) against C MuJoCo {reference.mujoco}, {rows} control steps:\n");
            sb.AppendLine($"  first observation: largest difference {obs[0]:E1} (number {worstObs})");
            for (int s = 0; s * second < rows; s++)
                sb.AppendLine($"  second {s + 1}: pelvis height off by at most {Max(height, s * second, (s + 1) * second) * 1000:0.000} mm, a joint by {Max(joint, s * second, (s + 1) * second):E1} rad, " +
                              $"a torque by {Max(torque, s * second, (s + 1) * second):E1} N m, an observation by {Max(obs, s * second, (s + 1) * second):E1}");
            sb.AppendLine(parted < 0 ? "  the joints never part by 0.01 rad" : $"  the joints part by 0.01 rad at {parted * reference.control_dt:0.00} s");
            Debug.Log(sb.ToString());
            Directory.CreateDirectory("Temp");
            File.WriteAllText("Temp/parity_hold.txt", sb.ToString());

            Assert.Less(obs[0], 1e-4, "the first observation is not the trainer's: number " + worstObs);
            Assert.Less(Max(joint, 0, second), 0.01, "a joint is not where the trainer's was in the first second");
            Assert.Less(Max(height, 0, second), 0.003, "the pelvis is not at the trainer's height in the first second");
        }
    }
}
