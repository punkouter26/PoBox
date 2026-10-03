using System;
using UnityEngine;
using PoBox.League;

namespace PoBox.Sim
{
    public enum BoutPhase { Intro, Fight, Count, RoundBreak, Results, WalkOn }

    /// <summary>
    /// The rules. The walk-on, rounds, the clock, the count over a fighter on the canvas, the three judges'
    /// cards, who won and how, and then the next pairing off the ladder. It also owns time: the speed chosen
    /// in the menu goes through here, so there is exactly one place that touches the time scale.
    ///
    /// A bout ends inside the distance when a fighter fails to beat the count of ten, or has no health
    /// left; otherwise it goes to the judges (<see cref="Judges"/>), who may not agree.
    /// </summary>
    public class Bout : MonoBehaviour
    {
        public static Bout Instance { get; private set; }
        /// <summary>Physics is stepping. False between rounds and while the result is up.</summary>
        public static bool SimRunning { get; private set; }
        /// <summary>The fighters are allowed to throw.</summary>
        public static bool Fighting => Instance != null && Instance.Phase == BoutPhase.Fight;
        /// <summary>Seconds of fighting so far in this bout.</summary>
        public static float Clock { get; private set; }

        [Header("Cast")]
        public Fighter red;
        public Fighter blue;
        public Transform redCorner;
        public Transform blueCorner;
        public Transform redNeutral;
        public Transform blueNeutral;
        [Tooltip("Where each fighter stands for the walk-on: in its own corner, by the post. Empty: at its starting mark.")]
        public Transform redStool, blueStool;
        public LeagueTable league;

        [Header("Rules")]
        public int rounds = 3;
        public float roundSeconds = 45f;
        public float introSeconds = 2.5f;
        [Tooltip("Real seconds the two are shown in their corners before the first round: the cameras and the lights have it, the fighters stand. 0 = none.")]
        public float walkOnSeconds = 5f;
        [Tooltip("Seconds per number of the referee's count.")]
        public float countInterval = 0.7f;
        [Tooltip("Real seconds between the end of one round and the start of the next.")]
        public float breakSeconds = 3f;
        [Tooltip("Real seconds the results stay up before the next bout starts on its own.")]
        public float resultsSeconds = 18f;
        public bool autoAdvance = true;
        [Tooltip("The two fighters in the scene are who they are: a trained policy belongs to the body it was trained in, so the ladder does not hand profiles round. Off for the scripted stand-ins, which are interchangeable.")]
        public bool fixedEntrants;

        [Header("Time")]
        public float physicsStep = 1f / 120f;
        [Range(1f, 4f)] public float userTimeScale = 1f;

        public BoutPhase Phase { get; private set; } = BoutPhase.Intro;
        public int Round { get; private set; } = 1;
        public float RoundTimeLeft { get; private set; }
        public int Count { get; private set; }
        public Fighter Downed { get; private set; }
        public Fighter Winner { get; private set; }
        /// <summary>"KO R2 0:31", "SPLIT DECISION", "MAJORITY DRAW".</summary>
        public string Method { get; private set; } = "";
        /// <summary>The three judges. Their cards are complete once the bout has gone the distance.</summary>
        public readonly Judges judges = new Judges();
        /// <summary>The bout went to the cards.</summary>
        public bool Decision { get; private set; }
        public float EloDelta { get; private set; }
        public int BoutNumber { get; private set; }
        /// <summary>
        /// Real seconds in the present phase. Counted frame by frame with no frame worth more than a tenth
        /// of a second: the first frame after a scene loads can take several seconds, and a clock read off
        /// the wall would have the five-second walk-on over before anything had been drawn.
        /// </summary>
        public float PhaseAge => _phaseAge;
        public float ResultsTimeLeft => Mathf.Max(0f, resultsSeconds - PhaseAge);

        float _phaseTimer, _countTimer, _phaseAge;
        bool _started;

        void Awake()
        {
            Instance = this;
            SimRunning = false;
            Clock = 0f;
        }

