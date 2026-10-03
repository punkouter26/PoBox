using System;
using UnityEngine;
using PoBox.Broadcast;
using PoBox.Fx;
using PoBox.Mj;

namespace PoBox.Sim
{
    /// <summary>
    /// Puts the two chosen boxers in the ring.
    ///
    /// The arena holds every boxer twice, once dressed for each corner (prefabs of Assets/Boxers), all switched
    /// off, and one MuJoCo ring. Before anything else in the scene wakes up, this switches on the two that
    /// <see cref="MatchSelection"/> names (or the scene's default pair), introduces them to each other, hands
    /// them to the ring, and tells the bout and the cameras who is fighting. Everything after that carries on as if those two had always been the only
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

        public Boxer[] boxers = new Boxer[0];
        [Tooltip("The one MuJoCo ring: it simulates whichever two boxers are switched on.")]
        public MjRing ring;
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
            // The ring's model is made from what is switched on when it first steps: these two, and nothing of the rest.
            red.gameObject.SetActive(true);
            blue.gameObject.SetActive(true);
            if (ring != null)
            {
                ring.red = red.boxer;
                ring.blue = blue.boxer;
            }
            else Debug.LogError("[MatchSetup] the arena has no MjRing.", this);

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
