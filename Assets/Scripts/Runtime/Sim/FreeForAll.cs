using System.Collections.Generic;
using UnityEngine;
using PoBox.Fx;
using PoBox.Mj;

namespace PoBox.Sim
{
    /// <summary>
    /// Every boxer in the ring at once (the Melee scene), boxed like a bout: three rounds with the bell, a count over
    /// a boxer knocked down, the judges if more than one is left at the end. It takes the place of <see cref="Bout"/>
    /// and <see cref="MatchSetup"/>, which are for two.
    ///
    ///   * The scene holds one corner prefab of each boxer, switched off. Before MuJoCo compiles, each is renamed
    ///     to a prefix of its own (a_, b_, c_, ...: the plugin names MuJoCo's objects after the GameObjects, and the
    ///     prefabs all say a_), given a colour of its own, switched on and handed to the ring. Every body is solid,
    ///     down or not: boxers on the canvas are tripped over and piled on.
    ///   * Each round starts with everybody still in at the ropes, in their guard, facing in. At the bell they walk
    ///     out towards the middle on the footwork policy (the one taught to walk on command; a boxing policy has
    ///     never seen an opponent five metres off), and box once another boxer is a step and a half away. A brain
    ///     (which sees one opponent) is kept facing the nearest boxer on its feet, turns on whoever has just hit
    ///     it, and leaves alone one that two are already on.
    ///   * A punch that puts a boxer down starts its own count, a number every <see cref="countInterval"/> seconds,
    ///     while the rest fight on; up before ten, it fights on; counted out, it stays where it lies. A fall with
    ///     no punch behind it is a slip and is not counted; a boxer who cannot get up from one in slipSeconds cannot go on.
    ///   * The round runs on through a count: the bell does not save anyone. Between rounds everybody still in
    ///     gets a minute on the stool (Fighter.Respawn) and goes back to the ropes.
    ///   * The last boxer standing wins. If more than one is in after the last round, the judges give it to the one
    ///     who did the most damage, a knockdown worth a clean round (<see cref="Bout.Score"/>).
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class FreeForAll : MonoBehaviour
    {
        public enum Stage { Waiting, Intro, Fight, Break, Over }

        [Tooltip("One corner prefab of each boxer, switched off.")]
        public Fighter[] fighters = new Fighter[0];
        public MjRing ring;
        [Header("Rules: the 1-on-1's")]
        public int rounds = 3;
        public float roundSeconds = 45f;
        public float introSeconds = 2.5f;
        [Tooltip("Real seconds between rounds.")]
        public float breakSeconds = 3f;
        [Tooltip("Seconds per number of a count.")]
        public float countInterval = 0.7f;
        [Tooltip("Seconds a boxer who slipped (no punch, no count) may stay down before it is ruled unable to go on.")]
        public float slipSeconds = 15f;
        [Tooltip("Real seconds after the final bell before the ring stops: the last punch and fall finish.")]
        public float settleSeconds = 1.5f;
        [Header("Who fights whom")]
        [Tooltip("Metres inside the ropes the boxers start from.")]
        public float startInside = 0.5f;
        [Tooltip("A new opponent has to be this much better than the present one, metres, to be turned to.")]
        public float switchMargin = 0.4f;
        [Tooltip("Metres an opponent counts as nearer for having just hit this boxer.")]
        public float grudge = 1.0f;
        [Tooltip("Metres an opponent counts as further for every other boxer already on it.")]
        public float crowding = 1.0f;
        [Tooltip("Walking out at the bell, m/s, on the footwork policy (taught 0.3 to 1.0).")]
        public float walkOutSpeed = 0.6f;
        [Tooltip("The walk ends and the boxing starts this near another boxer on its feet, metres (a step and a half: how a training episode starts), or this near the middle.")]
        public float engageAt = 1.6f, middleAt = 0.8f;
        [Tooltip("Seconds a walk out lasts at most.")]
        public float walkSeconds = 6f;
        public Color[] colours =
        {
            new Color(0.89f, 0.24f, 0.24f), new Color(0.20f, 0.49f, 0.94f), new Color(0.98f, 0.76f, 0.18f), new Color(0.22f, 0.78f, 0.45f),
            new Color(0.66f, 0.36f, 0.92f), new Color(0.98f, 0.52f, 0.16f), new Color(0.20f, 0.80f, 0.86f), new Color(0.95f, 0.45f, 0.72f),
        };

        public static FreeForAll Instance { get; private set; }
        public Stage Phase { get; private set; } = Stage.Waiting;
        public int Round { get; private set; } = 1;
        public float RoundTimeLeft { get; private set; }
        public Fighter Winner { get; private set; }
        /// <summary>Seconds of fighting in the bout so far.</summary>
        public float Clock { get; private set; }
        public string Method { get; private set; } = "";
        public float PhaseAge { get; private set; }

        readonly Dictionary<Fighter, float> _downFor = new Dictionary<Fighter, float>();
        readonly HashSet<Fighter> _out = new HashSet<Fighter>(), _counted = new HashSet<Fighter>();
        readonly Dictionary<Fighter, MjBrain> _brain = new Dictionary<Fighter, MjBrain>();
        float _roundAge;
        bool _settled;

        public bool IsOut(Fighter f) => _out.Contains(f);
        /// <summary>The count over a boxer knocked down (0 to 10); -1 when it is standing, slipped, or already out.</summary>
        public int CountOf(Fighter f) =>
            !_out.Contains(f) && _counted.Contains(f) && _downFor.TryGetValue(f, out float t) ? Mathf.Min(10, Mathf.FloorToInt(t / countInterval)) : -1;
        /// <summary>Down without a punch behind it: a slip, not counted.</summary>
        public bool Slipped(Fighter f) => f != null && f.IsDown && !_out.Contains(f) && !_counted.Contains(f);
        public int Left { get { int n = 0; foreach (Fighter f in fighters) if (f != null && !_out.Contains(f)) n++; return n; } }

        void Awake()
        {
            Instance = this;
            var boxers = new List<MjBoxer>();
            for (int i = 0; i < fighters.Length; i++)
            {
                Fighter f = fighters[i];
                if (f == null || f.boxer == null) continue;
                // A prefix of its own: two letters, as SkinBinder expects (it strips "x_" to find the bodies).
                string from = f.boxer.prefix, to = (char)('a' + boxers.Count) + "_";
                if (from != to)
                    foreach (Transform t in f.GetComponentsInChildren<Transform>(true))
                        if (t.name.StartsWith(from)) t.name = to + t.name.Substring(from.Length);
                f.boxer.prefix = to;
                Paint(f, colours[boxers.Count % colours.Length]);
                f.ring = ring;
                f.gameObject.SetActive(true);
                _brain[f] = f.GetComponent<MjBrain>();
                boxers.Add(f.boxer);
            }
            if (ring == null || boxers.Count < 2)
            {
                Debug.LogError("[FreeForAll] needs a ring and at least two boxers.", this);
                enabled = false;
                return;
            }
            ring.boxers = boxers.ToArray();
            ring.red = boxers[0];
            ring.blue = boxers[1];
            ring.solidBodies = true;
            Bout.SimRunning = false;
            Bout.FightingWithoutBout = false;
            Bout.Clock = 0f;
        }

        void OnEnable() => SimBus.Knockdown += OnKnockdown;

        void OnDisable()
        {
            SimBus.Knockdown -= OnKnockdown;
            if (Instance != this) return;
            Instance = null;
            Bout.SimRunning = false;
            Bout.FightingWithoutBout = false;
        }

        static void Paint(Fighter f, Color c)
        {
            f.cornerColor = c;
            var skin = f.GetComponent<FighterSkin>();
            if (skin == null) return;
            skin.rimColor = c;
            foreach (TrailRenderer t in skin.trails)
                if (t != null) { t.startColor = new Color(c.r, c.g, c.b, 0.55f); t.endColor = new Color(c.r, c.g, c.b, 0f); }
        }

        /// <summary>A punch put it down (the cause has an attacker): the referee counts. Anything else is a slip.</summary>
        void OnKnockdown(Fighter f, HitEvent cause)
        {
            if (Phase != Stage.Fight || f == null || _out.Contains(f)) return;
            if (cause.attacker != null) _counted.Add(f);
        }

        // ------------------------------------------------------------ flow

        /// <summary>Everybody back on their feet at the ropes, the counts and the result cleared: the same bout again.</summary>
        public void Restart()
        {
            if (!ring.Ready) return;
            _out.Clear();
            _counted.Clear();
            _downFor.Clear();
            Winner = null;
            Method = "";
            Clock = 0f;
            Bout.Clock = 0f;
            Round = 1;
            SetPhase(Stage.Intro, BoutPhase.WalkOn);       // the walk-on's sting, and the canvas wiped
            ToTheRopes(true);
            SimBus.Say($"{fighters.Length} in the ring. Last one standing.", 2);
        }

        /// <summary>
        /// Everybody still in to a spot at the ropes, facing the middle, round the ring in the order of the scene, each
        /// set on the boxer across from it. A spot with a body lying on it is passed over for the next one free.
        /// </summary>
        void ToTheRopes(bool newBout)
        {
            float half = ring.ringHalf - startInside;
            // The floor's height is this object's: it stands on the canvas.
            Vector3 centre = new Vector3(ring.centre.x, transform.position.y, ring.centre.y);
            var spots = new List<Vector3>();
            for (int i = 0; i < 16; i++)
            {
                // Round the square of the ropes: corners, the middle of each side, and the points between.
                float a = Mathf.PI * 2f * i / 16f, c = Mathf.Cos(a), s = Mathf.Sin(a), m = Mathf.Max(Mathf.Abs(c), Mathf.Abs(s));
                spots.Add(centre + new Vector3(c / m, 0f, s / m) * half);
            }
            var placed = new List<(Fighter f, int spot)>();
            var used = new bool[spots.Count];
            var inRing = new List<Fighter>();
            foreach (Fighter f in fighters) if (f != null && !_out.Contains(f)) inRing.Add(f);
            for (int n = 0; n < inRing.Count; n++)
            {
                int want = Mathf.RoundToInt(n * spots.Count / (float)inRing.Count) % spots.Count;
                for (int tries = 0; tries < spots.Count; tries++)
                {
                    int s = (want + tries) % spots.Count;
                    if (used[s] || Blocked(spots[s])) continue;
                    used[s] = true;
                    placed.Add((inRing[n], s));
                    break;
                }
            }
            foreach (var (f, s) in placed)
            {
                Vector3 at = spots[s];
                f.Respawn(at, Quaternion.LookRotation(centre - at), newBout);
                f.SendMessage("ResetBrain", SendMessageOptions.DontRequireReceiver);
                // In its guard at the ropes until the bell.
                ring.Hold(f.boxer, centre - at);
            }
            _roundAge = 0f;
            _downFor.Clear();
            _counted.Clear();
            Retarget(true);
        }

        /// <summary>A body lies within reach of the spot: a boxer stood there would be stood inside it.</summary>
        bool Blocked(Vector3 spot)
        {
            foreach (Fighter f in fighters)
            {
                if (f == null || !_out.Contains(f) || f.pelvis == null) continue;
                Vector3 d = f.pelvis.transform.position - spot;
                d.y = 0f;
                if (d.magnitude < 1.0f) return true;
            }
            return false;
        }

        void SetPhase(Stage next, BoutPhase said)
        {
            BoutPhase from = Phase == Stage.Fight ? BoutPhase.Fight : Phase == Stage.Break ? BoutPhase.RoundBreak : Phase == Stage.Over ? BoutPhase.Results : BoutPhase.Intro;
            Phase = next;
            PhaseAge = 0f;
            bool run = next == Stage.Intro || next == Stage.Fight || next == Stage.Over;
            Bout.SimRunning = run;
            Bout.FightingWithoutBout = next == Stage.Fight;
            ring.running = run;
            if (next == Stage.Intro) RoundTimeLeft = roundSeconds;
            _settled = false;
            // The bell, the sting and the applause are the arena's sound's, which listens for a bout's phases.
            SimBus.RaisePhase(from, said);
            if (said == BoutPhase.WalkOn) SimBus.RaisePhase(BoutPhase.WalkOn, BoutPhase.Intro);
        }

        void FixedUpdate()
        {
            // Before the brains (MjBrain runs at -40): a boxer whose opponent has just gone down turns to another
            // standing one at once, instead of being held in its guard as if the referee were counting.
            if (Phase == Stage.Intro || Phase == Stage.Fight) Retarget(false);
            if (Phase == Stage.Fight) EndWalks();
        }

        /// <summary>Out of the corner: the walk ends near another boxer on its feet, at the middle, when hit, or after walkSeconds.</summary>
        void EndWalks()
        {
            Vector3 middle = new Vector3(ring.centre.x, 0f, ring.centre.y);
            foreach (Fighter f in fighters)
            {
                if (f == null || !_brain.TryGetValue(f, out MjBrain brain) || !brain.Walking) continue;
                Vector3 me = f.pelvis.transform.position;
                bool near = Flat(me - middle) < middleAt || _roundAge > walkSeconds || f.TimeSinceHit < 0.5f || f.IsDown;
                foreach (Fighter g in fighters)
                    if (!near && g != null && g != f && !g.IsDown && !_out.Contains(g) && Flat(g.pelvis.transform.position - me) < engageAt) near = true;
                if (near) brain.EndWalk();
            }
        }

        void Update()
        {
            PhaseAge += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            float dt = Time.deltaTime;
            switch (Phase)
            {
                case Stage.Waiting:
                    if (ring.Ready) Restart();
                    break;

                case Stage.Intro:
                    if (PhaseAge >= introSeconds)
                    {
                        SetPhase(Stage.Fight, BoutPhase.Fight);
                        SimBus.Say($"ROUND {Round}", 2);
                        foreach (Fighter f in fighters)
                        {
                            if (f == null || _out.Contains(f)) continue;
                            ring.Release(f.boxer);
                            if (_brain.TryGetValue(f, out MjBrain brain)) brain.WalkOut(walkOutSpeed);
                        }
                    }
                    break;

                case Stage.Fight:
                    Clock += dt;
                    Bout.Clock = Clock;
                    _roundAge += dt;
                    RoundTimeLeft = Mathf.Max(0f, RoundTimeLeft - dt);
                    Count(dt);
                    if (Decide()) break;
                    // The bell waits for a count to finish: nobody is saved by it.
                    if (RoundTimeLeft <= 0f && !Counting()) EndRound();
                    break;

                case Stage.Break:
                    if (PhaseAge >= breakSeconds)
                    {
                        Round++;
                        ToTheRopes(false);
                        SetPhase(Stage.Intro, BoutPhase.Intro);
                    }
                    break;

                case Stage.Over:
                    // The final bell: the last punch and the last fall finish, then everything stops, as in a bout.
                    if (!_settled && PhaseAge >= settleSeconds)
                    {
                        _settled = true;
                        Bout.SimRunning = false;
                        ring.running = false;
                    }
                    break;
            }
        }

        bool Counting()
        {
            foreach (Fighter f in _counted) if (f != null && f.IsDown && !_out.Contains(f)) return true;
            return false;
        }

        void Count(float dt)
        {
            foreach (Fighter f in fighters)
            {
                if (f == null || _out.Contains(f)) continue;
                if (!f.IsDown) { _downFor.Remove(f); _counted.Remove(f); continue; }
                _downFor.TryGetValue(f, out float t);
                t += dt;
                _downFor[f] = t;
                // Ten after a knockdown: counted out. After a slip there is no count, but a boxer who cannot get up
                // (from under a pile, say) in all that time cannot go on.
                bool knockedDown = _counted.Contains(f);
                if (knockedDown ? Mathf.FloorToInt(t / countInterval) < 10 : t < slipSeconds) continue;
                _out.Add(f);
                f.Eliminate();
                SimBus.Say(knockedDown ? $"{f.displayName} is counted out. {Left} left." : $"{f.displayName} cannot go on. {Left} left.", 2);
            }
        }

        /// <summary>Over inside the distance: one left on its feet, or nobody left at all.</summary>
        bool Decide()
        {
            Fighter last = null;
            int standing = 0, left = 0;
            foreach (Fighter f in fighters)
            {
                if (f == null || _out.Contains(f)) continue;
                left++;
                if (!f.IsDown) { standing++; last = f; }
            }
            if (left == 1 && standing == 1) { Finish(last, $"KO · ROUND {Round} · {Bout.ClockText(roundSeconds - RoundTimeLeft)}"); return true; }
            if (left == 0) { Finish(null, "NOBODY BEAT THE COUNT"); return true; }
            return false;
        }

        void EndRound()
        {
            if (Round < rounds)
            {
                SimBus.Say($"End of round {Round}.", 2);
                SetPhase(Stage.Break, BoutPhase.RoundBreak);
                return;
            }
            // The distance: the judges, on the damage done and the knockdowns scored, among those still in.
            Fighter best = null;
            foreach (Fighter f in fighters)
                if (f != null && !_out.Contains(f) && (best == null || Bout.Score(f) > Bout.Score(best))) best = f;
            Finish(best, $"JUDGES' DECISION · {Left} WENT THE DISTANCE");
        }

        void Finish(Fighter winner, string method)
        {
            Winner = winner;
            Method = method;
            SetPhase(Stage.Over, BoutPhase.Results);
            SimBus.Say(winner != null ? $"{winner.displayName} wins: {method}" : method, 3);
        }

        // ------------------------------------------------------------ who fights whom

        /// <summary>
        /// Every boxer still in gets an opponent: the nearest on its feet, counted nearer if it has just landed on this one and further for
        /// every other boxer already on it; with nobody else standing, the nearest still in (it is then held facing
        /// them, as over a count).
        /// </summary>
        void Retarget(bool fresh)
        {
            var on = new Dictionary<Fighter, int>();
            foreach (Fighter f in fighters)
                if (f != null && f.opponent != null && !_out.Contains(f)) { on.TryGetValue(f.opponent, out int n); on[f.opponent] = n + 1; }

            foreach (Fighter f in fighters)
            {
                if (f == null || f.pelvis == null || _out.Contains(f)) continue;
                Vector3 me = f.pelvis.transform.position;

                bool anyStanding = false;
                foreach (Fighter g in fighters)
                    if (g != null && g != f && !_out.Contains(g) && !g.IsDown) { anyStanding = true; break; }
                Fighter best = null;
                float bestCost = float.MaxValue, nowCost = float.MaxValue;
                foreach (Fighter g in fighters)
                {
                    if (g == null || g == f || g.pelvis == null || _out.Contains(g) || (anyStanding && g.IsDown)) continue;
                    on.TryGetValue(g, out int already);
                    if (f.opponent == g) already--;
                    float cost = Flat(g.pelvis.transform.position - me) + crowding * Mathf.Max(0, already - 1);
                    if (f.LastHitTaken.attacker == g && f.TimeSinceHit < 1.5f) cost -= grudge;
                    if (g == f.opponent) nowCost = cost;
                    if (cost < bestCost) { bestCost = cost; best = g; }
                }
                if (best == null) continue;
                if (!fresh && nowCost < float.MaxValue && nowCost - bestCost < switchMargin) continue;
                Face(f, best, on);
            }
        }

        static void Face(Fighter f, Fighter g, Dictionary<Fighter, int> on)
        {
            if (f.opponent == g) return;
            if (f.opponent != null && on.TryGetValue(f.opponent, out int n)) on[f.opponent] = n - 1;
            on.TryGetValue(g, out int m);
            on[g] = m + 1;
            f.opponent = g;
            f.boxer.Face(g.boxer);
        }

        static float Flat(Vector3 d) { d.y = 0f; return d.magnitude; }
    }
}