        void OnEnable()
        {
            SimBus.Knockdown += OnKnockdown;
            SimBus.GotUp += OnGotUp;
            SimBus.Hit += OnHit;
            SimBus.PunchThrown += OnPunch;
        }

        void OnDisable()
        {
            SimBus.Knockdown -= OnKnockdown;
            SimBus.GotUp -= OnGotUp;
            SimBus.Hit -= OnHit;
            SimBus.PunchThrown -= OnPunch;
            if (Instance == this)
            {
                Instance = null;
                SimRunning = false;
                Time.timeScale = 1f;
            }
        }

        void Start()
        {
            Time.fixedDeltaTime = physicsStep;
            NewBout();
        }

        // ------------------------------------------------------------ flow

        public void NewBout()
        {
            BoutNumber++;
            Winner = null;
            Method = "";
            EloDelta = 0f;
            Round = 1;
            Clock = 0f;
            Count = 0;
            Downed = null;
            Decision = false;
            judges.Reset();

            if (league != null && !fixedEntrants && league.PickNext(out PolicyProfile a, out PolicyProfile b))
            {
                Assign(red, a);
                Assign(blue, b);
            }

            // The walk-on finds each fighter in its own corner, across the ring from the other.
            bool walk = walkOnSeconds > 0f;
            Place(true, walk);
            _started = true;
            if (red != null) judges.impulseFloor = red.impulseFloor;
            SetPhase(walk ? BoutPhase.WalkOn : BoutPhase.Intro);
            SimBus.Say($"{red.displayName} v {blue.displayName}", 2);
        }

        /// <summary>Throws the current bout away and starts the same two again.</summary>
        public void Restart()
        {
            BoutNumber++;
            Winner = null; Method = ""; EloDelta = 0f; Round = 1; Clock = 0f; Count = 0; Downed = null;
            Decision = false;
            judges.Reset();
            Place(true);
            SetPhase(BoutPhase.Intro);
        }

        static void Assign(Fighter f, PolicyProfile p)
        {
            if (f == null || p == null) return;
            f.profile = p;
            f.displayName = p.displayName;
        }

        void Place(bool newBout, bool inCorners = false)
        {
            Respawn(red, inCorners && redStool != null ? redStool : redCorner, redNeutral, newBout);
            Respawn(blue, inCorners && blueStool != null ? blueStool : blueCorner, blueNeutral, newBout);
        }

        void Respawn(Fighter f, Transform corner, Transform neutral, bool newBout)
        {
            if (f == null || corner == null) return;
            f.Respawn(corner.position, corner.rotation, newBout);
            // Its policy starts the round with a clear head.
            f.SendMessage("ResetBrain", SendMessageOptions.DontRequireReceiver);
        }

        void SetPhase(BoutPhase next)
        {
            BoutPhase from = Phase;
            Phase = next;
            _phaseAge = 0f;
            _phaseTimer = 0f;

            bool run = next == BoutPhase.Intro || next == BoutPhase.Fight || next == BoutPhase.Count;
            // MuJoCo steps only while this is set, which is what freezes the ring between rounds.
            SimRunning = run;
            if (red != null && red.ring != null) red.ring.running = run;

            if (next == BoutPhase.Intro || next == BoutPhase.WalkOn) RoundTimeLeft = roundSeconds;
            if (_started) SimBus.RaisePhase(from, next);
        }

