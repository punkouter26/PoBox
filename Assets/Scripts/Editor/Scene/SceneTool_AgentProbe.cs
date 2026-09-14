using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoBox.Editor
{
    /// <summary>Read-only measurements of the actual contest; never creates or saves scenes.</summary>
    internal static class SceneTool_AgentProbe
    {
        private static object Read(object target, string field)
        {
            for (Type type = target?.GetType(); type != null; type = type.BaseType)
            {
                var info = type.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (info != null) { return info.GetValue(target); }
            }
            return null;
        }

        private static string Value(object value) => value is IFormattable number
            ? number.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "missing";

        [MenuItem("PoBox/Scene/Probe Active Contest")]
        public static void Snapshot()
        {
            var sb = new StringBuilder();
            string scene = SceneManager.GetActiveScene().name;
            sb.AppendLine($"scene\t{scene}\tutc={DateTime.UtcNow:O}\tplaying={Application.isPlaying}");
            sb.AppendLine($"clock\tsimulated={Value(Time.timeSinceLevelLoad)}\tscale={Value(Time.timeScale)}\tstep={Value(Time.fixedDeltaTime)}");
            var referee = UnityEngine.Object.FindFirstObjectByType<Systems_ContestReferee>();
            if (referee != null)
            {
                sb.AppendLine($"round\tstarted={referee.RoundsStarted}\tended={referee.RoundsEnded}\trestarts={referee.RestartsAutomatically}\ttime={Value(Read(referee, "_roundTime"))}");
                var entrants = (Read(referee, "_racers") ?? Read(referee, "_contestants")) as IEnumerable;
                if (entrants != null)
                {
                    foreach (var entrant in entrants)
                    {
                        sb.Append($"score\t{Read(entrant, "displayName")}");
                        foreach (string field in new[] { "startHeadHeight", "aliveTime", "travelled", "startProjection", "fallen", "finished", "finishTime" })
                        {
                            var value = Read(entrant, field);
                            if (value != null) { sb.Append($"\t{field}={Value(value)}"); }
                        }
                        sb.AppendLine();
                    }
                }
            }
            foreach (var rig in UnityEngine.Object.FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID))
            {
                var agent = rig.GetComponent<Agent_FighterBoxing>();
                var bp = rig.GetComponent<BehaviorParameters>();
                sb.AppendLine($"brain\t{rig.name}\tmodel={bp?.Model?.name ?? "none"}\tbehavior={bp?.BehaviorType}\tobservations={bp?.BrainParameters.VectorObservationSize}\texpected={agent?.ExpectedObservationCount}");
                var colliders = rig.GetComponentsInChildren<Collider>();
                int ignored = 0, pairs = 0;
                for (int a = 0; a < colliders.Length; a++)
                for (int b = a + 1; b < colliders.Length; b++)
                {
                    pairs++;
                    if (Physics.GetIgnoreCollision(colliders[a], colliders[b])) { ignored++; }
                }
                var bodies = rig.GetComponentsInChildren<Rigidbody>();
                sb.AppendLine($"physics\t{rig.name}\tbodies={bodies.Length}\tmass={Value(bodies.Sum(b => b.mass))}\tcolliders={colliders.Length}\tignoredSelfPairs={ignored}/{pairs}\tmaxAngularVelocity={Value(bodies.Select(b => b.maxAngularVelocity).DefaultIfEmpty().Max())}");
            }
            foreach (var behaviour in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.InstanceID))
            {
                if (behaviour is IContestFighter fighter)
                {
                    sb.AppendLine($"external\t{fighter.DisplayName}\theadHeight={Value(fighter.HeadHeightAboveGround)}\tdown={fighter.ReportsDown}\tposition={fighter.WorldPosition.ToString("F4", CultureInfo.InvariantCulture)}");
                }
                if (behaviour.GetType().Name != "CreatureSentisController") { continue; }
                sb.Append($"mujoco\t{behaviour.name}\tmodel={Read(behaviour, "_onnxModelAsset")}\tphysxColliders={behaviour.GetComponentsInChildren<Collider>().Length}");
                foreach (string property in new[] { "IsBound", "DebugHeadZ", "DebugResetCount", "DebugStepCount", "DebugFootLeftRestZ", "DebugFootRightRestZ", "CommandedDirection" })
                {
                    var info = behaviour.GetType().GetProperty(property);
                    if (info != null) { sb.Append($"\t{property}={Value(info.GetValue(behaviour))}"); }
                }
                sb.AppendLine();
            }
            Directory.CreateDirectory("Temp/agent-probes");
            string path = $"Temp/agent-probes/{scene}.txt";
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"Agent probe saved {path}\n{sb}");
        }
    }
}
