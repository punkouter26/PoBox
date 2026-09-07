using System;
using System.IO;
using UnityEngine;

namespace PoBox.MuJoCoCreature
{
    /// <summary>
    /// The C# half of the observation parity harness. Tools/MuJoCo/parity_check.py
    /// writes a state (qpos, qvel, command) to Temp/nick_parity_in.json; this
    /// component loads it into the live MjScene, asks the controller for the
    /// observation vector it would feed the brain, and writes that to
    /// Temp/nick_parity_out.json. The Python side builds the same vector from
    /// the same state on CPU MuJoCo and diffs element-wise.
    ///
    /// This is the only test that can tell the two builders apart: a shape
    /// match proves nothing, because a wrong term is the same width as a
    /// right one. Added to the demo scene at run time by
    /// RigTool_NickMuJoCo.RunParityProbe; never saved into a scene.
    /// </summary>
    public sealed class Systems_NickParityProbe : MonoBehaviour
    {
        private const string InputPath = "Temp/nick_parity_in.json";
        private const string OutputPath = "Temp/nick_parity_out.json";

        [Serializable]
        private sealed class Input
        {
            public double[] qpos;
            public double[] qvel;
            public float speed;
            public float dirX;
            public float dirY;
        }

        [Serializable]
        private sealed class Output
        {
            public int observations;
            public bool locomotionCommand;
            public int decimation;
            public float[] obs;
            public float pelvisX;
            public float pelvisY;
            public float pelvisZ;
        }

        [SerializeField] private CreatureSentisController _controller;
        private bool _done;

        private void FixedUpdate()
        {
            if (_done) { return; }
            if (_controller == null) { _controller = FindFirstObjectByType<CreatureSentisController>(); }
            if (_controller == null || !_controller.IsBound) { return; }
            if (!File.Exists(InputPath))
            {
                Debug.LogError($"NICK_PARITY: no {InputPath}; run Tools/MuJoCo/parity_check.py.");
                _done = true;
                return;
            }

            var input = JsonUtility.FromJson<Input>(File.ReadAllText(InputPath));
            _controller.DebugSetState(input.qpos, input.qvel);
            _controller.SetCommand(input.speed, new Vector3(input.dirX, input.dirY, 0f));
            float[] obs = _controller.DebugGatherObservations();
            var output = new Output
            {
                observations = _controller.ObservationCount,
                locomotionCommand = _controller.ObservesLocomotionCommand,
                decimation = _controller.Decimation,
                obs = obs,
                pelvisX = _controller.DebugPelvisPosition.x,
                pelvisY = _controller.DebugPelvisPosition.y,
                pelvisZ = _controller.DebugPelvisPosition.z,
            };
            File.WriteAllText(OutputPath, JsonUtility.ToJson(output));
            Debug.Log($"NICK_PARITY wrote {OutputPath}: {obs.Length} observations, " +
                      $"locomotionCommand={output.locomotionCommand}");
            _done = true;
        }
    }
}