        void Update()
        {
            ApplyTime();
            float dt = Time.deltaTime;
            _phaseTimer += dt;
            _phaseAge += Mathf.Min(Time.unscaledDeltaTime, 0.1f);

            switch (Phase)
            {
                case BoutPhase.WalkOn:
                    // Real seconds: nothing is being simulated, so the game's speed has nothing to speed up.
                    if (PhaseAge >= walkOnSeconds)
                    {
                        // Out of the corners to the marks they box from. The picture cuts at the same moment.
                        Place(true);
                        SetPhase(BoutPhase.Intro);
                    }
                    break;

                case BoutPhase.Intro:
                    if (_phaseTimer >= introSeconds)
                    {
                        SetPhase(BoutPhase.Fight);
                        SimBus.Say($"ROUND {Round}", 2);
                    }
                    break;

                case BoutPhase.Fight:
                    Clock += dt;
                    RoundTimeLeft -= dt;
                    if (RoundTimeLeft <= 0f) EndRound();
                    break;

                case BoutPhase.Count:
                    Clock += dt;
                    _countTimer += dt;
                    if (_countTimer >= countInterval)
                    {
                        _countTimer = 0f;
                        Count++;
                        if (Count >= 10) Finish(Other(Downed), $"KO R{Round} {ClockText(roundSeconds - RoundTimeLeft)}", true);
                    }
                    break;

                case BoutPhase.RoundBreak:
                    if (PhaseAge > breakSeconds)
                    {
                        Round++;
                        Place(false);
                        SetPhase(BoutPhase.Intro);
                    }
                    break;

                case BoutPhase.Results:
                    if (autoAdvance && PhaseAge > resultsSeconds) NewBout();
                    break;
            }
        }

        void ApplyTime()
        {
            float scale = Mathf.Max(1f, userTimeScale);
            if (!Mathf.Approximately(Time.timeScale, scale)) Time.timeScale = scale;
            // The step never changes with the speed: a trained policy was taught at one step length and one
            // control rate, and a different step is a different body.
            if (!Mathf.Approximately(Time.fixedDeltaTime, physicsStep)) Time.fixedDeltaTime = physicsStep;
        }

        void OnHit(HitEvent e)
        {
            if (Phase == BoutPhase.Fight && e.attacker != null) judges.OnHit(e.attacker == red ? 0 : 1, e);
        }

        void OnPunch(Fighter f, PunchType type, int hand)
        {
            if (Phase == BoutPhase.Fight) judges.OnThrown(f == red ? 0 : 1);
        }

        void OnKnockdown(Fighter f, HitEvent cause)
        {
            if (Phase != BoutPhase.Fight) return;
            judges.OnKnockdown(f == red ? 0 : 1);
            Downed = f;
            Count = 0;
            _countTimer = 0f;
            SetPhase(BoutPhase.Count);
        }

        void OnGotUp(Fighter f)
        {
            if (Phase != BoutPhase.Count || f != Downed) return;
            Downed = null;
            Count = 0;
            SetPhase(BoutPhase.Fight);
        }

        void EndRound()
        {
            RoundTimeLeft = 0f;
            judges.CloseRound();
            if (Round >= rounds)
            {
                Decision = true;
                string verdict = judges.Verdict(out int winner);
                Finish(winner == 0 ? red : winner == 1 ? blue : null, verdict, false);
                return;
            }
            SimBus.Say($"End of round {Round}. The cards: {CardsText()}", 2);
            SetPhase(BoutPhase.RoundBreak);
        }

        void Finish(Fighter winner, string method, bool knockout)
        {
            Winner = winner;
            Method = method;
            // A boxer against a copy of itself is not a result the ladder can hold.
            if (league != null && red.displayName != blue.displayName)
            {
                float score = winner == null ? 0.5f : winner == red ? 1f : 0f;
                EloDelta = league.Report(red.displayName, blue.displayName, score, knockout);
            }
            SimBus.Say(winner != null ? $"{winner.displayName} wins: {method}" : method, 3);
            SetPhase(BoutPhase.Results);
        }

        // ------------------------------------------------------------ helpers

        public Fighter Other(Fighter f) => f == red ? blue : red;

        /// <summary>Damage done, with a knockdown worth a clear round: one number for a fighter's bout so far.</summary>
        public static float Score(Fighter f) => f == null ? 0f : f.stats.damageDealt + 15f * f.stats.knockdowns;

        /// <summary>The three cards as they stand, red's score first: "29-28 · 28-29 · 29-28".</summary>
        public string CardsText() => $"{judges.Card(0)} · {judges.Card(1)} · {judges.Card(2)}";

        public static string ClockText(float seconds)
        {
            int s = Mathf.Max(0, Mathf.CeilToInt(seconds - 0.001f));
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
