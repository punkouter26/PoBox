using System;
using System.Collections.Generic;
using System.IO;
using PoBox.Sim;
using UnityEngine;

namespace PoBox.League
{
    /// <summary>
    /// The fight log: every finished bout as one line of JSON in Application.persistentDataPath/fightlog.ndjson, for
    /// looking at the boxers over time (AGENTS.md: log agent actions to analyse movement profiles and results). Each
    /// line holds the result (winner, method, cards, Elo change), which brain each corner ran, and per corner what it
    /// did: punches thrown and landed by kind, where they landed, impulse, blocks, knockdowns, and how it moved
    /// (metres walked, seconds on the ropes and chest to chest, how hard each group of joints was driven, the
    /// lowest reserve), plus one sample a second of where it stood and how hard it was working.
    ///
    /// Only the 1-on-1 (Bout); the CPU bouts the trainer boxes for the ratings have their own log
    /// (training/logs/cpu_bouts.ndjson, tools/elo.py). ponytail: one file that only grows; split it by month
    /// if it ever gets in the way.
    /// </summary>
    public class FightLog : MonoBehaviour
    {
        public Bout bout;
        /// <summary>Pelvises closer than this are chest to chest, metres.</summary>
        public float clinchRange = 0.55f;
        /// <summary>Fewer seconds of fighting than this and the bout is not logged.</summary>
        public const float MinSeconds = 10f;

        public static string FilePath => Path.Combine(Application.persistentDataPath, "fightlog.ndjson");

        [Serializable]
        class Sample { public float t, x, z, speed, reserve, effort; }

        [Serializable]
        class Corner
        {
            public string name, brain;
            public int thrown, landed, head, body, blocked, knockdowns, downs;
            public int[] thrownBy = new int[5], landedBy = new int[5];   // Jab, Cross, Hook, Uppercut, Body
            public float impulse, peakImpulse, energyJ, metres, ropesS, clinchS, lowestReserve = 1f;
            public float[] effort = new float[4];                       // mean |action|: legs, torso, left arm, right arm
            public List<Sample> samples = new List<Sample>();
            [NonSerialized] public Vector3 last;
            [NonSerialized] public float steps, nextSample;
        }

        [Serializable]
        class Record
        {
            public string when, winner, method, cards;
            public bool knockout;
            public int rounds, boutNumber;
            public float seconds, eloDelta;
            public Corner red, blue;
        }

        Record _rec;
        int _bout = -1;
        readonly float[] _sum = new float[4];
        readonly int[] _n = new int[4];

        void OnEnable()
        {
            SimBus.PunchThrown += OnThrown;
            SimBus.Hit += OnHit;
            SimBus.PhaseChanged += OnPhase;
        }

        void OnDisable()
        {
            SimBus.PunchThrown -= OnThrown;
            SimBus.Hit -= OnHit;
            SimBus.PhaseChanged -= OnPhase;
        }

        Corner Of(Fighter f) => _rec == null ? null : f == bout.red ? _rec.red : f == bout.blue ? _rec.blue : null;

        void Begin()
        {
            _bout = bout.BoutNumber;
            _rec = new Record { boutNumber = bout.BoutNumber, red = NewCorner(bout.red), blue = NewCorner(bout.blue) };
        }

        static Corner NewCorner(Fighter f)
        {
            var c = new Corner { name = f != null ? f.displayName : "" };
            if (f != null && f.boxer != null)
            {
                var brain = f.boxer.matchPolicy != null ? f.boxer.matchPolicy : f.boxer.Running;
                c.brain = brain != null ? $"{f.boxer.Cfg.name}/{brain.name}" : "";
            }
            if (f != null && f.pelvis != null) c.last = f.pelvis.transform.position;
            return c;
        }

        void OnThrown(Fighter f, PunchType t, int hand)
        {
            Corner c = Of(f);
            if (c == null) return;
            c.thrown++;
            c.thrownBy[(int)t]++;
        }

        void OnHit(HitEvent e)
        {
            Corner c = Of(e.attacker);
            if (c == null || !e.clean) return;
            c.landed++;
            c.landedBy[(int)e.punch]++;
            if (e.zone == PartKind.Head) c.head++; else c.body++;
            c.impulse += e.impulse;
        }

