using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Runs a baked .onnx through a training scene with no trainer attached and
    /// writes the same metrics the trainer would have seen, split by body.
    ///
    /// WHY THIS EXISTS. Reward_Locomotion's numbers only ever reach TensorBoard,
    /// and TensorBoard only receives them while mlagents-learn is on the other
    /// end of the socket. So a FINISHED brain — the only kind a shipping
    /// decision is ever about — produced no measurements at all, and every
    /// promotion in this project's history was argued from one run's training
    /// curve against a different generation's training curve, or from numbers
    /// re-measured by hand in the Editor. Gen 20's SOURCE.txt says so outright:
    /// it had to invent two ad-hoc protocols, and it records that neither is
    /// comparable to the table in its own config header.
    ///
    /// This makes the comparison like-for-like by construction. Two brains are
    /// measured in the same scene, on the same three bodies, at the same
    /// commanded speed, for the same number of episodes, through the same
    /// reward code that produced every historical number.
    ///
    /// It installs itself from the command line, so the training env build
    /// serves as the evaluation build unchanged and there is no second scene to
    /// keep in sync:
    ///
    ///   PoBoxTrain.exe -batchmode -nographics -evalBrain Locomotion_gen20
    ///                  -evalEpisodes 4 -evalSpeed 0 -evalOutput report.json
    ///
    /// -evalBrain heuristic benchmarks the code-driven PD bot instead, which is
    /// the floor every policy has to clear to be worth shipping.
    /// </summary>
    public sealed class Systems_EvalHarness : MonoBehaviour
    {
        private const string BRAIN_RESOURCE_FOLDER = "EvalBrains/";
        private const string HEURISTIC = "heuristic";
        // The behaviour name every fighter's BehaviorParameters carries, and the
        // key the trained models were exported under.
        private const string BEHAVIOR_NAME = "Boxer";
        // A stuck run must end by itself: this is launched from a script that
        // waits on the process, and a player that never quits stalls the whole
        // pipeline behind it.
        private const float WALL_CLOCK_LIMIT_SECONDS = 900f;

        [Serializable]
        public sealed class BodyResult
        {
            public string body;
            public int fighters;
            public int episodes;
            public float stepsBetweenFalls;
            public float uprightFraction;
            public float alternation;
            public float stepsSurvived;
            public float singleSupportMean;
            public float clearanceMean;
            public float speedMatchMean;
            public float stumbles;
            public float bestRunDistance;
            public float footLeftGrounded;
            public float footRightGrounded;
            public float footLeftLift;
            public float footRightLift;
        }

        [Serializable]
        public sealed class Report
        {
            public string brain;
            public float speedCommandMax;
            public int episodesPerFighter;
            public int observationWidth;
            public bool shove;
            public string scene;
            public BodyResult[] bodies;
        }

        private sealed class Accumulator
        {
            public int Fighters;
            public int Episodes;
            public double StepsBetweenFalls;
            public double UprightFraction;
            public double Alternation;
            public double StepsSurvived;
            public double SingleSupport;
            public double Clearance;
            public double SpeedMatch;
            public double Stumbles;
            public double BestRunDistance;
            public double FootLeftGrounded;
            public double FootRightGrounded;
            public double FootLeftLift;
            public double FootRightLift;
        }

        private Reward_Locomotion[] _rewards;
        private int[] _seenEpisodes;
        private readonly Dictionary<string, Accumulator> _byBody = new();
        private string _brainName;
        private string _outputPath;
        private float _speedCommandMax;
        private int _targetEpisodes;
        private int _observationWidth = -1;
        private float _startTime;
        private bool _finished;
        private bool _diagnoseFeet;
        private bool _shove;
        private bool _diagnosed;

        /// <summary>
        /// Installs the harness only when the command line asks for it, so the
        /// same player binary is both the training env and the evaluator.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallFromCommandLine()
        {
            if (string.IsNullOrEmpty(Argument("-evalBrain")))
            {
                return;
            }
            var host = new GameObject("EvalHarness");
            host.AddComponent<Systems_EvalHarness>();
        }

        private void Start()
        {
            _brainName = Argument("-evalBrain");
            _outputPath = Argument("-evalOutput") ?? "eval_report.json";
            _targetEpisodes = ParseInt(Argument("-evalEpisodes"), 3);
            _speedCommandMax = ParseFloat(Argument("-evalSpeed"), 0f);
            Time.timeScale = ParseFloat(Argument("-evalTimeScale"), 20f);
            _diagnoseFeet = Argument("-evalDiagnoseFeet") != null;
            // The balance ring runs hazards and shoves; SCN_TRAIN_LOCOMOTION
            // disables the shover because it fights the walking signal. So the
            // default benchmark measures UNDISTURBED standing, which is not the
            // question the balance contest asks. -evalShove turns the per-agent
            // shover back on so a brain can be scored on the thing it will
            // actually face, without needing a second scene or a second build.
            _shove = Argument("-evalShove") != null;
            _startTime = Time.realtimeSinceStartup;

            _rewards = FindObjectsByType<Reward_Locomotion>(FindObjectsSortMode.None);
            if (_rewards.Length == 0)
            {
                Debug.LogError("EvalHarness: no Reward_Locomotion in the scene — nothing to measure.");
                Quit(1);
                return;
            }
            _seenEpisodes = new int[_rewards.Length];

            ModelAsset model = null;
            bool heuristic = string.Equals(_brainName, HEURISTIC, StringComparison.OrdinalIgnoreCase);
            if (!heuristic)
            {
                model = Resources.Load<ModelAsset>(BRAIN_RESOURCE_FOLDER + _brainName);
                if (model == null)
                {
                    Debug.LogError($"EvalHarness: no brain '{_brainName}' under Resources/{BRAIN_RESOURCE_FOLDER}. " +
                        "Build the eval player with Build_EvalEnv, which stages Assets/Agents/**/*.onnx there.");
                    Quit(1);
                    return;
                }
                _observationWidth = Systems_BrainCompatibility.ObservationWidth(model);
            }

            for (int rewardIndex = 0; rewardIndex < _rewards.Length; rewardIndex++)
            {
                Reward_Locomotion reward = _rewards[rewardIndex];
                reward.SetSpeedCommandMax(_speedCommandMax);

                var shover = reward.GetComponent<Systems_Shover>();
                if (shover != null)
                {
                    shover.enabled = _shove;
                }

                var agent = reward.GetComponent<Agent_FighterBoxing>();
                var behavior = reward.GetComponent<BehaviorParameters>();
                if (agent == null || behavior == null)
                {
                    continue;
                }
                int sensorSize = behavior.BrainParameters.VectorObservationSize;
                // Refused, not merely reported, exactly as the contest spawner
                // refuses it. Benchmarking a brain that reads a shifted vector
                // would produce a number, and the number would be a lie.
                if (heuristic || model == null ||
                    !Systems_BrainCompatibility.Accept(model, reward.name, sensorSize))
                {
                    behavior.BehaviorType = BehaviorType.HeuristicOnly;
                    continue;
                }
                agent.SetModel(BEHAVIOR_NAME, model, InferenceDevice.Default);
                behavior.BehaviorType = BehaviorType.InferenceOnly;
            }

            Debug.Log($"EvalHarness: '{_brainName}' on {_rewards.Length} fighters, " +
                $"{_targetEpisodes} episodes each, commanded speed max {_speedCommandMax}, " +
                $"shove {(_shove ? "ON" : "off")}.");
        }

        private void Update()
        {
            if (_finished)
            {
                return;
            }
            // Once, after the pose has settled under gravity but long before any
            // policy has done anything interesting.
            // SCALED time, not wall clock: the harness runs at timeScale 20, so a
            // single 3000-step episode is over in about three REAL seconds and a
            // wall-clock trigger of five never fired at all. Four scaled seconds
            // is 200 fixed steps -- long enough for the pose to settle, short
            // enough to land inside the first episode at any episode count.
            if (_diagnoseFeet && !_diagnosed && Time.timeSinceLevelLoad > 4f)
            {
                _diagnosed = true;
                for (int rewardIndex = 0; rewardIndex < _rewards.Length; rewardIndex++)
                {
                    Debug.Log("FOOT_DIAG " + _rewards[rewardIndex].DescribeFeet());
                }
            }
            int slowest = int.MaxValue;
            for (int rewardIndex = 0; rewardIndex < _rewards.Length; rewardIndex++)
            {
                Reward_Locomotion reward = _rewards[rewardIndex];
                int completed = reward.EpisodesCompleted;
                while (_seenEpisodes[rewardIndex] < completed)
                {
                    // Only the most recent episode is retained, so one poll per
                    // frame can miss earlier ones. Episodes are 3000 fixed steps
                    // long and frames are far more frequent than that, so this
                    // loop runs at most once in practice; counting the skipped
                    // ones keeps the episode tally honest if it ever does not.
                    _seenEpisodes[rewardIndex]++;
                    if (_seenEpisodes[rewardIndex] == completed)
                    {
                        Record(reward);
                    }
                }
                slowest = Mathf.Min(slowest, completed);
            }

            if (slowest >= _targetEpisodes)
            {
                WriteReport();
                Quit(0);
                return;
            }
            if (Time.realtimeSinceStartup - _startTime > WALL_CLOCK_LIMIT_SECONDS)
            {
                Debug.LogWarning("EvalHarness: wall-clock limit reached with the slowest fighter on " +
                    $"episode {slowest} of {_targetEpisodes}. Reporting what completed.");
                WriteReport();
                Quit(0);
            }
        }

        private void Record(Reward_Locomotion reward)
        {
            string body = string.IsNullOrEmpty(reward.BodyName) ? "Unlabelled" : reward.BodyName;
            if (!_byBody.TryGetValue(body, out Accumulator accumulator))
            {
                accumulator = new Accumulator();
                _byBody[body] = accumulator;
            }
            Reward_Locomotion.EpisodeSummary episode = reward.LastEpisode;
            accumulator.Episodes++;
            accumulator.StepsBetweenFalls += episode.StepsBetweenFalls;
            accumulator.UprightFraction += episode.UprightFraction;
            accumulator.Alternation += episode.Alternation;
            accumulator.StepsSurvived += episode.StepsSurvived;
            accumulator.SingleSupport += episode.SingleSupportMean;
            accumulator.Clearance += episode.ClearanceMean;
            accumulator.SpeedMatch += episode.SpeedMatchMean;
            accumulator.Stumbles += episode.Stumbles;
            accumulator.BestRunDistance += episode.BestRunDistance;
            accumulator.FootLeftGrounded += episode.FootLeftGrounded;
            accumulator.FootRightGrounded += episode.FootRightGrounded;
            accumulator.FootLeftLift += episode.FootLeftLift;
            accumulator.FootRightLift += episode.FootRightLift;
        }

        private void WriteReport()
        {
            _finished = true;
            // Fighters per body, counted from the scene rather than from the
            // episodes recorded, so a body that produced none is still visible
            // in the report as a body with zero episodes instead of vanishing.
            var fighterCounts = new Dictionary<string, int>();
            for (int rewardIndex = 0; rewardIndex < _rewards.Length; rewardIndex++)
            {
                string body = string.IsNullOrEmpty(_rewards[rewardIndex].BodyName)
                    ? "Unlabelled" : _rewards[rewardIndex].BodyName;
                fighterCounts.TryGetValue(body, out int count);
                fighterCounts[body] = count + 1;
                if (!_byBody.ContainsKey(body))
                {
                    _byBody[body] = new Accumulator();
                }
            }

            var results = new List<BodyResult>();
            var overall = new Accumulator();
            foreach (KeyValuePair<string, Accumulator> pair in _byBody)
            {
                Accumulator accumulator = pair.Value;
                fighterCounts.TryGetValue(pair.Key, out int fighters);
                results.Add(ToResult(pair.Key, fighters, accumulator));
                overall.Episodes += accumulator.Episodes;
                overall.Fighters += fighters;
                overall.StepsBetweenFalls += accumulator.StepsBetweenFalls;
                overall.UprightFraction += accumulator.UprightFraction;
                overall.Alternation += accumulator.Alternation;
                overall.StepsSurvived += accumulator.StepsSurvived;
                overall.SingleSupport += accumulator.SingleSupport;
                overall.Clearance += accumulator.Clearance;
                overall.SpeedMatch += accumulator.SpeedMatch;
                overall.Stumbles += accumulator.Stumbles;
                overall.BestRunDistance += accumulator.BestRunDistance;
                overall.FootLeftGrounded += accumulator.FootLeftGrounded;
                overall.FootRightGrounded += accumulator.FootRightGrounded;
                overall.FootLeftLift += accumulator.FootLeftLift;
                overall.FootRightLift += accumulator.FootRightLift;
            }
            results.Sort((left, right) => string.CompareOrdinal(left.body, right.body));
            results.Insert(0, ToResult("ALL", overall.Fighters, overall));

            var report = new Report
            {
                brain = _brainName,
                speedCommandMax = _speedCommandMax,
                episodesPerFighter = _targetEpisodes,
                observationWidth = _observationWidth,
                shove = _shove,
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                bodies = results.ToArray()
            };
            string json = JsonUtility.ToJson(report, prettyPrint: true);
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(_outputPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(_outputPath, json);
                Debug.Log($"EvalHarness: wrote {_outputPath}");
            }
            catch (IOException exception)
            {
                Debug.LogError($"EvalHarness: could not write {_outputPath}: {exception.Message}");
            }
            // Also to the log, so a run whose file write fails is not a total
            // loss and so the numbers are greppable from the player log.
            Debug.Log("EVAL_REPORT_JSON " + JsonUtility.ToJson(report));
        }

        private static BodyResult ToResult(string body, int fighters, Accumulator accumulator)
        {
            float divisor = Mathf.Max(1, accumulator.Episodes);
            return new BodyResult
            {
                body = body,
                fighters = fighters,
                episodes = accumulator.Episodes,
                stepsBetweenFalls = (float)(accumulator.StepsBetweenFalls / divisor),
                uprightFraction = (float)(accumulator.UprightFraction / divisor),
                alternation = (float)(accumulator.Alternation / divisor),
                stepsSurvived = (float)(accumulator.StepsSurvived / divisor),
                singleSupportMean = (float)(accumulator.SingleSupport / divisor),
                clearanceMean = (float)(accumulator.Clearance / divisor),
                speedMatchMean = (float)(accumulator.SpeedMatch / divisor),
                stumbles = (float)(accumulator.Stumbles / divisor),
                bestRunDistance = (float)(accumulator.BestRunDistance / divisor),
                footLeftGrounded = (float)(accumulator.FootLeftGrounded / divisor),
                footRightGrounded = (float)(accumulator.FootRightGrounded / divisor),
                footLeftLift = (float)(accumulator.FootLeftLift / divisor),
                footRightLift = (float)(accumulator.FootRightLift / divisor)
            };
        }

        private static void Quit(int exitCode)
        {
            Application.Quit(exitCode);
        }

        /// <summary>Value following <paramref name="flag"/>, or null.</summary>
        private static string Argument(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int argIndex = 0; argIndex < args.Length - 1; argIndex++)
            {
                if (string.Equals(args[argIndex], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return args[argIndex + 1];
                }
            }
            return null;
        }

        private static int ParseInt(string text, int fallback)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value : fallback;
        }

        private static float ParseFloat(string text, float fallback)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value : fallback;
        }
    }
}
