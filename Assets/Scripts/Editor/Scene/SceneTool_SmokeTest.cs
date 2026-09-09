using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Plays every NON-TRAINING scene in the open Editor and writes a telemetry
    /// report to Tools/cleanup/SCENE_SMOKE_REPORT.md.
    ///
    /// WHY THIS EXISTS. The training scenes are covered: verify_train_scene.py
    /// reads their YAML and catches a hole in the fall detector without opening
    /// Unity. Nothing covered the three scenes that actually SHIP. The failure
    /// this is built to catch is the documented one -- ten of sixteen fighters
    /// throwing NullReferenceException every physics tick while the aggregate
    /// still reads plausible -- so it reports PER FIGHTER, and counts exceptions
    /// by signature rather than trusting a mean.
    ///
    /// It also records what each fighter's brain actually resolved to. A brain
    /// assigned from code mismatches its sensor in total silence, and
    /// Systems_BrainCompatibility.Accept is the only thing standing between that
    /// and a scene that looks fine while every fighter runs the fallback PD bot.
    /// The report prints sensor and expected observation width side by side so a
    /// silent mismatch is visible.
    ///
    /// RUN IT with the command bridge, which is the only way in while the Editor
    /// is open:
    ///
    ///   echo PoBox.Editor.SceneTool_SmokeTest.RunAll > Temp/agent-command.txt
    ///
    /// Entering play mode reloads the domain and wipes statics, so all run state
    /// lives in SessionState and the captured log lives on disk.
    /// </summary>
    [InitializeOnLoad]
    internal static class SceneTool_SmokeTest
    {
        private const string StateKey = "PoBox.Smoke.State";
        private const string IndexKey = "PoBox.Smoke.Index";
        private const string DeadlineKey = "PoBox.Smoke.Deadline";
        private const string LogDir = "Temp/smoke";
        private const string ReportPath = "Tools/cleanup/SCENE_SMOKE_REPORT.md";

        /// Seconds of play per scene. Long enough for the referee to start a
        /// round, and for a per-tick exception to repeat a thousand times.
        private const float SecondsPerScene = 20f;

        /// Cap on printed samples per distinct message, so a per-tick throw
        /// cannot fill the disk. The COUNT stays exact; only the text is capped.
        private const int MaxSamplesPerSignature = 3;

        /// Field separators for the SessionState tally. Control characters,
        /// because a log message can contain any printable text.
        private const char RecordSeparator = (char)30;   // ASCII record separator
        private const char UnitSeparator = (char)31;     // ASCII unit separator

        private static readonly string[] Scenes =
        {
            "Assets/Scenes/SCN_MENU.unity",
            "Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity",
            "Assets/Scenes/SCN_TEST_WALK_CONTEST.unity",
        };

        private static readonly Dictionary<string, int> Seen = new Dictionary<string, int>();

        static SceneTool_SmokeTest()
        {
            Application.logMessageReceived += OnLog;
            EditorApplication.update += Poll;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        // ------------------------------------------------------------------
        // Entry point
        // ------------------------------------------------------------------

        [MenuItem("PoBox/Scene/Smoke Test Shippable Scenes")]
        public static void RunAll()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("SceneTool_SmokeTest: already in play mode. Stop play and retry.");
                return;
            }

            // This walks scenes with OpenScene, which DISCARDS unsaved edits
            // without prompting. Refuse rather than destroy someone's work.
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var open = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!open.isDirty) { continue; }
                Debug.LogError($"SceneTool_SmokeTest: REFUSED. Scene '{open.name}' has unsaved " +
                               "changes, and this test opens scenes, which would discard them. " +
                               "Save or revert it, then run again.");
                return;
            }

            if (Directory.Exists(LogDir)) { Directory.Delete(LogDir, true); }
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            for (int i = 0; i < Scenes.Length; i++)
            {
                SessionState.EraseString(CountsKey(i));
            }

            SessionState.SetString(StateKey, Next);
            SessionState.SetInt(IndexKey, 0);
            Debug.Log($"SceneTool_SmokeTest: starting, {Scenes.Length} scenes, {SecondsPerScene} s each.");
        }

        // ------------------------------------------------------------------
        // State machine.
        //
        // ENTERING AND LEAVING PLAY MODE EACH RELOAD THE DOMAIN, which wipes
        // static fields AND any queued EditorApplication.delayCall. An earlier
        // version advanced scenes from a delayCall registered inside
        // playModeStateChanged; the reload ate it, and the run stopped silently
        // after the second scene with a report that was never written. So every
        // transition is a value in SessionState, and the only thing that acts
        // on it is Poll, which EditorApplication.update re-registers on load.
        // ------------------------------------------------------------------

        /// A scene is open and playing; sample it when the deadline passes.
        private const string Playing = "PLAYING";

        /// Between scenes. Poll opens the next one once the Editor is idle.
        private const string Next = "NEXT";

        private static string State => SessionState.GetString(StateKey, string.Empty);
        private static bool Running => State.Length > 0;

        private static void StartCurrent()
        {
            int index = SessionState.GetInt(IndexKey, 0);
            if (index >= Scenes.Length) { Finish(); return; }

            string scene = Scenes[index];
            if (!File.Exists(scene))
            {
                File.WriteAllText(DataPath(index), $"MISSING SCENE FILE\t{scene}\n");
                SessionState.SetInt(IndexKey, index + 1);
                StartCurrent();
                return;
            }

            Debug.Log($"SceneTool_SmokeTest: [{index + 1}/{Scenes.Length}] {Path.GetFileName(scene)}");
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            SessionState.SetFloat(DeadlineKey, (float)EditorApplication.timeSinceStartup + SecondsPerScene);
            SessionState.SetString(StateKey, Playing);
            EditorApplication.EnterPlaymode();
        }

        private static void Poll()
        {
            switch (State)
            {
                case Next:
                    // Wait for the Editor to settle. Opening a scene or entering
                    // play mode mid-compile or mid-import is refused silently.
                    if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode
                        || EditorApplication.isCompiling || EditorApplication.isUpdating) { return; }
                    StartCurrent();
                    return;

                case Playing:
                    if (!EditorApplication.isPlaying) { return; }
                    if (EditorApplication.timeSinceStartup
                        < SessionState.GetFloat(DeadlineKey, float.MaxValue)) { return; }

                    // Deadline reached. Sample while the scene is still live,
                    // then leave play mode; OnPlayModeChanged queues the next.
                    SessionState.SetFloat(DeadlineKey, float.MaxValue);
                    try { Snapshot(SessionState.GetInt(IndexKey, 0)); }
                    catch (Exception e) { Debug.LogError($"SceneTool_SmokeTest: snapshot failed: {e}"); }
                    EditorApplication.ExitPlaymode();
                    return;
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (State != Playing || change != PlayModeStateChange.EnteredEditMode) { return; }
            SessionState.SetInt(IndexKey, SessionState.GetInt(IndexKey, 0) + 1);
            SessionState.SetString(StateKey, Next);
        }

        private static void Finish()
        {
            SessionState.SetString(StateKey, string.Empty);
            try
            {
                File.WriteAllText(ReportPath, BuildReport());
                Debug.Log($"SceneTool_SmokeTest: DONE. Report at {ReportPath}");
            }
            catch (Exception e) { Debug.LogError($"SceneTool_SmokeTest: report failed: {e}"); }
        }

        // ------------------------------------------------------------------
        // Capture
        // ------------------------------------------------------------------

        private static string CountsKey(int index) => $"PoBox.Smoke.Counts.{index}";
        private static string LogPath(int index) => $"{LogDir}/{index}.log";
        private static string DataPath(int index) => $"{LogDir}/{index}.txt";

        private static void OnLog(string message, string stack, LogType type)
        {
            if (!Running || !EditorApplication.isPlaying) { return; }
            if (type == LogType.Log) { return; }

            // Signature = type plus the first line. Counts stay exact; only the
            // sample text is capped, so a per-tick throw is visible but not
            // ruinous.
            string first = message.Split('\n')[0];
            string signature = $"{type}\t{first}";
            Seen.TryGetValue(signature, out int n);
            Seen[signature] = n + 1;

            int index = SessionState.GetInt(IndexKey, 0);
            if (n < MaxSamplesPerSignature)
            {
                string frame = n == 0 && !string.IsNullOrEmpty(stack)
                    ? "\n    " + stack.Split('\n')[0].Trim()
                    : string.Empty;
                Append(LogPath(index), $"{type}\t{first}{frame}\n");
            }
            else if (n == MaxSamplesPerSignature)
            {
                Append(LogPath(index), "    (further occurrences counted, not printed)\n");
            }

            // Rewrite the tally every time, so a crash still leaves exact counts.
            SessionState.SetString(CountsKey(index), Serialize(Seen));
        }

        private static void Append(string path, string text)
        {
            try { File.AppendAllText(path, text); } catch (IOException) { }
        }

        private static string Serialize(Dictionary<string, int> counts) =>
            string.Join(RecordSeparator.ToString(),
                counts.Select(kv => $"{kv.Key}{UnitSeparator}{kv.Value}"));

        private static Dictionary<string, int> Deserialize(string blob)
        {
            var result = new Dictionary<string, int>();
            if (string.IsNullOrEmpty(blob)) { return result; }
            foreach (string entry in blob.Split(RecordSeparator))
            {
                string[] parts = entry.Split(UnitSeparator);
                if (parts.Length == 2 && int.TryParse(parts[1], out int n)) { result[parts[0]] = n; }
            }
            return result;
        }

        /// <summary>
        /// Reads the live scene and records one line per fighter. Per fighter,
        /// because an aggregate cannot see ten broken bodies behind six working
        /// ones, which is a failure this project has already shipped once.
        /// </summary>
        private static void Snapshot(int index)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"scene\t{Scenes[index]}");
            sb.AppendLine($"timeScale\t{Fmt(Time.timeScale)}");
            sb.AppendLine($"fixedDeltaTime\t{Fmt(Time.fixedDeltaTime)}");
            sb.AppendLine($"gravity\t{Physics.gravity.ToString("0.##")}");

            var rigs = UnityEngine.Object.FindObjectsByType<Systems_FighterRig>(
                FindObjectsInactive.Include);
            sb.AppendLine($"fighters\t{rigs.Length}");

            foreach (var rig in rigs.OrderBy(r => r.name, StringComparer.Ordinal))
            {
                var agent = rig.GetComponentInParent<Agent_FighterBoxing>();
                if (agent == null) { agent = rig.GetComponentInChildren<Agent_FighterBoxing>(); }
                var bp = agent != null ? agent.GetComponent<BehaviorParameters>() : null;

                string model = bp != null && bp.Model != null ? bp.Model.name : "(none)";
                string behavior = bp != null ? bp.BehaviorType.ToString() : "(no BehaviorParameters)";
                int sensor = bp != null ? bp.BrainParameters.VectorObservationSize : -1;
                int expected = agent != null ? agent.ExpectedObservationCount : -1;

                var id = rig.GetComponentInParent<Systems_FighterIdentity>();
                string identity = id != null ? id.DisplayName : "(none)";

                float height = float.NaN;
                float upright = float.NaN;
                if (rig.Pelvis != null)
                {
                    height = rig.Pelvis.position.y - rig.GroundY;
                    upright = Vector3.Dot(rig.Pelvis.transform.up, Vector3.up);
                }

                sb.AppendLine(string.Join("\t",
                    "fighter",
                    rig.name,
                    identity,
                    $"joints={rig.JointCount}",
                    $"sensor={sensor}",
                    $"expected={expected}",
                    $"model={model}",
                    $"behavior={behavior}",
                    $"pelvisHeight={Fmt(height)}",
                    $"upright={Fmt(upright)}"));
            }

            // Nick is a contestant the ring referees through IContestFighter,
            // NOT a Systems_FighterRig -- he is a MuJoCo creature in another
            // assembly. The loop above cannot see him, and an earlier run of
            // this report listed five fighters in a scene that fields six.
            // Anything the referee accepts has to appear here or the report
            // repeats the very blind spot it exists to catch.
            var contestants = UnityEngine.Object
                .FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include)
                .OfType<IContestFighter>()
                .ToArray();
            sb.AppendLine($"contestants	{contestants.Length}");
            foreach (var c in contestants.OrderBy(c => c.DisplayName, StringComparer.Ordinal))
            {
                sb.AppendLine(string.Join("	",
                    "contestant",
                    c.GetType().Name,
                    c.DisplayName,
                    $"headAboveGround={Fmt(c.HeadHeightAboveGround)}",
                    $"reportsDown={c.ReportsDown}",
                    $"position={c.WorldPosition.ToString("0.##")}"));
            }

            File.WriteAllText(DataPath(index), sb.ToString());
        }

        private static string Fmt(float value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------
        // Report
        // ------------------------------------------------------------------

        private static string BuildReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Non-training scene smoke test");
            sb.AppendLine();
            sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `SceneTool_SmokeTest.RunAll`,");
            sb.AppendLine($"{SecondsPerScene:0} s of play mode per scene in the open Editor.");
            sb.AppendLine();
            sb.AppendLine("`sensor` is the width the VectorSensor was built at, `expected` is what");
            sb.AppendLine("`ComputeObservationCount` derives from the rig. They must agree.");
            sb.AppendLine();

            for (int i = 0; i < Scenes.Length; i++)
            {
                sb.AppendLine($"## {Path.GetFileNameWithoutExtension(Scenes[i])}");
                sb.AppendLine();

                var counts = Deserialize(SessionState.GetString(CountsKey(i), string.Empty));
                int warnings = counts.Where(kv => kv.Key.StartsWith("Warning", StringComparison.Ordinal))
                                     .Sum(kv => kv.Value);
                int errors = counts.Sum(kv => kv.Value) - warnings;
                sb.AppendLine($"Errors and exceptions: **{errors}**. Warnings: {warnings}.");
                sb.AppendLine();

                if (counts.Count > 0)
                {
                    sb.AppendLine("| count | type | message |");
                    sb.AppendLine("|---:|---|---|");
                    foreach (var kv in counts.OrderByDescending(kv => kv.Value).Take(25))
                    {
                        string[] parts = kv.Key.Split('\t');
                        string text = parts.Length > 1 ? parts[1] : kv.Key;
                        if (text.Length > 160) { text = text.Substring(0, 160) + "..."; }
                        sb.AppendLine($"| {kv.Value} | {parts[0]} | {text.Replace("|", "\\|")} |");
                    }
                    sb.AppendLine();
                }

                if (File.Exists(DataPath(i)))
                {
                    sb.AppendLine("```");
                    sb.AppendLine(File.ReadAllText(DataPath(i)).TrimEnd());
                    sb.AppendLine("```");
                }
                else
                {
                    sb.AppendLine("_No snapshot: the scene never reached the sample point._");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