        void FixedUpdate()
        {
            if (bout == null || bout.red == null || bout.blue == null) return;
            if (bout.BoutNumber != _bout) Begin();
            if (bout.Phase != BoutPhase.Fight) return;
            float dt = Time.fixedDeltaTime;
            for (int k = 0; k < 2; k++)
            {
                Fighter f = k == 0 ? bout.red : bout.blue;
                Corner c = k == 0 ? _rec.red : _rec.blue;
                if (f.pelvis == null) continue;
                Vector3 p = f.pelvis.transform.position;
                Vector3 step = p - c.last;
                step.y = 0f;
                c.last = p;
                bool moved = step.magnitude < 0.5f;                       // not a boxer put back on its spot
                if (moved) c.metres += step.magnitude;
                if (f.OnTheRopes) c.ropesS += dt;
                Vector3 gap = bout.Other(f).pelvis.transform.position - p;
                gap.y = 0f;
                if (gap.magnitude < clinchRange && !f.IsDown) c.clinchS += dt;
                Mj.MjBoxer body = f.boxer;
                float effort = 0f;
                if (body != null && body.Bound && body.Action != null)
                {
                    string[] joints = body.Cfg.joint_order;
                    Array.Clear(_sum, 0, 4);
                    Array.Clear(_n, 0, 4);
                    for (int j = 0; j < body.Action.Length && j < joints.Length; j++)
                    {
                        int g = UI.HudView.GroupOf(joints[j]);
                        float a = Mathf.Abs((float)body.Action[j]);
                        _sum[g] += a; _n[g]++;
                        effort += a;
                    }
                    for (int g = 0; g < 4; g++) if (_n[g] > 0) c.effort[g] += _sum[g] / _n[g];
                    effort /= Mathf.Max(1, body.Action.Length);
                    c.lowestReserve = Mathf.Min(c.lowestReserve, (float)body.Reserve);
                }
                c.steps++;
                if (Bout.Clock >= c.nextSample)
                {
                    c.nextSample = Bout.Clock + 1f;
                    c.samples.Add(new Sample
                    {
                        t = Round2(Bout.Clock), x = Round2(p.x), z = Round2(p.z), speed = moved ? Round2(step.magnitude / dt) : 0f,
                        reserve = body != null ? Round2((float)body.Reserve) : 1f, effort = Round2(effort),
                    });
                }
            }
        }

        static float Round2(float v) => Mathf.Round(v * 100f) / 100f;

        void OnPhase(BoutPhase from, BoutPhase to)
        {
            if (to != BoutPhase.Results || _rec == null || bout == null || bout.BoutNumber != _bout) return;
            // A bout with under MinSeconds of fighting is not one to learn from (the play-mode tests box half-second bouts).
            if (Bout.Clock < MinSeconds) { _rec = null; return; }
            _rec.when = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _rec.winner = bout.Winner != null ? bout.Winner.displayName : "";
            _rec.method = bout.Method;
            _rec.knockout = !bout.Decision && bout.Winner != null;
            _rec.cards = bout.CardsText();
            _rec.rounds = bout.Round;
            _rec.seconds = Round2(Bout.Clock);
            _rec.eloDelta = bout.EloDelta;
            for (int k = 0; k < 2; k++)
            {
                Fighter f = k == 0 ? bout.red : bout.blue;
                Corner c = k == 0 ? _rec.red : _rec.blue;
                c.blocked = f.stats.blocked;
                c.knockdowns = f.stats.knockdowns;
                c.downs = f.stats.downs;
                c.peakImpulse = f.stats.peakImpulse;
                c.energyJ = Mathf.Round(f.stats.energyJ);
                c.metres = Round2(c.metres);
                c.ropesS = Round2(c.ropesS);
                c.clinchS = Round2(c.clinchS);
                for (int g = 0; g < 4; g++) c.effort[g] = Round2(c.effort[g] / Mathf.Max(1f, c.steps));
            }
            try { File.AppendAllText(FilePath, JsonUtility.ToJson(_rec) + "\n"); }
            catch (Exception ex) { Debug.LogWarning($"fight log: {ex.Message}"); }
            _rec = null;
        }
    }
}
