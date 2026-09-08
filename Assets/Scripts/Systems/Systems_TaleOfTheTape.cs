using System.Collections.Generic;
using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// The pre-round card: who is fighting, and what their brain actually is.
    ///
    /// This is the one piece of spectator furniture that no other physics
    /// fighting game could put up, because in this one the contestants have
    /// real provenance. "Locomotion_gen25 · 4.5M steps · 127 obs · 167.9 steps
    /// between falls" against "Heuristic PD bot · hand-written · no brain" is a
    /// tale of the tape in the boxing sense, and it is also the whole argument
    /// of the project: the red bot is the floor every policy has to clear, and
    /// it is not a soft one.
    ///
    /// IT REPORTS WHAT THE FIGHTER GOT, NOT WHAT THE ROSTER ASKED FOR. A brain
    /// whose obs_0 is a different width than the fighter emits is REFUSED by
    /// <see cref="Systems_BrainCompatibility"/> and replaced with the heuristic
    /// bot — deliberately, and in a build as well as in the editor. That
    /// substitution used to be visible only as one line in a console nobody
    /// reads on a phone. Here it is on the card, because a contest that
    /// silently ran a different fighter than the one advertised is exactly the
    /// failure this project has already shipped once.
    ///
    /// Plays over the round countdown, which freezes time for 2.9 s and is
    /// therefore free screen time; sits BELOW the countdown's own numerals
    /// (top 42%, 220 px) rather than over them. Shares the referee's UIDocument
    /// for the same reason the announcer's hazard chip does — a second
    /// UIDocument is a second panel, and two HUD stacks land on top of each
    /// other. Test-scene harness only.
    /// </summary>
    public sealed class Systems_TaleOfTheTape : MonoBehaviour
    {
        /// <summary>
        /// Held for slightly less than the countdown's 2.9 s (3 ticks of 0.8 s
        /// plus a 0.5 s GO linger), so the card is gone by the time the fighters
        /// are released rather than covering the first second of the round.
        /// </summary>
        private const float HOLD_SECONDS = 2.6f;
        private const float FADE_SECONDS = 0.35f;

        /// <summary>
        /// Top of the card, as a fraction of panel height. Below the countdown
        /// numerals — see the class comment — and above the scoreboard plates
        /// the referee keeps at the bottom.
        /// </summary>
        private const float CARD_TOP_PERCENT = 57f;

        private static readonly Color RefusedRed = new(1f, 0.42f, 0.38f);

        private Systems_ContestReferee _contest;
        private Systems_ContestSpawner _spawner;
        private Systems_SpectatorKit _kit;
        private VisualElement _card;
        private float _remaining = -1f;

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
            _spawner = FindFirstObjectByType<Systems_ContestSpawner>();
            _kit = Systems_SpectatorKit.Load();

            BuildCard(document.rootVisualElement);
            _contest.RoundStarted += OnRoundStarted;
            // Round one has already started by the time this runs — the referee
            // raises RoundStarted from its own Start, which may be before or
            // after this one — so the first card is shown unconditionally rather
            // than waiting for an event that may already have gone past.
            Show();
        }

        private void OnDestroy()
        {
            if (_contest != null)
            {
                _contest.RoundStarted -= OnRoundStarted;
            }
        }

        private void OnRoundStarted(int round)
        {
            Show();
        }

        private void Show()
        {
            if (_card == null)
            {
                return;
            }
            Populate();
            _remaining = HOLD_SECONDS;
            _card.style.display = DisplayStyle.Flex;
            _card.style.opacity = 1f;
        }

        private void Update()
        {
            if (_remaining < 0f || _card == null)
            {
                return;
            }
            // Unscaled: the countdown this plays over parks timeScale at 0.
            _remaining -= Time.unscaledDeltaTime;
            if (_remaining <= 0f)
            {
                _remaining = -1f;
                _card.style.display = DisplayStyle.None;
                return;
            }
            if (_remaining < FADE_SECONDS)
            {
                _card.style.opacity = _remaining / FADE_SECONDS;
            }
        }

        private void BuildCard(VisualElement root)
        {
            _card = new VisualElement();
            _card.style.position = Position.Absolute;
            _card.style.top = Length.Percent(CARD_TOP_PERCENT);
            _card.style.left = 24f;
            _card.style.right = 24f;
            _card.style.paddingTop = 14f;
            _card.style.paddingBottom = 14f;
            _card.style.paddingLeft = 18f;
            _card.style.paddingRight = 18f;
            _card.style.backgroundColor = Systems_UiTheme.PanelDark;
            Systems_UiTheme.SetRadius(_card, 16f);
            Systems_UiTheme.SetBorderWidth(_card, 2f);
            Color border = Systems_UiTheme.Gold;
            border.a = 0.5f;
            Systems_UiTheme.SetBorderColor(_card, border);
            _card.pickingMode = PickingMode.Ignore;
            _card.style.display = DisplayStyle.None;
            root.Add(_card);
        }

        /// <summary>
        /// Rebuilt per round rather than updated in place: the roster does not
        /// change between rounds, but a refused brain is a per-FIGHTER fact and
        /// the ring is respawned, so nothing here is safe to cache across a
        /// round boundary.
        /// </summary>
        private void Populate()
        {
            _card.Clear();

            var title = new Label("TALE OF THE TAPE");
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.fontSize = 34f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Systems_UiTheme.GoldBright;
            title.style.marginBottom = 10f;
            title.pickingMode = PickingMode.Ignore;
            _card.Add(title);

            List<Systems_FighterIdentity> fighters = OrderedFighters();
            var summary = new List<string>(fighters.Count);
            for (int fighterIndex = 0; fighterIndex < fighters.Count; fighterIndex++)
            {
                Systems_FighterIdentity fighter = fighters[fighterIndex];
                _card.Add(BuildRow(fighter));
                Describe(fighter, out string spec, out _);
                summary.Add($"{fighter.DisplayName}={spec}");
            }
            // Logged for the same reason Systems_BalanceContest logs
            // CONTEST_ROUND: the card itself is on screen for 2.6 s during a
            // time-frozen countdown, which is exactly the window an offline
            // capture cannot sample, so this is the only way to check from
            // outside that the right brain was matched to the right fighter —
            // and the only way a refusal is greppable after the fact.
            Debug.Log("TALE_OF_THE_TAPE | " + string.Join(" | ", summary));
        }

        /// <summary>
        /// Fighters in a stable order. FindObjectsByType with InstanceID sorting
        /// is the same order every other presentation system here discovers them
        /// in, so the card matches the scoreboard rather than shuffling.
        /// </summary>
        private static List<Systems_FighterIdentity> OrderedFighters()
        {
            var identities = FindObjectsByType<Systems_FighterIdentity>(FindObjectsSortMode.InstanceID);
            var ordered = new List<Systems_FighterIdentity>(identities.Length);
            for (int index = 0; index < identities.Length; index++)
            {
                if (identities[index] != null)
                {
                    ordered.Add(identities[index]);
                }
            }
            return ordered;
        }

        private VisualElement BuildRow(Systems_FighterIdentity identity)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 6f;
            row.pickingMode = PickingMode.Ignore;

            var swatch = new VisualElement();
            swatch.style.width = 18f;
            swatch.style.height = 40f;
            swatch.style.backgroundColor = identity.PlateColor;
            Systems_UiTheme.SetRadius(swatch, 4f);
            swatch.style.marginRight = 12f;
            swatch.pickingMode = PickingMode.Ignore;
            row.Add(swatch);

            var name = new Label(identity.DisplayName);
            name.style.fontSize = 30f;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.color = Color.white;
            name.style.flexGrow = 1f;
            name.style.flexShrink = 1f;
            name.style.overflow = Overflow.Hidden;
            name.pickingMode = PickingMode.Ignore;
            row.Add(name);

            Describe(identity, out string spec, out bool refused);
            var detail = new Label(spec);
            detail.style.fontSize = 24f;
            detail.style.color = refused ? RefusedRed : Systems_UiTheme.Gold;
            detail.style.unityTextAlign = TextAnchor.MiddleRight;
            detail.style.flexShrink = 0f;
            detail.pickingMode = PickingMode.Ignore;
            row.Add(detail);

            return row;
        }

        /// <summary>
        /// The one line that says what this fighter is running, resolved from
        /// three sources that can disagree: the roster entry says which brain
        /// was ASKED for, the BehaviorParameters on the spawned instance say
        /// what it is actually running, and the dossier says what that brain is
        /// known to be worth. Where the first two disagree the fighter was
        /// refused its brain, and that is what gets reported.
        /// </summary>
        private void Describe(Systems_FighterIdentity identity, out string spec, out bool refused)
        {
            refused = false;
            ContestRosterEntry entry = ResolveEntry(identity);
            var behavior = identity.GetComponent<BehaviorParameters>();
            bool runningHeuristic = behavior == null || behavior.BehaviorType == BehaviorType.HeuristicOnly;

            // The roster says what was ASKED for and is the only thing that can
            // reveal a refusal — but it is reachable only for a fighter the
            // spawner created. `Tools/ML Boxing/15` places the roster into the
            // scene at author time instead, and those fighters were serialized
            // before Systems_FighterIdentity recorded a roster index, so they
            // carry -1 forever. Their BehaviorParameters still hold the real
            // ModelAsset, which is the better source anyway: it is what the
            // fighter is ACTUALLY running rather than what a roster entry hoped.
            if (entry != null && !entry.forceHeuristic && entry.model != null && runningHeuristic)
            {
                // Asked for a brain, running the bot: Systems_BrainCompatibility
                // threw it out. Name the brain anyway so the mismatch is
                // diagnosable from the card alone.
                refused = true;
                int width = Systems_BrainCompatibility.ObservationWidth(entry.model);
                spec = $"{entry.model.name} REFUSED ({width} obs) · running bot";
                return;
            }

            if (runningHeuristic)
            {
                // The mandated red bot. Its zero is the point of the row: it is
                // the floor, and it beats trained policies often enough that
                // saying so is not a joke.
                spec = "heuristic PD bot · 0 steps · hand-written";
                return;
            }

            ModelAsset model = behavior.Model != null ? behavior.Model : entry?.model;
            if (model == null)
            {
                spec = "brain · unknown provenance";
                return;
            }

            Systems_BrainDossier dossier = _kit != null ? _kit.Find(model) : null;
            // Read from the model itself rather than from the dossier: brain
            // folder names have historically lied about which generation they
            // hold, and obs_0's width is the one thing that cannot.
            int observations = Systems_BrainCompatibility.ObservationWidth(model);
            string label = dossier != null && !string.IsNullOrEmpty(dossier.brainLabel)
                ? dossier.brainLabel
                : model.name;

            var parts = new List<string> { label };
            if (dossier != null && dossier.trainingSteps > 0)
            {
                parts.Add(dossier.StepsDisplay + " steps");
            }
            if (observations > 0)
            {
                parts.Add(observations + " obs");
            }
            if (dossier != null && !string.IsNullOrEmpty(dossier.headlineStat))
            {
                parts.Add(dossier.headlineStat);
            }
            spec = string.Join(" · ", parts);
        }

        private ContestRosterEntry ResolveEntry(Systems_FighterIdentity identity)
        {
            if (_spawner == null)
            {
                return null;
            }
            ContestRosterEntry[] roster = _spawner.Roster;
            int index = identity.RosterIndex;
            if (roster == null || index < 0 || index >= roster.Length)
            {
                return null;
            }
            return roster[index];
        }
    }
}
