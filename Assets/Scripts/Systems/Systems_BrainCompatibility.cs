using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Whether a trained brain reads the same observation vector a fighter
    /// emits.
    ///
    /// This is the project's one silent failure mode and the reason it needs a
    /// home of its own. ML-Agents compares a model's shape to the fighter's
    /// BrainParameters ONLY from the BehaviorParameters inspector; its runtime
    /// path checks the model version and nothing else. Every brain this project
    /// assigns is assigned from code, so a mismatch produces no error, no
    /// warning and no console line at all — the policy simply reads a vector
    /// that is shifted from the first differing observation onward, and every
    /// number after it means something other than it did in training.
    /// Measured 2026-08-20: the balance roster ran 119-observation brains on
    /// 121-observation fighters in total silence.
    ///
    /// Extracted from Systems_ContestSpawner so the offline evaluation harness
    /// makes the same call the shipping spawner does. An evaluator that will
    /// happily benchmark a mismatched brain is worse than no evaluator: it
    /// reports a number, and the number is meaningless.
    /// </summary>
    public static class Systems_BrainCompatibility
    {
        private const string OBSERVATION_INPUT_NAME = "obs_0";

        // Cached: a contest spawns up to eight fighters off the same two or three
        // ModelAssets, and deserializing one is not free.
        private static readonly Dictionary<ModelAsset, int> ObservationWidths = new();

        /// <summary>
        /// Width of <paramref name="modelAsset"/>'s obs_0 input, or -1 when it
        /// cannot be read. -1 means "unknown", never "incompatible".
        /// </summary>
        public static int ObservationWidth(ModelAsset modelAsset)
        {
            if (modelAsset == null)
            {
                return -1;
            }
            if (ObservationWidths.TryGetValue(modelAsset, out int cached))
            {
                return cached;
            }
            int width = -1;
            Model model = ModelLoader.Load(modelAsset);
            for (int inputIndex = 0; inputIndex < model.inputs.Count; inputIndex++)
            {
                Model.Input input = model.inputs[inputIndex];
                if (input.name != OBSERVATION_INPUT_NAME || input.shape.isRankDynamic || input.shape.rank != 2)
                {
                    continue;
                }
                width = input.shape.Get(1);
                break;
            }
            ObservationWidths[modelAsset] = width;
            return width;
        }

        /// <summary>
        /// True when <paramref name="modelAsset"/> was trained on the same
        /// observation width <paramref name="sensorSize"/> describes, or when
        /// the width cannot be read. Logs and returns false otherwise.
        ///
        /// Deliberately NOT [Conditional]: this decides whether the brain runs
        /// at all, so a player build has to make the same call an editor run
        /// does.
        /// </summary>
        public static bool Accept(ModelAsset modelAsset, string instanceName, int sensorSize)
        {
            int modelSize = ObservationWidth(modelAsset);
            if (modelSize < 0 || modelSize == sensorSize)
            {
                return true;
            }
            Debug.LogError($"{instanceName}: brain '{modelAsset.name}' expects {modelSize} observations but " +
                $"this fighter emits {sensorSize}, so it would read a shifted vector. Falling back to the " +
                "heuristic bot — export a brain trained on this layout, or point the roster entry at one " +
                "that matches.");
            return false;
        }
    }
}
