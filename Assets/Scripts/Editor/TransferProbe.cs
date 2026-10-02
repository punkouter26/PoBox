using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using PoBox.Rl;
using PoBox.Sim;

namespace PoBox.EditorTools
{
    /// <summary>
    /// Does a policy trained in MuJoCo stand up in Unity's physics? Measured, headless.
    ///
    ///     Unity.exe -batchmode -nographics -projectPath . -executeMethod PoBox.EditorTools.TransferProbe.Run
    ///               [-probeMode match|hold|replay] [-probeSeconds 20] [-probeEntrant matt]
    ///
    /// Imports the newest policies from training/, rebuilds the Arena with them and plays it with no graphics
    /// at all, so it can run beside a training job without touching the GPU. Every line it prints starts
    /// with PROBE.
    ///
    ///   match   the bout as the app runs it. One line every half second of fight: each fighter's pelvis
    ///           height, how upright it is, whether it is down, what it has thrown and landed.
    ///   hold    the red fighter stood in its guard with every joint target held there and no policy, laid
    ///   replay  beside MuJoCo doing the same from the same start; or driven by the very actions MuJoCo's
    ///           run of the policy produced. training/tools/export_reference.py records both. If Unity's
    ///           body is MuJoCo's body the two stay together for a second or so before chaos parts them;
    ///           where they part at once, the column that parts first says which joint or which mass is wrong.
    /// </summary>
    [InitializeOnLoad]
    public static class TransferProbe
    {
        const string Flag = "pobox.probe.running";
        const string Seconds = "pobox.probe.seconds";
        const string Mode = "pobox.probe.mode";
        const string Entrant = "pobox.probe.entrant";
        const string Armed = "pobox.probe.armed";
        static int s_quiet, s_traces;
        static float s_traceUntil, s_traceNext;
        static bool s_listening;

        [Serializable]
        class Recording
        {
            public int steps;
            public float[] obs, act;
        }

        [Serializable]
        class Reference
        {
            public string name;
            public int iteration, obs_size, act_size;
            public float dt;
            public Recording hold, policy;
        }

        [Serializable]
        class Spot
        {
            public float x, y, yaw;
        }

        [Serializable]
        class PairReference
        {
            public string name;
            public string[] fighters;
            public int iteration, obs_size, act_size, steps;
            public float dt;
            public Spot[] starts;
            public float[] obs, act, where;
            public bool ghost;
        }

        static PairReference s_pair;
        static readonly PolicyBrain[] s_brains = new PolicyBrain[2];
        static readonly string[] Blocks = { "lin vel", "ang vel", "gravity", "joint pos", "joint vel", "last action", "feet, height", "target head", "target body", "target head vel", "own glove L", "own glove R", "their glove L", "their glove R", "their facing", "ring" };

        static int[] BlockSizes(int joints) => new[] { 3, 3, 3, joints, joints, joints, 3, 3, 3, 3, 3, 3, 3, 3, 2, 2 };

        static float s_next;
        static Reference s_ref;
        static Recording s_rec;
        static PolicyBrain s_subject;
        static bool s_started, s_finished;
        static float s_worstJoint, s_worstHeight;
        static readonly System.Collections.Generic.List<HitEvent> s_hits = new System.Collections.Generic.List<HitEvent>();

