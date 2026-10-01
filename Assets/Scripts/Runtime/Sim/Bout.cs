using System;
using UnityEngine;
using PoBox.League;

namespace PoBox.Sim
{
    public enum BoutPhase { Intro, Fight, Count, RoundBreak, Results }

    /// <summary>
    /// The rules. Rounds, the clock, the count over a fighter on the canvas, who won and how, and then the
    /// next pairing off the ladder. It also owns time: the slow motion on a big hit and the freeze while a
    /// replay plays both go through here, so there is exactly one place that touches the time scale.
    /// </summary>
    public class Bout : MonoBehaviour
    {
        public static Bout Instance { get; private set; }
        /// <summary>Physics is stepping. False while a replay is on screen.</summary>
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
        public LeagueTable league;

        [Header("Rules")]
        public int rounds = 3;
        public float roundSeconds = 45f;
        public float introSeconds = 2.5f;
        [Tooltip("Seconds per number of the referee's count.")]
        public float countInterval = 0.7f;
        [Tooltip("Real seconds the round-break replay may take before the next round starts regardless.")]
        public float breakSeconds = 9f;
        [Tooltip("Real seconds the results stay up before the next bout starts on its own.")]
        public float resultsSeconds = 18f;
        public bool autoAdvance = true;
        [Tooltip("The two fighters in the scene are who they are: a trained policy belongs to the body it was trained in, so the ladder does not hand profiles round. Off for the scripted stand-ins, which are interchangeable.")]
        public bool fixedEntrants;

        [Header("Time")]
        public float physicsStep = 1f / 120f;
        [Range(0.1f, 2f)] public float userTimeScale = 1f;
        public bool fixedStepInSlowMotion;

        public BoutPhase Phase { get; private set; } = BoutPhase.Intro;
        public int Round { get; private set; } = 1;
        public float RoundTimeLeft { get; private set; }
        public int Count { get; private set; }
        public Fighter Downed { get; private set; }
        public Fighter Winner { get; private set; }
        /// <summary>"KO R2 0:31", "POINTS 58-41", "DRAW 40-40".</summary>
        public string Method { get; private set; } = "";
        public float EloDelta { get; private set; }
        public int BoutNumber { get; private set; }
        public float PhaseAge => Time.unscaledTime - _phaseStartReal;
        public float ResultsTimeLeft => Mathf.Max(0f, resultsSeconds - PhaseAge);
        public bool SlowMotion => Time.unscaledTime < _slowUntil;

        /// <summary>Set by the replay system while a clip owns the screen: the break waits for it.</summary>
        [NonSerialized] public bool replayBusy;

        float _phaseTimer, _countTimer, _phaseStartReal, _slowUntil, _slowScale = 1f;
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
        }

        void OnDisable()
        {
            SimBus.Knockdown -= OnKnockdown;
            SimBus.GotUp -= OnGotUp;
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

            if (league != null && !fixedEntrants && league.PickNext(out PolicyProfile a, out PolicyProfile b))
            {
                Assign(red, a);
                Assign(blue, b);
            }

            Place(true);
            _started = true;
            SetPhase(BoutPhase.Intro);
            SimBus.Say($"{red.displayName} v {blue.displayName}", 2);
        }

        /// <summary>Throws the current bout away and starts the same two again.</summary>
        public void Restart()
        {
            BoutNumber++;
            Winner = null; Method = ""; EloDelta = 0f; Round = 1; Clock = 0f; Count = 0; Downed = null;
            Place(true);
            SetPhase(BoutPhase.Intro);
        }

        static void Assign(Fighter f, PolicyProfile p)
        {
            if (f == null || p == null) return;
            f.profile = p;
            f.displayName = p.displayName;
        }

        void Place(bool newBout)
        {
            Respawn(red, redCorner, redNeutral, newBout);
            Respawn(blue, blueCorner, blueNeutral, newBout);
        }

        void Respawn(Fighter f, Transform corner, Transform neutral, bool newBout)
        {
            if (f == null || corner == null) return;
            f.Respawn(corner.position, corner.rotation, newBout);
            // Whatever is driving it, scripted or trained, starts the round with a clear head.
            f.SendMessage("ResetBrain", SendMessageOptions.DontRequireReceiver);
            var scripted = f.GetComponent<ScriptedBoxer>();
            if (scripted != null && neutral != null) scripted.neutralSpot = neutral.position;
            var trained = f.GetComponent<Rl.PolicyBrain>();
            if (trained != null && neutral != null) trained.neutralSpot = neutral.position;
        }

