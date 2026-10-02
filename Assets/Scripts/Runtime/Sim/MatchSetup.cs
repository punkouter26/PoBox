using System;
using UnityEngine;
using PoBox.Broadcast;
using PoBox.Fx;
using PoBox.Rl;

namespace PoBox.Sim
{
    /// <summary>
    /// Puts the two chosen boxers in the ring.
    ///
    /// The arena holds every trained boxer twice, once dressed for each corner, all switched off, and one
    /// MuJoCo ring for every pair of them that has a match model. Before anything else in the scene wakes up, this
    /// switches on the two that <see cref="MatchSelection"/> names (or the scene's default pair), introduces
    /// them to each other, switches on the ring that simulates that pair, and tells the bout and the
    /// cameras who is fighting. Everything after that carries on as if those two had always been the only
    /// fighters in the scene.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class MatchSetup : MonoBehaviour
    {
        [Serializable]
        public class Boxer
        {
            public string name;
            [Tooltip("This boxer dressed for the red corner, and for the blue one.")]
            public Fighter red, blue;
        }

        [Serializable]
        public class Pairing
        {
            [Tooltip("The two names, in the order the MuJoCo model has them.")]
            public string a, b;
            public MujocoRing ring;
        }

        public Boxer[] boxers = new Boxer[0];
        public Pairing[] rings = new Pairing[0];
        [Tooltip("Who fights when the arena is opened without the menu.")]
        public string defaultRed, defaultBlue;
        public Bout bout;
        public BroadcastDirector director;

        void Awake()
        {
            Boxer r = Find(MatchSelection.Chosen ? MatchSelection.Red : defaultRed) ?? Find(defaultRed);
            Boxer b = Find(MatchSelection.Chosen ? MatchSelection.Blue : defaultBlue) ?? Find(defaultBlue);
            if (r == null || b == null || r.red == null || b.blue == null)
            {
                Debug.LogError("[MatchSetup] the arena has no boxers to put in the ring.", this);
                return;
            }
            Fighter red = r.red, blue = b.blue;

            red.opponent = blue;
            blue.opponent = red;
            var redBrain = red.GetComponent<PolicyBrain>();
            var blueBrain = blue.GetComponent<PolicyBrain>();
            if (redBrain != null) redBrain.opponent = blue.mjcf;
            if (blueBrain != null) blueBrain.opponent = red.mjcf;
            red.gameObject.SetActive(true);
            blue.gameObject.SetActive(true);

            // The ring wakes after the fighters: it takes their bodies over as it loads.
            MujocoRing ring = null;
            foreach (Pairing p in rings)
            {
                if (p.ring == null) continue;
                if (p.a == r.name && p.b == b.name) p.ring.rigs = new[] { red.mjcf, blue.mjcf };
                else if (p.a == b.name && p.b == r.name) p.ring.rigs = new[] { blue.mjcf, red.mjcf };
                else continue;
                ring = p.ring;
                break;
            }
            if (ring != null) ring.gameObject.SetActive(true);
            else Debug.LogWarning($"[MatchSetup] no MuJoCo match model for {r.name} v {b.name}; they will run on Unity's physics, where these policies fall over.", this);

            if (bout != null)
            {
                bout.red = red;
                bout.blue = blue;
            }
            if (director != null)
            {
                director.redLive = red.GetComponent<FighterSkin>();
                director.blueLive = blue.GetComponent<FighterSkin>();
            }
        }

        Boxer Find(string name)
        {
            foreach (Boxer x in boxers) if (x.name == name) return x;
            return null;
        }
    }
}
