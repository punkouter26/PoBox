using PoBox.Sim;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PoBox.Fx
{
    /// <summary>
    /// A look for each part of the night, over the house look (the count's own grey is ArenaMood's):
    ///
    ///   walk-on   the edges down and a little more contrast: the boxers come out of the dark
    ///   replay    the instant replay (Mj/MjReplay) watched through a lens: the crowd out of focus behind the ring
    ///             (a Gaussian depth of field, its near edge kept just past the ring's middle), a touch of motion
    ///             blur, a cooler and duller picture, so it reads as a replay and not as the fight
    ///   result    warmer and brighter, the bloom up for the flashes
    ///
    /// Each is a global Volume (PoBox/Do All/Graphics makes them, with profiles in Assets/Settings/Look_*.asset)
    /// whose weight is eased to 1 while its moment lasts and back to 0 after; at 0 a volume costs nothing.
    /// </summary>
    public class PhaseLook : MonoBehaviour
    {
        public Bout bout;
        public Volume walkOn, replay, result;
        [Tooltip("Seconds to come in and to go out.")]
        public float fade = 0.4f;
        [Tooltip("How far past the ring's middle the replay's blur begins, and how far beyond that it is full, metres.")]
        public float focusPast = 1.5f, blurOver = 8f;
        public Vector3 ringCentre = new Vector3(0f, 1f, 0f);

        DepthOfField _dof;

        void Start()
        {
            // The volume's own copy of the profile (Volume.profile), so moving the focus never touches the asset.
            if (replay != null && replay.sharedProfile != null) replay.profile.TryGet(out _dof);
        }

        void Update()
        {
            BoutPhase phase = bout != null ? bout.Phase : BoutPhase.Fight;
            bool replaying = Mj.MjReplay.Playing;
            float step = Time.unscaledDeltaTime / Mathf.Max(0.01f, fade);
            Ease(walkOn, phase == BoutPhase.WalkOn, step);
            Ease(replay, replaying, step);
            Ease(result, phase == BoutPhase.Results && !replaying, step);

            // The replay's focus: just past the middle of the ring from wherever the camera is.
            Camera cam = Camera.main;
            if (_dof != null && cam != null && replay != null && replay.weight > 0f)
            {
                float d = Vector3.Distance(cam.transform.position, ringCentre);
                _dof.gaussianStart.value = d + focusPast;
                _dof.gaussianEnd.value = d + focusPast + blurOver;
            }
        }

        static void Ease(Volume v, bool on, float step)
        {
            if (v == null) return;
            v.weight = Mathf.MoveTowards(v.weight, on ? 1f : 0f, step);
            bool live = v.weight > 0f;
            if (v.enabled != live) v.enabled = live;
        }
    }
}