        void SetPhase(BoutPhase next)
        {
            BoutPhase from = Phase;
            Phase = next;
            _phaseStartReal = Time.unscaledTime;
            _phaseTimer = 0f;

            bool run = next == BoutPhase.Intro || next == BoutPhase.Fight || next == BoutPhase.Count;
            // PhysicsStepper steps the world only while this is set, which is what freezes it for a replay.
            SimRunning = run;

            if (next == BoutPhase.Intro) RoundTimeLeft = roundSeconds;
            if (_started) SimBus.RaisePhase(from, next);
        }

        void Update()
        {
            ApplyTime();
            float dt = Time.deltaTime;
            _phaseTimer += dt;

            switch (Phase)
            {
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
                    if (!replayBusy && PhaseAge > 1.2f || PhaseAge > breakSeconds)
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
            float scale = userTimeScale;
            if (Time.unscaledTime < _slowUntil) scale *= _slowScale;
            if (!Mathf.Approximately(Time.timeScale, scale)) Time.timeScale = scale;
            // Slow motion would otherwise run the physics at a fraction of the frame rate and stutter:
            // articulations are not interpolated. Shrinking the step with the scale keeps one step a frame.
            // Not for trained fighters: their policies were trained at one step length and one control
            // rate, and a shorter step is a different body. At 200 Hz slow motion still gets a step a frame.
            float step = fixedStepInSlowMotion ? physicsStep : physicsStep * Mathf.Clamp(scale, 0.25f, 1f);
            if (!Mathf.Approximately(Time.fixedDeltaTime, step)) Time.fixedDeltaTime = step;
        }

        /// <summary>Slows the fight for a moment. Real seconds, so the length does not depend on the scale.</summary>
        public void SlowMo(float scale, float realSeconds)
        {
            if (Phase != BoutPhase.Fight && Phase != BoutPhase.Count) return;
            _slowScale = Mathf.Clamp(scale, 0.1f, 1f);
            _slowUntil = Mathf.Max(_slowUntil, Time.unscaledTime + realSeconds);
        }

        void OnKnockdown(Fighter f, HitEvent cause)
        {
            if (Phase != BoutPhase.Fight) return;
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
            if (Round >= rounds)
            {
                float r = Score(red), b = Score(blue);
                if (Mathf.Abs(r - b) < 2f) Finish(null, $"DRAW {r:0}-{b:0}", false);
                else if (r > b) Finish(red, $"POINTS {r:0}-{b:0}", false);
                else Finish(blue, $"POINTS {b:0}-{r:0}", false);
                return;
            }
            SimBus.Say($"End of round {Round}: {Leader()}", 2);
            SetPhase(BoutPhase.RoundBreak);
        }

        void Finish(Fighter winner, string method, bool knockout)
        {
            Winner = winner;
            Method = method;
            if (league != null)
            {
                float score = winner == null ? 0.5f : winner == red ? 1f : 0f;
                EloDelta = league.Report(red.displayName, blue.displayName, score, knockout);
            }
            SimBus.Say(winner != null ? $"{winner.displayName} wins: {method}" : method, 3);
            SetPhase(BoutPhase.Results);
        }

        // ------------------------------------------------------------ helpers

        public Fighter Other(Fighter f) => f == red ? blue : red;

        /// <summary>What a judge would add up: damage done, with a knockdown worth a clear round.</summary>
        public static float Score(Fighter f) => f == null ? 0f : f.stats.damageDealt + 15f * f.stats.knockdowns;

        string Leader()
        {
            float r = Score(red), b = Score(blue);
            if (Mathf.Abs(r - b) < 2f) return "level";
            return r > b ? $"{red.displayName} ahead {r:0}-{b:0}" : $"{blue.displayName} ahead {b:0}-{r:0}";
        }

        public static string ClockText(float seconds)
        {
            int s = Mathf.Max(0, Mathf.CeilToInt(seconds - 0.001f));
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