        static TransferProbe()
        {
            // The scene is rebuilt after a calibration in an open editor, so it carries the new numbers.
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool("pobox.probe.rebuild", false)) return;
                SessionState.SetBool("pobox.probe.rebuild", false);
                PoBoxBuilder.BuildAll();
                Debug.Log("PROBE scene rebuilt with the new scoring");
            };
            if (!SessionState.GetBool(Flag, false)) return;
            EditorApplication.update += Tick;
            string mode = SessionState.GetString(Mode, "match");
            if (mode == "match" || mode == "shots" || mode == "static") return;
            if (mode == "spar") { PolicyBrain.Stepped = SparStepped; return; }
            if (mode == "pair")
            {
                string both = Path.Combine(Path.GetDirectoryName(Application.dataPath), "training", "logs", $"reference_{SessionState.GetString(Entrant, "matt_vs_zombie")}.json");
                if (!File.Exists(both)) { Debug.LogError($"PROBE no reference at {both}; run training/tools/export_reference.py --match a b"); return; }
                s_pair = JsonUtility.FromJson<PairReference>(File.ReadAllText(both));
                PolicyBrain.ActionOverride = PairOverride;
                PolicyBrain.Stepped = PairStepped;
                return;
            }
            string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "training", "logs", $"reference_{SessionState.GetString(Entrant, "matt")}.json");
            if (!File.Exists(path)) { Debug.LogError($"PROBE no reference at {path}; run training/tools/export_reference.py"); return; }
            s_ref = JsonUtility.FromJson<Reference>(File.ReadAllText(path));
            s_rec = mode == "hold" ? s_ref.hold : s_ref.policy;
            PolicyBrain.ActionOverride = Override;
            PolicyBrain.Stepped = Stepped;
        }

        /// <summary>Arguments given from inside an open editor, where there is no command line to put them on.</summary>
        static readonly System.Collections.Generic.Dictionary<string, string> s_given = new System.Collections.Generic.Dictionary<string, string>();

        /// <summary>
        /// After training: imports the newest policies, spars them for five minutes on the clock with no
        /// damage and no count, sets the scorekeeper's numbers from how hard they turned out to hit, and
        /// rebuilds the scene with those numbers. In an open editor it stops playing at the end and leaves
        /// the editor as it was.
        /// </summary>
        [MenuItem("PoBox/Dev/Import Policies And Calibrate Scoring", priority = 40)]
        public static void ImportAndCalibrate()
        {
            if (EditorApplication.isPlaying) { Debug.LogWarning("PROBE stop Play first."); return; }
            s_given.Clear();
            s_given["-probeMode"] = "spar";
            s_given["-probeSeconds"] = "300";
            s_given["-probeCalibrate"] = "1";
            Run();
        }

        static string Arg(string name, string fallback)
        {
            if (s_given.TryGetValue(name, out string given)) return given;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return fallback;
        }

        public static void Run()
        {
            float.TryParse(Arg("-probeSeconds", "20"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seconds);
            string mode = Arg("-probeMode", "match");

            // Experiments with the physics engine's own settings: -probeSolver 0|1 (PGS, TGS), -probeFriction 0|1|2
            // (patch, one-directional, two-directional). They are project settings and stay as left.
            string solver = Arg("-probeSolver", ""), friction = Arg("-probeFriction", "");
            if (solver.Length > 0 || friction.Length > 0)
            {
                var physics = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset")[0]);
                if (solver.Length > 0) physics.FindProperty("m_SolverType").intValue = int.Parse(solver);
                if (friction.Length > 0) physics.FindProperty("m_FrictionType").intValue = int.Parse(friction);
                physics.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                Debug.Log($"PROBE physics settings: solver type {physics.FindProperty("m_SolverType").intValue}, friction type {physics.FindProperty("m_FrictionType").intValue}");
            }

            if (Arg("-probeNoImport", "") != "1") EntrantFactory.Import();
            PoBoxBuilder.BuildAll();
            EditorSceneManager.OpenScene(PoBoxBuilder.ScenePath, OpenSceneMode.Single);
            SessionState.SetBool(Flag, true);
            SessionState.SetFloat(Seconds, seconds);
            SessionState.SetString(Mode, mode);
            SessionState.SetString(Entrant, Arg("-probeEntrant", "matt"));
            SessionState.SetBool(Armed, false);
            SessionState.SetString("pobox.probe.joint", Arg("-probeJoint", ""));
            SessionState.SetString("pobox.probe.ghost", Arg("-probeGhost", ""));
            SessionState.SetString("pobox.probe.calibrate", Arg("-probeCalibrate", ""));
            SessionState.SetString("pobox.probe.pos", Arg("-probePosIters", ""));
            SessionState.SetString("pobox.probe.vel", Arg("-probeVelIters", ""));
            Debug.Log($"PROBE start: {mode}, {seconds:0} s");
            EditorApplication.update += Tick;
        }

        /// <summary>
        /// Play starts only once the editor has nothing left to compile. Building the scene can leave a
        /// recompile pending; started straight away, play runs for twenty seconds and then has its scripts
        /// reloaded under it, which empties every static and every policy and looks like a physics fault.
        /// </summary>
        static void WaitThenPlay()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { s_quiet = 0; return; }
            if (++s_quiet < 60) return;
            SessionState.SetBool(Armed, true);
            EditorApplication.EnterPlaymode();
        }

        /// <summary>What the scorekeeper has to work with: how hard the hits that landed were, per fighter.</summary>
        static void HitSummary(Bout bout)
        {
            foreach (Fighter f in new[] { bout.red, bout.blue })
            {
                var mine = s_hits.FindAll(h => h.attacker == f);
                if (mine.Count == 0) { Debug.Log($"PROBE hits by {f.displayName}: none"); continue; }
                mine.Sort((a, b) => a.impulse.CompareTo(b.impulse));
                float At(float q) => mine[Mathf.Clamp(Mathf.RoundToInt(q * (mine.Count - 1)), 0, mine.Count - 1)].impulse;
                int head = mine.FindAll(h => h.zone == PartKind.Head).Count, body = mine.FindAll(h => h.zone == PartKind.Torso || h.zone == PartKind.Pelvis).Count;
                float damage = 0f, speed = 0f;
                foreach (HitEvent h in mine) { damage += h.damage; speed += h.gloveSpeed; }
                Debug.Log($"PROBE hits by {f.displayName}: {mine.Count} in {Bout.Clock:0} s of fight ({head} head, {body} body, {mine.Count - head - body} guard); " +
                          $"impulse Ns p10 {At(0.1f):0.0} median {At(0.5f):0.0} p90 {At(0.9f):0.0} max {At(1f):0.0}; glove {speed / mine.Count:0.0} m/s; " +
                          $"damage {damage:0} hp, {damage / mine.Count:0.00} a hit; thrown {f.stats.thrown}, knockdowns scored {f.stats.knockdowns}; own health {f.Health:0}");
            }
        }

        static void Finish()
        {
            if (s_rec == null && s_pair == null && Bout.Instance != null) HitSummary(Bout.Instance);
            SessionState.SetBool(Flag, false);
            Debug.Log("PROBE end");
            // Headless, the probe is the whole session. In somebody's open editor it only stops playing.
            if (Application.isBatchMode) EditorApplication.Exit(0);
            else
            {
                SessionState.SetBool("pobox.probe.rebuild", SessionState.GetString("pobox.probe.calibrate", "") == "1");
                EditorApplication.ExitPlaymode();
            }
        }

        // ---------------------------------------------------------------- hold and replay

        static bool Override(PolicyBrain brain, int step, float[] action)
        {
            if (brain != s_subject || !s_started)
            {
                // Everybody else, and the subject until its start: hold the guard.
                Array.Clear(action, 0, action.Length);
                return true;
            }
            if (step < s_rec.steps) Array.Copy(s_rec.act, step * s_ref.act_size, action, 0, action.Length);
            else Array.Clear(action, 0, action.Length);
            return true;
        }

        static void Stepped(PolicyBrain brain, int step)
        {
            if (brain != s_subject || !s_started || s_finished) return;
            if (step >= s_rec.steps) { s_finished = true; return; }
            float[] u = brain.LastObservation;
            int n = s_ref.act_size, at = step * s_ref.obs_size;
            float[] r = s_rec.obs;
            int height = 9 + 3 * n + 2, gloves = height + 1 + 9;

            int worst = 0;
            float worstBy = 0f;
            for (int i = 0; i < n; i++)
            {
                float d = Mathf.Abs(u[9 + i] - r[at + 9 + i]);
                if (d > worstBy) { worstBy = d; worst = i; }
            }
            if (step <= 50)
            {
                s_worstJoint = Mathf.Max(s_worstJoint, worstBy);
                s_worstHeight = Mathf.Max(s_worstHeight, Mathf.Abs(u[height] - r[at + height]));
            }

            if (step == 0) Overlaps(brain.rig);
            // One joint in close-up, every step: -probeJoint ankle_y_r
            string watch = SessionState.GetString("pobox.probe.joint", "");
            if (watch.Length > 0 && step < 45)
            {
                int j = Array.IndexOf(brain.rig.jointNames, watch);
                if (j >= 0)
                {
                    ArticulationBody ab = brain.rig.joints[j];
                    float asked = Mathf.Clamp(brain.rig.defaultPos[j] + 0.5f * s_rec.act[step * n + j], brain.rig.lower[j], brain.rig.upper[j]) - brain.rig.defaultPos[j];
                    Debug.Log($"PROBE {watch} {step,2}: asked {asked,6:0.000}   angle {u[9 + j],6:0.000} | {r[at + 9 + j],6:0.000}   speed {u[9 + n + j],6:0.00} | {r[at + 9 + n + j],6:0.00}   " +
                              $"drive torque {ab.driveForce[0],6:0.0} of {ab.xDrive.forceLimit:0}   stiffness {ab.xDrive.stiffness:0} damping {ab.xDrive.damping:0.0}");
                }
            }
            if (step == 0)
            {
                var first = new StringBuilder("PROBE step 0, before anything has moved (Unity | MuJoCo):");
                first.Append($"\nPROBE   gravity   {u[6]:0.000} {u[7]:0.000} {u[8]:0.000} | {r[at + 6]:0.000} {r[at + 7]:0.000} {r[at + 8]:0.000}");
                first.Append($"\nPROBE   feet,h    {u[height - 2]:0} {u[height - 1]:0} {u[height]:0.000} | {r[at + height - 2]:0} {r[at + height - 1]:0} {r[at + height]:0.000}");
                first.Append($"\nPROBE   glove L   {u[gloves]:0.000} {u[gloves + 1]:0.000} {u[gloves + 2]:0.000} | {r[at + gloves]:0.000} {r[at + gloves + 1]:0.000} {r[at + gloves + 2]:0.000}");
                first.Append($"\nPROBE   glove R   {u[gloves + 3]:0.000} {u[gloves + 4]:0.000} {u[gloves + 5]:0.000} | {r[at + gloves + 3]:0.000} {r[at + gloves + 4]:0.000} {r[at + gloves + 5]:0.000}");
                if (brain.HasModel)
                {
                    // The policy itself: MuJoCo's observation in, and the action should be MuJoCo's action.
                    var given = new float[s_ref.obs_size];
                    float worstAction = 0f;
                    foreach (int s in new[] { 0, Mathf.Min(20, s_ref.policy.steps - 1), Mathf.Min(60, s_ref.policy.steps - 1) })
                    {
                        Array.Copy(s_ref.policy.obs, s * s_ref.obs_size, given, 0, given.Length);
                        float[] a = brain.Infer(given);
                        for (int i = 0; i < n; i++)
                            worstAction = Mathf.Max(worstAction, Mathf.Abs(Mathf.Clamp(a[i], -brain.rig.actionClip, brain.rig.actionClip) - s_ref.policy.act[s * n + i]));
                    }
                    first.Append($"\nPROBE   policy    given MuJoCo's observations, Unity's copy of the policy differs from MuJoCo's by at most {worstAction:0.0000} in any action");
                }
                Debug.Log(first.ToString());
            }

            if (step % 5 == 0 || step == s_rec.steps - 1)
                Debug.Log($"PROBE {step,3} t={step * s_ref.dt:0.00}  height {u[height]:0.000} | {r[at + height]:0.000}   " +
                          $"tilt fwd {u[6]:0.000} | {r[at + 6]:0.000}  side {u[7]:0.000} | {r[at + 7]:0.000}   " +
                          $"feet {u[height - 2]:0}{u[height - 1]:0} | {r[at + height - 2]:0}{r[at + height - 1]:0}   " +
                          $"glove L x {u[gloves]:0.00} | {r[at + gloves]:0.00}   " +
                          $"worst joint {brain.rig.jointNames[worst]} {u[9 + worst]:0.000} | {r[at + 9 + worst]:0.000}");
            if (step == s_rec.steps - 1)
            {
                s_finished = true;
                Debug.Log($"PROBE over the first second: joints within {s_worstJoint:0.000} rad, pelvis height within {s_worstHeight * 100f:0.0} cm");
            }
        }

        /// <summary>Which of the fighter's own shapes are inside each other as it stands, and whether each such pair is told to ignore it.</summary>
        static void Overlaps(MjcfRig rig)
        {
            Collider[] all = rig.GetComponentsInChildren<Collider>();
            var excludes = rig.GetComponent<MjcfContactExcludes>();
            var s = new StringBuilder($"PROBE overlaps in the guard ({all.Length} shapes, {(excludes != null ? excludes.a.Length : 0)} pairs listed to ignore):");
            for (int i = 0; i < all.Length; i++)
                for (int j = i + 1; j < all.Length; j++)
                {
                    Collider a = all[i], b = all[j];
                    if (a.attachedArticulationBody == b.attachedArticulationBody) continue;
                    if (!Physics.ComputePenetration(a, a.transform.position, a.transform.rotation, b, b.transform.position, b.transform.rotation, out _, out float depth)) continue;
                    s.Append($"\nPROBE   {a.name} x {b.name}: {depth * 100f:0.0} cm, {(Physics.GetIgnoreCollision(a, b) ? "ignored" : "COLLIDING")}");
                }
            Debug.Log(s.ToString());
        }

        static void StartSubject(Bout bout)
        {
            bool blue = bout.blue.mjcf.fighterName == s_ref.name && bout.red.mjcf.fighterName != s_ref.name;
            s_subject = (blue ? bout.blue : bout.red).GetComponent<PolicyBrain>();
            // The other fighter is furniture for this: out of the way and unable to fall.
            Fighter otherFighter = blue ? bout.red : bout.blue;
            MjcfRig other = otherFighter.mjcf;
            Vector3 centre = s_subject.ringCentre;
            otherFighter.Respawn(centre + new Vector3(2.2f, 0f, 2.2f), Quaternion.LookRotation(Vector3.back), true);
            other.root.immovable = true;
            (blue ? bout.blue : bout.red).Respawn(centre + new Vector3(-0.75f, 0f, 0f), Quaternion.LookRotation(Vector3.right), true);
            s_subject.ResetBrain();
            s_started = true;
            Debug.Log($"PROBE {SessionState.GetString(Mode, "")} of {s_ref.name} (iteration {s_ref.iteration}): {s_rec.steps} steps. Unity | MuJoCo");
        }

        // ---------------------------------------------------------------- training's episodes, in Unity

        static bool s_sparring;
        static int s_episodeSteps, s_episodes, s_timeouts, s_totalSteps;
        static readonly int[] s_falls = new int[2];
        static readonly System.Random s_random = new System.Random(7);

        static float Uniform(float lo, float hi) => lo + (float)s_random.NextDouble() * (hi - lo);

        /// <summary>
        /// -probeMode spar: the two fighters with the bout's rules taken away and training's put in their
        /// place. No damage, no count, no referee: an episode starts with the two somewhere in the ring 0.85
        /// to 2.2 m apart and roughly facing, and ends when one falls or after 12 s, exactly as in
        /// training/envs/boxing.py. The fall rate and episode length it prints are the same quantities the
        /// trainer reports, so they can be read side by side (training/tools/eval_match.py gives MuJoCo's,
        /// for the same policies acting without exploration noise).
        /// </summary>
        static void StartSpar(Bout bout)
        {
            bout.roundSeconds = 100000f;
            bout.rounds = 1;
            foreach (Fighter f in new[] { bout.red, bout.blue })
            {
                f.damagePerNs = 0f;
                f.staggerShock = f.knockdownShock = 1e9f;
                f.dazeRule = false;
            }
            if (SessionState.GetString("pobox.probe.ghost", "") == "1")
            {
                // The two pass through each other: whatever goes wrong now is nothing to do with being touched.
                foreach (Collider a in bout.red.GetComponentsInChildren<Collider>())
                    foreach (Collider b in bout.blue.GetComponentsInChildren<Collider>())
                        Physics.IgnoreCollision(a, b, true);
                Debug.Log("PROBE spar: the two fighters cannot touch each other");
            }
            bout.Restart();
            s_brains[0] = bout.red.GetComponent<PolicyBrain>();
            s_brains[1] = bout.blue.GetComponent<PolicyBrain>();
            s_sparring = true;
            SparReset(bout);
            Debug.Log($"PROBE spar: {bout.red.displayName} v {bout.blue.displayName}, training's episodes");
        }

        static void SparReset(Bout bout)
        {
            Vector3 centre = s_brains[0].ringCentre + new Vector3(Uniform(-1.4f, 1.4f), 0f, Uniform(-1.4f, 1.4f));
            float bearing = Uniform(-Mathf.PI, Mathf.PI), apart = Uniform(0.85f, 2.2f);
            Vector3 u = new Vector3(Mathf.Cos(bearing), 0f, Mathf.Sin(bearing));
            for (int k = 0; k < 2; k++)
            {
                float yaw = bearing + (k == 0 ? 0f : Mathf.PI) + Uniform(-0.6f, 0.6f);
                Fighter f = k == 0 ? bout.red : bout.blue;
                f.Respawn(centre + u * (apart * (k == 0 ? -0.5f : 0.5f)), Quaternion.LookRotation(new Vector3(Mathf.Cos(yaw), 0f, Mathf.Sin(yaw))), true);
                s_brains[k].ResetBrain();
            }
            s_episodeSteps = 0;
        }

        static void SparStepped(PolicyBrain brain, int step)
        {
            if (!s_sparring || brain != s_brains[1]) return;   // once per control step, after both have acted
            Bout bout = Bout.Instance;
            s_episodeSteps++;
            s_totalSteps++;
            bool any = false;
            for (int k = 0; k < 2; k++)
            {
                Fighter f = k == 0 ? bout.red : bout.blue;
                if (s_episodeSteps > 2 && (f.PelvisHeight < f.standingPelvisHeight * 0.6f || f.Upright < 0.4f))
                {
                    s_falls[k]++;
                    any = true;
                    Fighter o = k == 0 ? bout.blue : bout.red;
                    Vector3 gap = o.pelvis.transform.position - f.pelvis.transform.position;
                    gap.y = 0f;
                    Vector3 fwd = f.mjcf.Forward;
                    // Which way it went over: the pelvis's up axis, seen from above, in its own heading.
                    Vector3 lean = f.pelvis.transform.up; lean.y = 0f;
                    float along = Vector3.Dot(lean.normalized, new Vector3(fwd.x, 0f, fwd.z).normalized);
                    if (s_episodes < 60)
                        Debug.Log($"PROBE fall: {f.displayName} {s_episodeSteps * 0.02f:0.0} s into the episode, {gap.magnitude:0.00} m from the other, went {(along > 0.5f ? "forwards" : along < -0.5f ? "backwards" : "sideways")}, " +
                                  $"last hit taken {(f.TimeSinceHit < 50f ? f.TimeSinceHit.ToString("0.0") + " s ago, " + f.LastHitTaken.impulse.ToString("0.0") + " Ns" : "never")}, own last punch landed {(o.TimeSinceHit < 50f ? o.TimeSinceHit.ToString("0.0") + " s ago" : "never")}, feet {(f.footL.transform.position - f.footR.transform.position).magnitude:0.00} m apart");
                }
            }
            bool timeout = s_episodeSteps >= 600;
            if (!any && !timeout) return;
            s_episodes++;
            if (!any) s_timeouts++;
            SparReset(bout);
        }

        static void SparSummary(Bout bout)
        {
            float seconds = s_totalSteps * 0.02f;
            Debug.Log($"PROBE spar result: {s_episodes} episodes in {seconds:0} s; stays up {seconds / Mathf.Max(1, s_episodes):0.0} s of an episode; " +
                      $"fall rate {(s_episodes - s_timeouts) / (float)Mathf.Max(1, s_episodes):0.00}; " +
                      $"{bout.red.displayName} down in {s_falls[0] / (float)Mathf.Max(1, s_episodes):0%} of endings, {bout.blue.displayName} in {s_falls[1] / (float)Mathf.Max(1, s_episodes):0%}");
            foreach (Fighter f in new[] { bout.red, bout.blue })
            {
                var mine = s_hits.FindAll(h => h.attacker == f);
                mine.Sort((a, b) => a.impulse.CompareTo(b.impulse));
                float speed = 0f;
                foreach (HitEvent h in mine) speed += h.gloveSpeed;
                Debug.Log($"PROBE spar hits by {f.displayName}: {mine.Count / Mathf.Max(1f, seconds):0.00}/s, glove at {(mine.Count > 0 ? speed / mine.Count : 0f):0.0} m/s, " +
                          $"median {(mine.Count > 0 ? mine[mine.Count / 2].impulse : 0f):0.0} Ns, hardest {(mine.Count > 0 ? mine[mine.Count - 1].impulse : 0f):0.0} Ns; thrown {f.stats.thrown}");
            }
        }

        [Serializable]
        public class Scoring
        {
            public float impulseFloor, damagePerNs, staggerShock, knockdownShock;
            public int hits;
            public float seconds;
            public string measured;
        }

        /// <summary>
        /// Sets the scorekeeper's four numbers from the sparring just measured and writes them to
        /// Assets/Entrants/scoring.json, which the scene builder reads. How hard a trained fighter hits is
        /// whatever training made it, and changes with every hour of it; rules written for one evening's
        /// fighters make the next evening's either invulnerable or made of glass. So the rules are stated
        /// in terms of the fighters themselves:
        ///   nearly every landed punch counts (the floor is half the tenth-percentile impulse);
        ///   the busier fighter, landing all bout as it did here, takes 100 points of health off the other;
        ///   the hardest 4% of punches stagger, and the hardest 0.8% put a fighter down.
        /// </summary>
        static void Calibrate(Bout bout, float seconds)
        {
            if (s_hits.Count < 40) { Debug.LogWarning($"PROBE scoring: only {s_hits.Count} hits measured; scoring.json left as it is."); return; }
            var impulses = new System.Collections.Generic.List<float>();
            var shocks = new System.Collections.Generic.List<float>();
            foreach (HitEvent h in s_hits) { impulses.Add(h.impulse); shocks.Add(h.impulse * Fighter.ZoneWeight(h.zone)); }
            impulses.Sort();
            shocks.Sort();
            float Q(System.Collections.Generic.List<float> v, float q) => v[Mathf.Clamp(Mathf.RoundToInt(q * (v.Count - 1)), 0, v.Count - 1)];

            var s = new Scoring { hits = s_hits.Count, seconds = seconds };
            s.impulseFloor = 0.5f * Q(impulses, 0.10f);
            float busiest = 0f;
            foreach (Fighter f in new[] { bout.red, bout.blue })
            {
                float unit = 0f;
                foreach (HitEvent h in s_hits)
                    if (h.attacker == f) unit += Mathf.Max(0f, h.impulse - s.impulseFloor) * Fighter.ZoneWeight(h.zone);
                busiest = Mathf.Max(busiest, unit / seconds);
            }
            float boutSeconds = bout.rounds * 45f;
            s.damagePerNs = 100f / (3f * 45f * Mathf.Max(1e-3f, busiest));
            s.staggerShock = Q(shocks, 0.96f);
            s.knockdownShock = Mathf.Max(Q(shocks, 0.992f), s.staggerShock * 1.01f);
            s.measured = $"{bout.red.displayName} v {bout.blue.displayName}, {DateTime.Now:yyyy-MM-dd HH:mm}";
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), EntrantFactory.EntrantsDir, "scoring.json"), JsonUtility.ToJson(s, true));
            Debug.Log($"PROBE scoring from {s.hits} hits in {seconds:0} s: floor {s.impulseFloor:0.0} Ns, {s.damagePerNs:0.000} health per Ns, stagger at {s.staggerShock:0.0}, down at {s.knockdownShock:0.0} " +
                      $"(impulse x zone; median {Q(shocks, 0.5f):0.0}, hardest {Q(shocks, 1f):0.0})");
        }

        // ---------------------------------------------------------------- the two together

        static bool PairOverride(PolicyBrain brain, int step, float[] action)
        {
            int who = brain == s_brains[0] ? 0 : brain == s_brains[1] ? 1 : -1;
            if (who < 0 || !s_started || step >= s_pair.steps) { Array.Clear(action, 0, action.Length); return true; }
            Array.Copy(s_pair.act, (step * 2 + who) * s_pair.act_size, action, 0, action.Length);
            return true;
        }

        /// <summary>
        /// -probeMode pair: both fighters stood where MuJoCo stood them and driven by the actions MuJoCo's run
        /// of their two policies produced. What it checks is the observation, all hundred numbers of it for
        /// each fighter and above all the quarter that describes the other fighter, which no recording with a
        /// bag can check. Before anything moves the two must agree to the millimetre.
        /// </summary>
        static void PairStepped(PolicyBrain brain, int step)
        {
            int who = brain == s_brains[0] ? 0 : brain == s_brains[1] ? 1 : -1;
            if (who < 0 || !s_started || s_finished) return;
            if (step >= s_pair.steps) { s_finished = true; return; }
            float[] u = brain.LastObservation, r = s_pair.obs;
            int at = (step * 2 + who) * s_pair.obs_size;
            int[] sizes = BlockSizes(s_pair.act_size);

            if (step == 0 || step == 1)
            {
                var s = new StringBuilder($"PROBE {s_pair.fighters[who]} step {step}: every block, largest difference Unity against MuJoCo");
                int o = 0;
                for (int b = 0; b < sizes.Length; b++)
                {
                    float worst = 0f;
                    for (int i = 0; i < sizes[b]; i++) worst = Mathf.Max(worst, Mathf.Abs(u[o + i] - r[at + o + i]));
                    s.Append($"\nPROBE   {Blocks[b],-16} {worst:0.000}");
                    if (worst > 0.02f && sizes[b] <= 3)
                    {
                        s.Append("   Unity");
                        for (int i = 0; i < sizes[b]; i++) s.Append($" {u[o + i]:0.000}");
                        s.Append("   MuJoCo");
                        for (int i = 0; i < sizes[b]; i++) s.Append($" {r[at + o + i]:0.000}");
                    }
                    o += sizes[b];
                }
                Debug.Log(s.ToString());
            }
            else if (step % 5 == 0)
            {
                int o = 0, worstBlock = 0;
                float body = 0f, other = 0f, worstBy = 0f;
                for (int b = 0; b < sizes.Length; b++)
                {
                    float worst = 0f;
                    for (int i = 0; i < sizes[b]; i++) worst = Mathf.Max(worst, Mathf.Abs(u[o + i] - r[at + o + i]));
                    // Speeds are in rad/s and m/s and swing by whole units; positions are what to read.
                    if (b == 3 || b == 2 || b == 6 || b == 10 || b == 11) body = Mathf.Max(body, worst);
                    if (b == 7 || b == 8 || b == 12 || b == 13 || b == 14 || b == 15) { other = Mathf.Max(other, worst); if (worst > worstBy) { worstBy = worst; worstBlock = b; } }
                    o += sizes[b];
                }
                int h = 9 + 3 * s_pair.act_size + 2;
                if (s_pair.where != null && s_pair.where.Length > 0)
                {
                    // Where it stands in the ring and where its feet are, which is where a slipping foot shows.
                    MjcfRig rig = brain.rig;
                    Vector3 c = brain.ringCentre, p = rig.root.transform.position - c;
                    Vector3 fl = rig.footL.transform.TransformPoint(rig.footL.center) - c, fr = rig.footR.transform.TransformPoint(rig.footR.center) - c;
                    int w = (step * 2 + who) * 7;
                    float[] m = s_pair.where;
                    int worstJoint = 0;
                    float worstJointBy = 0f;
                    for (int i = 0; i < s_pair.act_size; i++)
                    {
                        float d = Mathf.Abs(u[9 + i] - r[at + 9 + i]);
                        if (d > worstJointBy) { worstJointBy = d; worstJoint = i; }
                    }
                    Debug.Log($"PROBE {s_pair.fighters[who],-7} {step,3} t={step * s_pair.dt:0.0}  h {u[h]:0.000} | {r[at + h]:0.000}  tilt fwd {u[6]:0.00} | {r[at + 6]:0.00} side {u[7]:0.00} | {r[at + 7]:0.00}  " +
                              $"pelvis ({p.x:0.00},{p.z:0.00}) | ({m[w]:0.00},{m[w + 1]:0.00})  yaw {rig.Yaw:0.00} | {m[w + 2]:0.00}  " +
                              $"foot L ({fl.x:0.00},{fl.z:0.00}) | ({m[w + 3]:0.00},{m[w + 4]:0.00})  R ({fr.x:0.00},{fr.z:0.00}) | ({m[w + 5]:0.00},{m[w + 6]:0.00})  feet {u[h - 2]:0}{u[h - 1]:0} | {r[at + h - 2]:0}{r[at + h - 1]:0}  " +
                              $"{rig.jointNames[worstJoint]} {u[9 + worstJoint]:0.00} | {r[at + 9 + worstJoint]:0.00}");
                    if (step == s_pair.steps - 1 && who == 1) s_finished = true;
                    return;
                }
                // target body block starts three after the height; its first number is how far ahead the other fighter is.
                Debug.Log($"PROBE {s_pair.fighters[who],-7} {step,3} t={step * s_pair.dt:0.0}  height {u[h]:0.000} | {r[at + h]:0.000}   tilt fwd {u[6]:0.00} | {r[at + 6]:0.00} side {u[7]:0.00} | {r[at + 7]:0.00}   " +
                          $"other ahead {u[h + 4]:0.00} | {r[at + h + 4]:0.00} left {u[h + 5]:0.00} | {r[at + h + 5]:0.00}   glove L ahead {u[h + 10]:0.00} | {r[at + h + 10]:0.00}  R {u[h + 13]:0.00} | {r[at + h + 13]:0.00}   feet {u[h - 2]:0}{u[h - 1]:0} | {r[at + h - 2]:0}{r[at + h - 1]:0}");
            }
            if (step == s_pair.steps - 1 && who == 1) s_finished = true;
        }

        static void StartPair(Bout bout)
        {
            Fighter first = bout.red.mjcf.fighterName == s_pair.fighters[0] ? bout.red : bout.blue;
            Fighter second = first == bout.red ? bout.blue : bout.red;
            s_brains[0] = first.GetComponent<PolicyBrain>();
            s_brains[1] = second.GetComponent<PolicyBrain>();
            Vector3 centre = s_brains[0].ringCentre;
            // A comparison of bodies, not a bout: nobody is hurt and nobody is counted out.
            foreach (Fighter f in new[] { first, second })
            {
                f.damagePerNs = 0f;
                f.staggerShock = f.knockdownShock = 1e9f;
                f.dazeRule = false;
            }
            if (s_pair.ghost)
                foreach (Collider a in first.GetComponentsInChildren<Collider>())
                    foreach (Collider b in second.GetComponentsInChildren<Collider>())
                        Physics.IgnoreCollision(a, b, true);
            for (int k = 0; k < 2; k++)
            {
                Spot at = s_pair.starts[k];
                // Through the fighter, not the rig: a fighter that fell over during the introductions is
                // still "down" as far as the bout is concerned, with its drives switched off.
                (k == 0 ? first : second).Respawn(centre + new Vector3(at.x, 0f, at.y), Quaternion.LookRotation(new Vector3(Mathf.Cos(at.yaw), 0f, Mathf.Sin(at.yaw))), true);
            }
            s_brains[0].ResetBrain();
            s_brains[1].ResetBrain();
            s_started = true;
            Debug.Log($"PROBE pair {s_pair.name} (iteration {s_pair.iteration}): {s_pair.steps} steps, {s_pair.fighters[0]} is {first.displayName} in the {(first == bout.red ? "red" : "blue")} corner");
        }

        // ---------------------------------------------------------------- how stiff the joints really are

        static float s_staticStart = -1f;

        /// <summary>
        /// -probeMode static: each fighter held in the air by its pelvis with every joint asked to be at
        /// zero (arms straight out, legs straight down). A joint then sags by the weight it carries divided
        /// by its stiffness, which is arithmetic, so it can be laid beside MuJoCo's number for the same pose
        /// without running MuJoCo at all (gravity torque / kp; the shoulder carries 12.8 N m at 160 N m/rad,
        /// 0.080 rad). If Unity's joints are softer or stiffer than the file says, it shows here and nowhere else.
        /// </summary>
        static void Static(Bout bout)
        {
            if (bout.Phase != BoutPhase.Fight) return;
            if (s_staticStart < 0f)
            {
                s_staticStart = Time.time;
                bout.roundSeconds = 100000f;
                foreach (Fighter f in new[] { bout.red, bout.blue })
                {
                    var brain = f.GetComponent<PolicyBrain>();
                    brain.enabled = false;
                    MjcfRig rig = f.mjcf;
                    if (int.TryParse(SessionState.GetString("pobox.probe.pos", ""), out int posIters)) rig.root.solverIterations = posIters;
                    if (int.TryParse(SessionState.GetString("pobox.probe.vel", ""), out int velIters)) rig.root.solverVelocityIterations = velIters;
                    Debug.Log($"PROBE static {f.displayName}: solver iterations {rig.root.solverIterations} position, {rig.root.solverVelocityIterations} velocity");
                    rig.root.TeleportRoot(rig.root.transform.position + Vector3.up * 0.6f, rig.root.transform.rotation);
                    rig.root.immovable = true;
                    var zero = new float[rig.joints.Length];
                    for (int i = 0; i < rig.joints.Length; i++)
                    {
                        rig.joints[i].jointPosition = new ArticulationReducedSpace(0f);
                        rig.joints[i].jointVelocity = new ArticulationReducedSpace(0f);
                    }
                    rig.ApplyTargets(zero);
                }
                return;
            }
            if (Time.time >= s_next)
            {
                s_next = Time.time + 0.5f;
                MjcfRig z = bout.blue.mjcf;
                int sx = Array.IndexOf(z.jointNames, "shoulder_x_l"), ay = Array.IndexOf(z.jointNames, "abdomen_y");
                Debug.Log($"PROBE static t={Time.time - s_staticStart:0.0} (timescale {Time.timeScale:0.0}, step {Time.fixedDeltaTime:0.0000}): {bout.blue.displayName} shoulder_x_l {-z.joints[sx].jointPosition[0]:0.0000} rad, {z.joints[sx].driveForce[0]:0.00} N m; abdomen_y {-z.joints[ay].jointPosition[0]:0.0000} rad, {z.joints[ay].driveForce[0]:0.00} N m; pelvis at {z.root.transform.position.y:0.000}");
            }
            if (Time.time - s_staticStart < 9f) return;
            foreach (Fighter f in new[] { bout.red, bout.blue })
            {
                MjcfRig rig = f.mjcf;
                var pos = new float[rig.joints.Length];
                var vel = new float[rig.joints.Length];
                rig.ReadJointState(pos, vel);
                var s = new StringBuilder($"PROBE static {f.displayName}: sag in radians (trainer's sign), drive torque, and the stiffness that implies");
                for (int i = 0; i < rig.joints.Length; i++)
                {
                    float torque = rig.joints[i].driveForce[0];
                    s.Append($"\nPROBE   {rig.jointNames[i],-14} sag {pos[i],8:0.0000}   speed {vel[i],6:0.000}   drive {torque,7:0.00} N m   {(Mathf.Abs(pos[i]) > 0.003f ? Mathf.Abs(torque / pos[i]).ToString("0") + " N m/rad against " + rig.joints[i].xDrive.stiffness.ToString("0") + " set" : "")}");
                }
                Debug.Log(s.ToString());
            }
            s_pair = new PairReference();
            Finish();
        }

        // ---------------------------------------------------------------- pictures

        static readonly float[] ShotTimes = { 0.25f, 3.5f, 5.0f, 6.5f, 8.0f, 9.5f };   // the first during the introductions, in the guard
        static int s_shot;
        static float s_fightStart = -1f;

        /// <summary>
        /// -probeMode shots (run WITHOUT -nographics): stills of the two fighters a few moments into the fight,
        /// from cameras of the probe's own, into Logs/shots. For checking that the skinned meshes sit on the
        /// bodies. The picture has no HUD and no post-processing; it is the fighters that are being looked at.
        /// </summary>
        static void Shots(Bout bout)
        {
            if (s_fightStart < 0f) s_fightStart = Time.time;
            if (s_shot >= ShotTimes.Length) { Finish(); return; }
            if (Time.time - s_fightStart < ShotTimes[s_shot]) return;

            string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "shots");
            Directory.CreateDirectory(dir);
            Vector3 r = bout.red.pelvis.transform.position, b = bout.blue.pelvis.transform.position;
            Vector3 mid = (r + b) * 0.5f;
            Vector3 across = Vector3.Cross(Vector3.up, (b - r).normalized);
            if (across.sqrMagnitude < 0.01f) across = Vector3.forward;
            float apart = Mathf.Max(1.6f, Vector3.Distance(r, b));

            Snap(Path.Combine(dir, $"both_{s_shot}.png"), mid + across * (apart * 0.9f + 2.0f) + Vector3.up * 0.35f, mid + Vector3.up * 0.1f, 1100, 760, 38f);
            foreach (Fighter f in new[] { bout.red, bout.blue })
            {
                Vector3 p = f.pelvis.transform.position;
                Vector3 front = f.mjcf != null ? f.mjcf.Forward : f.transform.forward;
                front.y = 0f; front.Normalize();
                Vector3 side = Vector3.Cross(Vector3.up, front);
                Snap(Path.Combine(dir, $"{f.displayName.ToLowerInvariant()}_front_{s_shot}.png"), p + front * 2.5f + side * 0.9f + Vector3.up * 0.3f, p + Vector3.up * 0.15f, 620, 820, 40f);
            }
            Debug.Log($"PROBE shot {s_shot} at {Time.time - s_fightStart:0.0} s of fight");
            if (s_shot == 0) foreach (Fighter f in new[] { bout.red, bout.blue }) Skeleton(f);
            s_shot++;
        }

        /// <summary>Where the mesh's spine bones are, in the fighter's own frame (forward, left, up from the pelvis link), beside where the rig says the torso and head are.</summary>
        static void Skeleton(Fighter f)
        {
            MjcfRig rig = f.mjcf;
            if (rig == null) return;
            Transform pelvis = rig.root.transform;
            string At(Vector3 world)
            {
                Vector3 e = CoordinateTransform.UnityToExternal(Quaternion.Inverse(pelvis.rotation) * (world - pelvis.position));
                return $"({e.x:0.00},{e.y:0.00},{e.z:0.00})";
            }
            var s = new StringBuilder($"PROBE skeleton of {f.displayName} (forward, left, up from the pelvis link): torso link {At(f.torso.transform.position)} head shape {At(rig.headGeom.position)}");
            var binder = f.GetComponent<SkinBinder>();
            if (binder != null && binder.skin != null)
            {
                s.Append($" | mesh scale {binder.FittedScale:0.000}, root {At(binder.skin.transform.position)}");
                foreach (Transform t in binder.skin.GetComponentsInChildren<Transform>())
                {
                    string n = t.name;
                    if (n.EndsWith("Hip") || n.EndsWith("Hips") || n.EndsWith("Pelvis") || n.EndsWith("Waist") || n.Contains("Spine") || n.EndsWith("Head") || n.Contains("Neck") || n == "root" || n == "Armature" || n.EndsWith("L_Thigh") || n.EndsWith("L_Clavicle") || n.EndsWith("L_Upperarm"))
                        s.Append($"\nPROBE   {n,-22} {At(t.position)}  up {CoordinateTransform.UnityToExternal(Quaternion.Inverse(pelvis.rotation) * t.up):0.00}  lossyScale {t.lossyScale.x:0.0000}");
                }
            }
            Debug.Log(s.ToString());
        }

        static void Snap(string path, Vector3 from, Vector3 lookAt, int width, int height, float fov)
        {
            var go = new GameObject("Probe Camera");
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 80f;
            go.transform.position = from;
            go.transform.LookAt(lookAt);
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture before = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = before;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(go);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }

        // ---------------------------------------------------------------- every frame

        static void Tick()
        {
            if (!EditorApplication.isPlaying)
            {
                if (!SessionState.GetBool(Armed, false)) WaitThenPlay();
                return;
            }
            Bout bout = Bout.Instance;
            if (bout == null || bout.red == null || bout.blue == null) return;

            if (s_pair != null)
            {
                if (!s_started && bout.Phase == BoutPhase.Fight) StartPair(bout);
                if (s_finished || Time.time >= SessionState.GetFloat(Seconds, 20f)) Finish();
                return;
            }
            if (s_rec != null)
            {
                if (!s_started && bout.Phase == BoutPhase.Fight) StartSubject(bout);
                if (s_finished || Time.time >= SessionState.GetFloat(Seconds, 20f)) Finish();
                return;
            }

            if (SessionState.GetString(Mode, "match") == "shots") { Shots(bout); return; }
            if (SessionState.GetString(Mode, "match") == "static") { Static(bout); return; }
            if (SessionState.GetString(Mode, "match") == "spar")
            {
                bout.userTimeScale = 2f;
                if (!s_sparring)
                {
                    SimBus.Hit += e => s_hits.Add(e);
                    StartSpar(bout);
                }
                if (Time.time >= SessionState.GetFloat(Seconds, 20f))
                {
                    SparSummary(bout);
                    if (SessionState.GetString("pobox.probe.calibrate", "") == "1") Calibrate(bout, s_totalSteps * 0.02f);
                    s_pair = new PairReference();
                    Finish();
                }
                return;
            }

            bout.userTimeScale = 2f;
            if (!s_listening)
            {
                // Here and not when the scripts load: the bus clears its listeners as play starts.
                s_listening = true;
                SimBus.Hit += e => s_hits.Add(e);
                float gotUpAt = -99f, downAt = -99f;
                string gotUpWho = "", downWho = "";
                SimBus.GotUp += f => { gotUpAt = Time.time; gotUpWho = f.displayName; };
                SimBus.Knockdown += (f, e) =>
                {
                    Fighter o = f == bout.red ? bout.blue : bout.red;
                    Vector3 gap = o.pelvis.transform.position - f.pelvis.transform.position;
                    gap.y = 0f;
                    Debug.Log($"PROBE knockdown t={Time.time:0.0} {bout.Phase} r{bout.Round}: {f.displayName} " +
                        (e.attacker != null ? $"by {e.attacker.displayName}, {e.impulse:0.0} Ns to the {e.zone}" : "on its own") +
                        $"; {gap.magnitude:0.00} m apart; {Time.time - downAt:0.0} s after {downWho} went down, {Time.time - gotUpAt:0.0} s after {gotUpWho} was stood up; " +
                        $"other is {(o.IsDown ? "down" : "up")}, pelvis {f.PelvisHeight:0.00} m, upright {f.Upright:0.00}");
                    downAt = Time.time; downWho = f.displayName;
                };
            }
            // The first few knockdowns in close-up: a line every tenth of a second for four seconds after each.
            if (bout.Phase == BoutPhase.Count && s_traceUntil < Time.time && s_traces < 3) { s_traceUntil = Time.time + 4f; s_traces++; s_traceNext = 0f; }
            if (Time.time < s_traceUntil && Time.time >= s_traceNext)
            {
                s_traceNext = Time.time + 0.1f;
                Vector3 gap = bout.blue.pelvis.transform.position - bout.red.pelvis.transform.position;
                gap.y = 0f;
                var line = new StringBuilder($"PROBE trace t={Time.time:0.0} {bout.Phase} count {bout.Count}: {gap.magnitude:0.00} m apart");
                foreach (Fighter f in new[] { bout.red, bout.blue })
                {
                    var b = f.GetComponent<PolicyBrain>();
                    Vector3 p = f.pelvis.transform.position;
                    line.Append($" | {f.displayName} h={f.PelvisHeight:0.00} up={f.Upright:0.00} {(f.IsDown ? "DOWN" : "up")} auth={f.Authority:0.0} at ({p.x:0.0},{p.z:0.0}) sees {b.SightDebug}");
                }
                Debug.Log(line.ToString());
            }

            if (Time.time < s_next) return;
            s_next = Time.time + 0.5f;

            var s = new StringBuilder($"PROBE t={Time.time:0.0} phase={bout.Phase} r{bout.Round}");
            foreach (Fighter f in new[] { bout.red, bout.blue })
            {
                var brain = f.GetComponent<PolicyBrain>();
                Vector3 p = f.pelvis.transform.position;
                s.Append($" | {f.displayName}: h={f.PelvisHeight:0.00}/{f.standingPelvisHeight:0.00} up={f.Upright:0.00} down={(f.IsDown ? 1 : 0)} hp={f.Health:0} " +
                         $"xz=({p.x:0.0},{p.z:0.0}) thrown={f.stats.thrown} landed={f.stats.landed} clean={f.stats.clean} peak={f.stats.peakImpulse:0.0}Ns kd={f.stats.knockdowns} " +
                         $"stress={f.PeakStress:0.00} W={f.PowerW:0}");
                if (brain != null) s.Append($" steps={brain.PolicySteps} clamp={brain.TargetClamping:0.00} inf={brain.InferenceMs:0.00}ms");
            }
            Debug.Log(s.ToString());

            // The clock stops on the results card, so the bout ending ends the probe too.
            if (Time.time >= SessionState.GetFloat(Seconds, 20f) || bout.Phase == BoutPhase.Results) Finish();
        }
    }
}
