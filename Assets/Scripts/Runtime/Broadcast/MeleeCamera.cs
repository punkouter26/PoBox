using System.Collections.Generic;
using UnityEngine;
using PoBox.Sim;

namespace PoBox.Broadcast
{
    /// <summary>
    /// The free-for-all's one camera: looking down into the ring at a fixed angle and turning slowly round it, close
    /// in on where the fight is hottest. Every boxer carries heat that cools over <see cref="cooling"/> seconds:
    /// a punch thrown warms it a little, one landed warms the boxer who landed it and the one who took it, a
    /// knockdown most of all. The camera frames the hottest boxer and its opponent; with nothing happening it
    /// frames everybody still in; once it is over, the winner. It moves the camera it is on; the Melee scene has
    /// no Cinemachine shots, which are the two-boxer broadcast's.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class MeleeCamera : MonoBehaviour
    {
        public FreeForAll rules;
        [Tooltip("Degrees below the horizon.")]
        public float pitch = 40f;
        [Tooltip("Degrees a second round the ring.")]
        public float orbit = 4f;
        [Tooltip("Metres kept clear round the boxers framed.")]
        public float margin = 0.6f;
        [Tooltip("The nearest the camera comes, metres.")]
        public float minDistance = 2.6f;
        [Tooltip("Seconds to settle on a new framing.")]
        public float settle = 0.9f;
        [Tooltip("Height looked at above the canvas: about the chest.")]
        public float lookHeight = 1.1f;
        [Tooltip("Degrees the camera is tipped up after framing: the boxers sit lower in the picture, under the HUD's board.")]
        public float tiltUp = 2f;
        [Tooltip("The winner's shot: degrees below the horizon and the nearest it comes, metres. Steep and far enough to look over the ropes, not through them.")]
        public float winnerPitch = 55f, winnerDistance = 4f;
        [Header("Heat")]
        [Tooltip("Seconds for a boxer's heat to fall to a third.")]
        public float cooling = 2.5f;
        public float perThrow = 0.3f, perLanded = 1f, perKnockdown = 4f;
        [Tooltip("Heat under which nothing is happening, and the camera frames everybody still in.")]
        public float quiet = 0.6f;

        Camera _cam;
        float _yaw = -90f, _dist = 9f, _pitch = 40f;
        Vector3 _focus;
        bool _placed;
        readonly Dictionary<Fighter, float> _heat = new Dictionary<Fighter, float>();
        readonly List<Fighter> _framed = new List<Fighter>();

        void Awake() => _cam = GetComponent<Camera>();

        void OnEnable()
        {
            SimBus.PunchThrown += OnThrown;
            SimBus.Hit += OnHit;
            SimBus.Knockdown += OnDown;
        }

        void OnDisable()
        {
            SimBus.PunchThrown -= OnThrown;
            SimBus.Hit -= OnHit;
            SimBus.Knockdown -= OnDown;
        }

        void Warm(Fighter f, float by)
        {
            if (f == null) return;
            _heat.TryGetValue(f, out float h);
            _heat[f] = h + by;
        }

        void OnThrown(Fighter f, PunchType type, int hand) => Warm(f, perThrow);
        void OnHit(HitEvent e) { Warm(e.attacker, perLanded); Warm(e.victim, perLanded); }
        void OnDown(Fighter f, HitEvent cause) { Warm(f, perKnockdown); Warm(cause.attacker, perKnockdown); }

        void LateUpdate()
        {
            if (rules == null || rules.fighters.Length == 0) return;
            float k = Mathf.Exp(-Time.deltaTime / Mathf.Max(0.1f, cooling));
            foreach (Fighter f in rules.fighters) if (f != null && _heat.ContainsKey(f)) _heat[f] *= k;

            Frame();
            if (_framed.Count == 0) return;
            Vector3 sum = Vector3.zero;
            foreach (Fighter f in _framed) sum += f.pelvis.transform.position;
            Vector3 centre = sum / _framed.Count;
            float reach = 0f;
            foreach (Fighter f in _framed)
            {
                Vector3 d = f.pelvis.transform.position - centre;
                d.y = 0f;
                reach = Mathf.Max(reach, d.magnitude);
            }
            reach += margin;
            bool over = rules.Phase == FreeForAll.Stage.Over && rules.Winner != null;
            // The narrower of the two angles of view decides: on a portrait screen, the horizontal one.
            float halfV = 0.5f * _cam.fieldOfView * Mathf.Deg2Rad;
            float halfH = Mathf.Atan(Mathf.Tan(halfV) * _cam.aspect);
            float want = Mathf.Max(over ? winnerDistance : minDistance, reach / Mathf.Sin(Mathf.Min(halfV, halfH)));
            Vector3 focus = new Vector3(centre.x, centre.y - rules.fighters[0].standingPelvisHeight + lookHeight, centre.z);

            float s = _placed ? 1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(0.05f, settle)) : 1f;
            _focus = Vector3.Lerp(_focus, focus, s);
            _dist = Mathf.Lerp(_dist, want, s);
            _pitch = Mathf.MoveTowards(_pitch, over ? winnerPitch : pitch, Time.unscaledDeltaTime * 10f);
            _placed = true;
            _yaw += orbit * Time.unscaledDeltaTime;
            Quaternion look = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.SetPositionAndRotation(_focus - look * Vector3.forward * _dist, Quaternion.Euler(_pitch - tiltUp, _yaw, 0f));
        }

        /// <summary>Who is in the picture: the winner; else the hottest boxer and its opponent; else everybody still in.</summary>
        void Frame()
        {
            _framed.Clear();
            if (rules.Phase == FreeForAll.Stage.Over && rules.Winner != null && rules.Winner.pelvis != null) { _framed.Add(rules.Winner); return; }
            Fighter hottest = null;
            float most = quiet;
            foreach (Fighter f in rules.fighters)
                if (f != null && f.pelvis != null && !rules.IsOut(f) && _heat.TryGetValue(f, out float h) && h > most) { most = h; hottest = f; }
            if (hottest != null)
            {
                _framed.Add(hottest);
                if (hottest.opponent != null && hottest.opponent.pelvis != null) _framed.Add(hottest.opponent);
                return;
            }
            foreach (Fighter f in rules.fighters)
                if (f != null && f.pelvis != null && !rules.IsOut(f)) _framed.Add(f);
        }
    }
}
