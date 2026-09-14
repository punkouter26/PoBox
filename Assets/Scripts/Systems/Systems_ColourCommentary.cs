using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// The second voice in the booth: a lower third that says WHY a fighter is
    /// about to lose, from telemetry the audience has no other way to read.
    ///
    /// <see cref="Systems_Announcer"/> is the play-by-play and it is purely
    /// event-driven — round start, a fall, a hazard, a save. Those are the
    /// moments a spectator can already see. Everything that makes an
    /// active-ragdoll contest actually interesting happens in the three seconds
    /// BEFORE the fall, in numbers that currently reach only the joint-stress
    /// glow: an ankle pinned at its force ceiling, a lean that is not being
    /// corrected, feet that have not moved in eight seconds. This turns those
    /// into sentences.
    ///
    /// IT SHARES ONE DEFINITION OF EFFORT WITH THE REST OF THE PROJECT.
    /// Strain is <see cref="Systems_Stamina.JointLoad01"/> — the same
    /// min(maxForce, spring x |w|) / maxForce the heatmap draws and stamina
    /// drains from, never the action vector. An action is a request; a policy
    /// that saturates its actions against a pinned limb is doing no work at
    /// all, and commentary built on the action vector would confidently
    /// describe effort that is not happening.
    ///
    /// SIDES COME FROM GEOMETRY, NOT FROM NAMES. Which of a fighter's legs is
    /// the left one is decided by the sign of the part's x in pelvis space.
    /// The capsule calls its parts ShinL/ShinR and the imported characters do
    /// not — <see cref="Sensor_GroundContact"/> records what that naming
    /// asymmetry already cost this project once — so anything that says "left
    /// ankle" out loud had better not be reading a suffix. Only the JOINT WORD
    /// (hip, knee, ankle) comes from the name, and an unrecognised part is
    /// called a joint rather than guessed at.
    ///
    /// TWO CLOCKS, ON PURPOSE. Streaks accumulate on SCALED time and the label
    /// fades on UNSCALED time. The round countdown parks Time.timeScale at 0
    /// for 2.9 s and the knockout dips it to 0.35; on unscaled time a frozen
    /// ring would manufacture a six-second strain streak out of a body that is
    /// not moving, and on scaled time the line already on screen would hang
    /// there through the freeze.
    ///
    /// Created at runtime by <see cref="Systems_ContestSpawner"/> and borrows
    /// the referee's UIDocument, for the same reason the hazard chip and the
    /// tale of the tape do: a second UIDocument is a second panel, and two
    /// panels put two HUD stacks at the same coordinates.
    /// Test-scene harness only.
    /// </summary>
    public sealed class Systems_ColourCommentary : MonoBehaviour
    {
        /// <summary>How long one line stays up, and how long it takes to fade.</summary>
        private const float LINE_SECONDS = 4.5f;
        private const float FADE_SECONDS = 0.6f;

        /// <summary>
        /// Silence between lines. A colour commentator who never stops talking
        /// stops being information and becomes wallpaper, so the booth is quiet
        /// for at least this long after every line finishes.
        /// </summary>
        private const float GLOBAL_COOLDOWN = 2f;

        /// <summary>
        /// How long before the same observation about the same fighter may be
        /// made again. Without it a leaning fighter produces one line per frame
        /// for as long as it leans.
        /// </summary>
        private const float REPEAT_COOLDOWN = 14f;

        /// <summary>
        /// Top of the band, as a fraction of panel height. The occupied bands
        /// above it are the announcer's callout (20%), the winner banner (32%),
        /// the countdown numerals (42%) and the tale of the tape (57%); below it
        /// the referee's plate row is pinned 56 px off the bottom and grows
        /// UPWARD as it wraps. Two lines here clear a two-row plate wrap on a
        /// 1920 px panel. A full eight-fighter ring wrapping to three rows is
        /// the case that would touch, and the card below is only ever up during
        /// the countdown, when this is suppressed anyway.
        /// </summary>
        private const float BAND_TOP_PERCENT = 70f;

        // --- detector thresholds ---------------------------------------------

        /// <summary>Fraction of a joint's force ceiling that counts as strained.</summary>
        private const float STRAIN_LOAD = 0.78f;

        /// <summary>How long it has to stay there before it is worth saying.</summary>
        private const float STRAIN_SECONDS = 2.5f;

        /// <summary>Smoothing rate for the load, matching the heatmap's.</summary>
        private const float LOAD_SMOOTH_RATE = 8f;

        /// <summary>Degrees off vertical, and how long, before the torso is leaning.</summary>
        private const float LEAN_DEGREES = 24f;
        private const float LEAN_SECONDS = 2f;

        /// <summary>Head height, as a fraction of standing, that counts as sinking.</summary>
        private const float SINK_FRACTION = 0.8f;
        private const float SINK_SECONDS = 1.2f;

        /// <summary>How long both feet stay planted before the footwork is frozen.</summary>
        private const float FROZEN_SECONDS = 7f;

        /// <summary>Decay window and count for the busy-footwork tell.</summary>
        private const float SHUFFLE_WINDOW = 4f;
        private const float SHUFFLE_SWITCHES = 5f;

        /// <summary>
        /// Priority above which a line is worth a camera cut. The drama
        /// director decides whether to honour it; this only nominates.
        /// </summary>
        private const float CUE_PRIORITY = 0.62f;
        private const float CUE_SECONDS = 2.6f;

        private const float STANDING_HEAD_FRACTION = 0.55f;

        /// <summary>One kind of observation. Cooldowns are kept per fighter per kind.</summary>
        private enum Tell { Strain, Lean, Sink, Frozen, Shuffle }

        private const int TELL_COUNT = 5;

        private Systems_ContestReferee _contest;
        private Systems_Announcer _announcer;
        private Systems_DramaCamera _camera;
        private Systems_FighterRig[] _rigs;

        private float[] _startHeadHeights;
        private float[][] _smoothedLoad;
        private float[][] _strainSeconds;
        private float[] _leanSeconds;
        private float[] _sinkSeconds;
        private float[] _plantedSeconds;
        private float[] _switchScore;
        private bool[] _leftDown;
        private bool[] _rightDown;
        private Sensor_GroundContact[] _leftShin;
        private Sensor_GroundContact[] _rightShin;
        private float[,] _repeatCooldown;

        /// <summary>
        /// Identities resolved once. <see cref="Systems_FighterIdentity.Resolve"/>
        /// is a GetComponent, the detectors run for every fighter every frame,
        /// and neither a name nor a plate colour changes after the spawner has
        /// set them.
        /// </summary>
        private string[] _names;
        private Color[] _plateColors;

        private Label _line;
        private float _lineRemaining;
        private float _globalCooldown;

        /// <summary>Rotates the wording so a repeated tell is not a repeated sentence.</summary>
        private int _variant;
        private bool _ready;

        /// <summary>
        /// Turns the booth off without destroying it, for a HUD toggle or a
        /// clean winner tableau. Matches <see cref="Systems_JointStressView"/>.
        /// </summary>
        public bool Speaking { get; set; } = true;

        private void Start()
        {
            _contest = FindFirstObjectByType<Systems_ContestReferee>();
            if (_contest == null)
            {
                return;
            }
            var document = _contest.GetComponent<UIDocument>();
            if (document == null)
            {
                return;
            }
            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            if (_rigs.Length == 0)
            {
                return;
            }
            _announcer = FindFirstObjectByType<Systems_Announcer>();
            _camera = FindFirstObjectByType<Systems_DramaCamera>();

            BuildBand(document.rootVisualElement);
            AllocateState();

            _contest.RoundStarted += OnRoundStarted;
            _ready = true;
        }

        private void OnDestroy()
        {
            if (_contest != null)
            {
                _contest.RoundStarted -= OnRoundStarted;
            }
        }

        private void AllocateState()
        {
            int count = _rigs.Length;
            _startHeadHeights = new float[count];
            _smoothedLoad = new float[count][];
            _strainSeconds = new float[count][];
            _leanSeconds = new float[count];
            _sinkSeconds = new float[count];
            _plantedSeconds = new float[count];
            _switchScore = new float[count];
            _leftDown = new bool[count];
            _rightDown = new bool[count];
            _leftShin = new Sensor_GroundContact[count];
            _rightShin = new Sensor_GroundContact[count];
            _repeatCooldown = new float[count, TELL_COUNT];
            _names = new string[count];
            _plateColors = new Color[count];

            for (int rigIndex = 0; rigIndex < count; rigIndex++)
            {
                Systems_FighterRig rig = _rigs[rigIndex];
                _startHeadHeights[rigIndex] = rig.Head.position.y - rig.GroundY;
                int joints = rig.Joints.Count;
                _smoothedLoad[rigIndex] = new float[joints];
                _strainSeconds[rigIndex] = new float[joints];
                ResolveShins(rig, out _leftShin[rigIndex], out _rightShin[rigIndex]);
                Systems_FighterIdentity.Resolve(rig, out _names[rigIndex], out _plateColors[rigIndex]);
            }
        }

        /// <summary>
        /// The two shin ground sensors <c>Systems_ContestSpawner.Configure</c>
        /// attaches, sorted into left and right by the sign of their x in pelvis
        /// space. See the class comment: the side is taken from where the part
        /// physically is rather than from what it is called. A rig with one shin
        /// sensor, or none, simply produces no footwork commentary.
        /// </summary>
        private static void ResolveShins(Systems_FighterRig rig,
            out Sensor_GroundContact left, out Sensor_GroundContact right)
        {
            left = null;
            right = null;
            Transform pelvis = rig.Pelvis != null ? rig.Pelvis.transform : rig.transform;
            Sensor_GroundContact[] sensors = rig.GetComponentsInChildren<Sensor_GroundContact>(true);
            for (int index = 0; index < sensors.Length; index++)
            {
                Sensor_GroundContact sensor = sensors[index];
                if (!Has(sensor.name, "shin"))
                {
                    continue;
                }
                float side = pelvis.InverseTransformPoint(sensor.transform.position).x;
                if (side < 0f && left == null)
                {
                    left = sensor;
                }
                else if (side >= 0f && right == null)
                {
                    right = sensor;
                }
            }
        }

        private void BuildBand(VisualElement root)
        {
            _line = new Label();
            _line.style.position = Position.Absolute;
            _line.style.top = Length.Percent(BAND_TOP_PERCENT);
            _line.style.left = 24f;
            _line.style.right = 24f;
            _line.style.paddingTop = 10f;
            _line.style.paddingBottom = 10f;
            _line.style.paddingLeft = 16f;
            _line.style.paddingRight = 16f;
            _line.style.fontSize = 30f;
            _line.style.whiteSpace = WhiteSpace.Normal;
            _line.style.unityTextAlign = TextAnchor.MiddleLeft;
            _line.style.color = Color.white;
            _line.style.backgroundColor = Systems_UiTheme.PanelDark;
            Systems_UiTheme.SetRadius(_line, 12f);
            Systems_UiTheme.SetBorderWidth(_line, 2f);
            Color border = Systems_UiTheme.Gold;
            border.a = 0.45f;
            Systems_UiTheme.SetBorderColor(_line, border);
            _line.pickingMode = PickingMode.Ignore;
            _line.style.display = DisplayStyle.None;
            root.Add(_line);
        }

        private void OnRoundStarted(int round)
        {
            // Every streak is a statement about the round in progress. Carrying
            // one across a reset would have the booth describing a strain the
            // fighter no longer has, in a pose it no longer holds.
            Array.Clear(_leanSeconds, 0, _leanSeconds.Length);
            Array.Clear(_sinkSeconds, 0, _sinkSeconds.Length);
            Array.Clear(_plantedSeconds, 0, _plantedSeconds.Length);
            Array.Clear(_switchScore, 0, _switchScore.Length);
            Array.Clear(_repeatCooldown, 0, _repeatCooldown.Length);
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                Array.Clear(_strainSeconds[rigIndex], 0, _strainSeconds[rigIndex].Length);
                Array.Clear(_smoothedLoad[rigIndex], 0, _smoothedLoad[rigIndex].Length);
            }
            Hide();
        }

        /// <summary>
        /// LateUpdate for the same reason the heatmap uses it: this reads the
        /// pose the renderer is about to draw, and it must keep working while
        /// the game clock is stopped.
        /// </summary>
        private void LateUpdate()
        {
            if (!_ready)
            {
                return;
            }
            float showDt = Time.unscaledDeltaTime;
            float physicsDt = Time.deltaTime;

            TickLabel(showDt);
            if (_globalCooldown > 0f)
            {
                _globalCooldown -= showDt;
            }

            // Nothing is moving, so nothing new is true. Sampling here would
            // still advance every streak by the length of the freeze.
            if (physicsDt <= 0f)
            {
                return;
            }

            float bestPriority = 0f;
            int bestRig = -1;
            Tell bestTell = Tell.Strain;
            string bestText = null;

            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                Systems_FighterRig rig = _rigs[rigIndex];
                if (rig == null)
                {
                    continue;
                }
                for (int tellIndex = 0; tellIndex < TELL_COUNT; tellIndex++)
                {
                    if (_repeatCooldown[rigIndex, tellIndex] > 0f)
                    {
                        _repeatCooldown[rigIndex, tellIndex] -= physicsDt;
                    }
                }
                Observe(rigIndex, rig, physicsDt,
                    ref bestPriority, ref bestRig, ref bestTell, ref bestText);
            }

            if (bestRig < 0 || !Speaking || _globalCooldown > 0f || _lineRemaining > 0f)
            {
                return;
            }
            // The play-by-play owns the moment. Two voices on screen at once is
            // the collision the announcer already resolved once with the winner
            // banner, one band lower.
            if (_announcer != null && _announcer.CalloutActive)
            {
                return;
            }
            Speak(bestRig, bestTell, bestText, bestPriority);
        }

        /// <summary>
        /// Advances one fighter's rolling telemetry and offers its strongest
        /// tell. Every detector here is a STREAK rather than an instant: a
        /// threshold crossed for one frame is noise at 50 Hz, and the thing
        /// worth saying out loud is that a condition has PERSISTED.
        /// </summary>
        private void Observe(int rigIndex, Systems_FighterRig rig, float dt,
            ref float bestPriority, ref int bestRig, ref Tell bestTell, ref string bestText)
        {
            float headFraction =
                (rig.Head.position.y - rig.GroundY) / Mathf.Max(0.01f, _startHeadHeights[rigIndex]);
            if (headFraction <= STANDING_HEAD_FRACTION)
            {
                // A fighter on the mat has nothing left to be said about, and
                // the announcer has already called the fall.
                _leanSeconds[rigIndex] = 0f;
                _sinkSeconds[rigIndex] = 0f;
                _plantedSeconds[rigIndex] = 0f;
                return;
            }

            ObserveStrain(rigIndex, rig, dt, ref bestPriority, ref bestRig, ref bestTell, ref bestText);
            ObserveLean(rigIndex, rig, dt, ref bestPriority, ref bestRig, ref bestTell, ref bestText);
            ObserveSink(rigIndex, headFraction, dt,
                ref bestPriority, ref bestRig, ref bestTell, ref bestText);
            ObserveFootwork(rigIndex, dt, ref bestPriority, ref bestRig, ref bestTell, ref bestText);
        }

        private void ObserveStrain(int rigIndex, Systems_FighterRig rig, float dt,
            ref float bestPriority, ref int bestRig, ref Tell bestTell, ref string bestText)
        {
            var joints = rig.Joints;
            float springScale = rig.CurrentSpringScale;
            float[] smoothed = _smoothedLoad[rigIndex];
            float[] streak = _strainSeconds[rigIndex];

            int worstJoint = -1;
            float worstStreak = 0f;
            for (int jointIndex = 0; jointIndex < joints.Count && jointIndex < smoothed.Length; jointIndex++)
            {
                RigJointEntry entry = joints[jointIndex];
                if (entry == null || entry.body == null)
                {
                    continue;
                }
                float load = Systems_Stamina.JointLoad01(entry, springScale);
                smoothed[jointIndex] = Mathf.Lerp(smoothed[jointIndex], load, dt * LOAD_SMOOTH_RATE);
                streak[jointIndex] = smoothed[jointIndex] >= STRAIN_LOAD ? streak[jointIndex] + dt : 0f;
                if (streak[jointIndex] > worstStreak)
                {
                    worstStreak = streak[jointIndex];
                    worstJoint = jointIndex;
                }
            }

            if (worstJoint < 0 || worstStreak < STRAIN_SECONDS ||
                _repeatCooldown[rigIndex, (int)Tell.Strain] > 0f)
            {
                return;
            }
            string part = PartPhrase(rig, joints[worstJoint]);
            int percent = Mathf.RoundToInt(smoothed[worstJoint] * 100f);
            string name = _names[rigIndex];
            // Priority climbs with the streak: an ankle pinned for six seconds
            // is a better story than one pinned for three.
            float priority = Mathf.Clamp01(0.55f + 0.06f * (worstStreak - STRAIN_SECONDS));
            string text = (_variant % 3) switch
            {
                0 => $"{name}'s {part} has been at {percent}% of its limit for {worstStreak:0} seconds.",
                1 => $"No authority left in {name}'s {part} — {percent}%, and holding.",
                _ => $"Watch {name}'s {part}: {worstStreak:0} seconds pinned at {percent}%."
            };
            Offer(priority, rigIndex, Tell.Strain, text,
                ref bestPriority, ref bestRig, ref bestTell, ref bestText);
        }

        private void ObserveLean(int rigIndex, Systems_FighterRig rig, float dt,
            ref float bestPriority, ref int bestRig, ref Tell bestTell, ref string bestText)
        {
            Rigidbody torso = rig.Torso != null ? rig.Torso : rig.Pelvis;
            if (torso == null)
            {
                return;
            }
            float degrees = Vector3.Angle(torso.transform.up, Vector3.up);
            _leanSeconds[rigIndex] = degrees >= LEAN_DEGREES ? _leanSeconds[rigIndex] + dt : 0f;
            if (_leanSeconds[rigIndex] < LEAN_SECONDS || _repeatCooldown[rigIndex, (int)Tell.Lean] > 0f)
            {
                return;
            }
            string name = _names[rigIndex];
            string text = (_variant % 2) == 0
                ? $"{name} is {degrees:0} degrees off vertical and not correcting."
                : $"{degrees:0} degrees of lean on {name}, held for {_leanSeconds[rigIndex]:0} seconds.";
            Offer(0.6f, rigIndex, Tell.Lean, text,
                ref bestPriority, ref bestRig, ref bestTell, ref bestText);
        }

        private void ObserveSink(int rigIndex, float headFraction, float dt,
            ref float bestPriority, ref int bestRig, ref Tell bestTell, ref string bestText)
        {
            _sinkSeconds[rigIndex] = headFraction < SINK_FRACTION ? _sinkSeconds[rigIndex] + dt : 0f;
            if (_sinkSeconds[rigIndex] < SINK_SECONDS || _repeatCooldown[rigIndex, (int)Tell.Sink] > 0f)
            {
                return;
            }
            string name = _names[rigIndex];
            int percent = Mathf.RoundToInt(headFraction * 100f);
            string text = (_variant % 2) == 0
                ? $"{name} is down to {percent}% of standing height and still folding."
                : $"That is {name} sinking — head at {percent}% of where it started.";
            // The highest-priority tell there is: a head that has been low for
            // more than a moment is a fall that has already begun.
            Offer(0.72f, rigIndex, Tell.Sink, text,
                ref bestPriority, ref bestRig, ref bestTell, ref bestText);
        }

        private void ObserveFootwork(int rigIndex, float dt,
            ref float bestPriority, ref int bestRig, ref Tell bestTell, ref string bestText)
        {
            Sensor_GroundContact left = _leftShin[rigIndex];
            Sensor_GroundContact right = _rightShin[rigIndex];
            if (left == null || right == null)
            {
                return;
            }
            bool leftDown = left.IsGrounded;
            bool rightDown = right.IsGrounded;
            bool switched = leftDown != _leftDown[rigIndex] || rightDown != _rightDown[rigIndex];
            _leftDown[rigIndex] = leftDown;
            _rightDown[rigIndex] = rightDown;

            // A leaky bucket rather than a ring buffer of timestamps: the count
            // it approximates is "switches in the last SHUFFLE_WINDOW seconds"
            // and nothing here needs that to be exact.
            _switchScore[rigIndex] = Mathf.Max(
                0f, _switchScore[rigIndex] - dt / SHUFFLE_WINDOW * SHUFFLE_SWITCHES);
            if (switched)
            {
                _switchScore[rigIndex] += 1f;
            }
            _plantedSeconds[rigIndex] = leftDown && rightDown ? _plantedSeconds[rigIndex] + dt : 0f;

            if (_switchScore[rigIndex] >= SHUFFLE_SWITCHES &&
                _repeatCooldown[rigIndex, (int)Tell.Shuffle] <= 0f)
            {
                int switches = Mathf.RoundToInt(_switchScore[rigIndex]);
                string text = $"{_names[rigIndex]} is dancing — {switches} stance changes in the last four seconds.";
                Offer(0.45f, rigIndex, Tell.Shuffle, text,
                    ref bestPriority, ref bestRig, ref bestTell, ref bestText);
                return;
            }
            if (_plantedSeconds[rigIndex] >= FROZEN_SECONDS &&
                _repeatCooldown[rigIndex, (int)Tell.Frozen] <= 0f)
            {
                string text = (_variant % 2) == 0
                    ? $"{_names[rigIndex]} has not lifted a foot in {_plantedSeconds[rigIndex]:0} seconds."
                    : $"Both of {_names[rigIndex]}'s feet have been planted for {_plantedSeconds[rigIndex]:0} seconds.";
                Offer(0.35f, rigIndex, Tell.Frozen, text,
                    ref bestPriority, ref bestRig, ref bestTell, ref bestText);
            }
        }

        private static void Offer(float priority, int rigIndex, Tell tell, string text,
            ref float bestPriority, ref int bestRig, ref Tell bestTell, ref string bestText)
        {
            if (priority <= bestPriority)
            {
                return;
            }
            bestPriority = priority;
            bestRig = rigIndex;
            bestTell = tell;
            bestText = text;
        }

        private void Speak(int rigIndex, Tell tell, string text, float priority)
        {
            _line.text = text;
            _line.style.display = DisplayStyle.Flex;
            _line.style.opacity = 1f;
            // Tinted to the fighter it is about, which is the only cue that says
            // WHICH of "Grandma" and "Grandma2" the booth means. Border only:
            // colour is identity in this project, and the band is not a fighter.
            Color border = _plateColors[rigIndex];
            border.a = 0.85f;
            Systems_UiTheme.SetBorderColor(_line, border);

            _lineRemaining = LINE_SECONDS;
            _repeatCooldown[rigIndex, (int)tell] = REPEAT_COOLDOWN;
            _variant++;

            if (priority >= CUE_PRIORITY && _camera != null)
            {
                // Nominated, not commanded: the director refuses the cue while
                // it is holding a winner shot.
                _camera.RequestFocus(_rigs[rigIndex], CUE_SECONDS);
            }
            // Greppable from a player log for the same reason TALE_OF_THE_TAPE
            // is: the band is on screen for 4.5 s and an offline capture cannot
            // reliably sample it.
            Debug.Log($"COLOUR_COMMENTARY | {tell} | {text}");
        }

        private void TickLabel(float dt)
        {
            if (_lineRemaining <= 0f)
            {
                return;
            }
            _lineRemaining -= dt;
            if (_lineRemaining <= 0f)
            {
                Hide();
                _globalCooldown = GLOBAL_COOLDOWN;
                return;
            }
            if (_lineRemaining < FADE_SECONDS)
            {
                _line.style.opacity = _lineRemaining / FADE_SECONDS;
            }
        }

        private void Hide()
        {
            _lineRemaining = 0f;
            if (_line != null)
            {
                _line.style.display = DisplayStyle.None;
            }
        }

        /// <summary>
        /// "left ankle", "right knee", "spine" — the joint WORD from the driven
        /// part's name, the SIDE from where that part physically is. See the
        /// class comment for why the two come from different places.
        /// </summary>
        private static string PartPhrase(Systems_FighterRig rig, RigJointEntry entry)
        {
            if (entry == null || entry.body == null)
            {
                return "joint";
            }
            string word = JointWord(entry.body.name);
            Transform pelvis = rig.Pelvis != null ? rig.Pelvis.transform : rig.transform;
            float side = pelvis.InverseTransformPoint(entry.body.worldCenterOfMass).x;
            // Centreline parts (spine, neck, tail) have no side worth naming,
            // and a few centimetres of asymmetry on one would otherwise have the
            // booth announcing a "left spine".
            if (Mathf.Abs(side) < 0.05f)
            {
                return word;
            }
            return (side < 0f ? "left " : "right ") + word;
        }

        /// <summary>
        /// The anatomical joint that drives a part: a thigh is swung by the hip,
        /// a shin by the knee, a foot by the ankle. An unrecognised part is
        /// called a joint rather than guessed at — a wrong body part said with
        /// confidence is worse commentary than a vague one.
        /// </summary>
        private static string JointWord(string partName)
        {
            if (Has(partName, "thigh")) { return "hip"; }
            if (Has(partName, "shin")) { return "knee"; }
            if (Has(partName, "meta")) { return "toe"; }
            if (Has(partName, "foot")) { return "ankle"; }
            if (Has(partName, "upperarm")) { return "shoulder"; }
            if (Has(partName, "forearm")) { return "elbow"; }
            if (Has(partName, "glove") || Has(partName, "hand")) { return "wrist"; }
            if (Has(partName, "torso") || Has(partName, "spine")) { return "spine"; }
            if (Has(partName, "head") || Has(partName, "neck")) { return "neck"; }
            if (Has(partName, "tail")) { return "tail"; }
            if (Has(partName, "pelvis")) { return "hips"; }
            return "joint";
        }

        private static bool Has(string text, string fragment)
        {
            return text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
