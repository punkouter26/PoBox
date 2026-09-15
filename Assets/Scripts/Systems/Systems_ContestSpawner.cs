using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// One selectable fighter kind for the contest setup menu: a prefab and
    /// an optional tint that marks it in the ring. The prefab carries its own
    /// brain (a MuJoCo creature loads its policy through
    /// CreatureSentisController). Empty in both shipping scenes since the
    /// PhysX cast was removed on 2026-09-14: every contestant stands in the
    /// scene at author time and is adopted rather than spawned.
    /// </summary>
    [Serializable]
    public sealed class ContestRosterEntry
    {
        public string displayName;
        public GameObject prefab;
        public Material tint;
    }

    /// <summary>
    /// Spawns the fighters chosen in the setup menu into the contest slots,
    /// then wakes the sleeping contest systems root (referee, FX, cube
    /// thrower) and the drama camera — those discover fighters in their own
    /// Start. Test-scene harness only.
    /// </summary>
    public sealed class Systems_ContestSpawner : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        /// <summary>
        /// World Y of the ring canvas the fighters stand on. Assets/Art/BoxingRing.glb
        /// carries its canvas 1 m above the model origin, so a ring placed at y = 0
        /// puts its floor here and reads as the raised ring a real bout is fought in.
        /// Ground colliders, spawns and cameras all follow this number. Safe to change
        /// only because height observations are ground-relative (Systems_FighterRig.GroundY).
        /// </summary>
        public const float RING_FLOOR_Y = 1f;

        /// <summary>Decision interval every shipped brain was trained at.</summary>
        private const float TRAINED_DECISION_SECONDS = 0.02f;

        private const float SPAWN_HEIGHT = RING_FLOOR_Y + 0.03f;

        /// <summary>
        /// 8 slots inside the 6.1 m canvas: four ranks of two, running AWAY from
        /// the camera. Nearest rank first, so a part-filled ring fills from the
        /// front and a single spawn stays filmable.
        ///
        /// It was the other way round — two ranks of four — and that layout is
        /// what made the balance contest unwatchable on a phone, for a reason no
        /// amount of camera tuning could fix. A camera has to frame the whole
        /// field, a portrait 9:16 frame is 1.78x taller than it is wide, and the
        /// vertical span the shot ends up with is therefore the horizontal span
        /// it needs DIVIDED BY 0.5625. Four fighters abreast are 4.2 m wide, so
        /// the shot was always going to be 9.2 m tall whatever the FOV or the
        /// distance, and a 1.7 m fighter is 18% of that. Measured 2026-08-22:
        /// the ring came back a quarter of the frame with the arena's unlit
        /// ceiling filling the top third.
        ///
        /// Turned through 90 degrees the field is 1.5 m wide and 4.2 m deep. The
        /// frame it needs is now barely wider than one fighter, and the depth
        /// does not cost frame width at all — perspective spreads the four ranks
        /// UP the picture, which is the one axis a portrait frame has to spare.
        /// The near rank comes out about a third of the frame tall against the
        /// old 18%, and the ranks behind it recede instead of lining up.
        /// </summary>
        private static readonly Vector3[] SlotPositions =
        {
            new(-0.75f, SPAWN_HEIGHT, 2.1f), new(0.75f, SPAWN_HEIGHT, 2.1f),
            new(-0.75f, SPAWN_HEIGHT, 0.7f), new(0.75f, SPAWN_HEIGHT, 0.7f),
            new(-0.75f, SPAWN_HEIGHT, -0.7f), new(0.75f, SPAWN_HEIGHT, -0.7f),
            new(-0.75f, SPAWN_HEIGHT, -2.1f), new(0.75f, SPAWN_HEIGHT, -2.1f)
        };

        [SerializeField] private ContestRosterEntry[] _roster;
        [SerializeField] private GameObject _systemsRoot;
        [SerializeField] private Systems_DramaCamera _dramaCamera;
        [SerializeField] private Systems_MenuOrbitCamera _menuOrbit;
        // Overrides the two-row ring layout above. Left empty by the balance
        // contest so scenes built before this field deserialize unchanged; the
        // walk contest sets a 4-wide start line.
        [SerializeField] private Vector3[] _slotPositionsOverride = System.Array.Empty<Vector3>();
        // Facing for spawned fighters. Identity faces +Z, which is the walk
        // race's direction of travel and the balance ring's camera side.
        [SerializeField] private Vector3 _spawnEuler = Vector3.zero;

        public ContestRosterEntry[] Roster => _roster;
        public int SlotCount => ActiveSlots.Length;

        /// <summary>Where slot <paramref name="index"/> stands, for author-time placement.</summary>
        public Vector3 SlotPosition(int index)
        {
            Vector3[] slots = ActiveSlots;
            return slots[Mathf.Clamp(index, 0, slots.Length - 1)];
        }

        /// <summary>Facing every fighter starts a round in.</summary>
        public Quaternion SpawnRotation => Quaternion.Euler(_spawnEuler);

        private Vector3[] ActiveSlots =>
            _slotPositionsOverride != null && _slotPositionsOverride.Length > 0
                ? _slotPositionsOverride
                : SlotPositions;

        // Called by the editor scene tool.
        public void EditorSetSlots(Vector3[] slotPositions, Vector3 spawnEuler)
        {
            _slotPositionsOverride = slotPositions;
            _spawnEuler = spawnEuler;
        }

        // Called by the editor scene tool.
        public void EditorInitialize(ContestRosterEntry[] roster, GameObject systemsRoot, Systems_DramaCamera dramaCamera)
        {
            _roster = roster;
            _systemsRoot = systemsRoot;
            _dramaCamera = dramaCamera;
        }

        /// <summary>
        /// Per-scene reset of the static line-up count, before any contestant
        /// exists.
        /// </summary>
        private void Awake()
        {
            // Reset per scene. This is static state read by another scene's HUD, so
            // a value left over from a contest would be reported over a scene that
            // has not fielded anyone yet.
            LastFieldedCount = 0;

            // HERE AS WELL AS IN SpawnAndBegin, BECAUSE HALF THE CONTEST SCENES
            // NEVER SPAWN ANYTHING. `Tools/ML Boxing/15` places the roster into
            // the scene at author time and disables Systems_MiniGameLauncher, so
            // SCN_TEST_BALANCE_CONTEST ships with five Contest_* fighters and
            // Nick already in it and SpawnAndBegin is never called. Hooking only
            // the spawn path meant the spectator systems were silently absent
            // from exactly the scene that ships — the contest ran, the HUD and
            // referee worked, and nothing announced that three systems had never
            // been created.
            //
            // Both call sites are needed and neither is redundant. In an
            // author-placed scene the systems root is already active, so
            // components added now get their Start next frame with the fighters
            // already in the scene. In a spawned scene the root is still asleep
            // here and the fighters do not exist yet, so this adds them and the
            // call in SpawnAndBegin is the no-op — but that call is what
            // guarantees the ordering when the root reference is only resolved
            // by then. EnsureSpectatorSystems is idempotent, so running twice
            // costs one GetComponentInChildren each.
            EnsureSpectatorSystems(_systemsRoot);
        }

        /// <summary>
        /// How many fighters the last line-up actually fielded, or 0 in a scene
        /// that has not spawned anyone.
        ///
        /// Published for the device HUD, which worked this out for itself by
        /// sweeping every MonoBehaviour in the scene looking for IContestFighter
        /// implementations — twice a second, allocating the whole component array
        /// each time, in every scene including the menu, in the build whose open
        /// item is frame rate. The spawner already knows the number exactly, at the
        /// moment it knows it.
        /// </summary>
        public static int LastFieldedCount { get; private set; }

        /// <summary>
        /// Fields the line-up the menu chose — one NAME per slot, with
        /// <see cref="Systems_FighterIdentity.EmptyPick"/> leaving a slot empty
        /// — and then starts the contest.
        ///
        /// ADOPTS BEFORE IT SPAWNS. Both ships' scenes already contain one of
        /// every fighter, standing on the marks the default line-up would put
        /// them on, because they were authored that way before this path was
        /// reachable. Spawning the line-up on top of them would double the ring:
        /// six authored bodies plus six built ones, two of each name, and a
        /// referee that discovers twelve contestants. So a slot is filled by the
        /// author-placed fighter of that name when one is spare, and only
        /// instantiated from the roster when there is none — which is the case
        /// for a repeat of a name already adopted, and for a name the scene does
        /// not place by hand.
        ///
        /// Unused author-placed fighters are STOOD DOWN, not destroyed. They
        /// stay in the scene, inert and selectable in the Inspector, so the
        /// authored cast can be inspected or restored by hand; destroying them
        /// would also invalidate every reference the cameras and spectator
        /// systems took to them when the scene loaded.
        ///
        /// A fighter that is adopted is MOVED to its slot, and that is safe for
        /// a PhysX rig precisely because <c>ResetToStartPose</c> works in LOCAL
        /// space — the captured start pose describes the body's own hierarchy, so
        /// it stays valid wherever the root is put.
        /// </summary>
        public void SpawnAndBegin(string[] pickNames)
        {
            pickNames = pickNames ?? System.Array.Empty<string>();
            int spawned = 0;
            int adopted = 0;
            bool requestedAnyFighter = false;
            var unfilled = new List<string>();
            Vector3[] slots = ActiveSlots;
            Quaternion spawnRotation = Quaternion.Euler(_spawnEuler);
            var nameCounts = new int[_roster.Length];
            List<PlacedFighter> placed = CollectPlacedFighters();
            // Captured before the fallback below runs, because the fallback is
            // what makes the "nothing was fielded" test unreachable if it is asked
            // afterwards: it always leaves at least one fighter standing, so the
            // condition that is supposed to warn about an empty ring could never be
            // true. See the diagnostics after the spawn loop.
            bool nothingFielded;
            // Fighters are built under an INACTIVE holder so Awake and OnEnable are
            // deferred until Configure has run. Instantiating straight into the scene
            // runs the Agent's LazyInitialize on the spot, which snapshots
            // BrainParameters and builds the VectorSensor from it — so the locomotion
            // observation-size fix in Configure arrived one frame too late and every
            // step logged "More observations (127) made than vector observation size
            // (121). The observations will be truncated." Measured 2026-08-20: 44,429
            // of them in one editor session, with the walking brain reading 6 junk
            // inputs. A GameObject parented to an inactive object never fires Awake,
            // so releasing it afterwards is what finally starts the agent.
            var holder = new GameObject("ContestSpawnHolder");
            holder.SetActive(false);
            try
            {
                for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
                {
                    string pick = slotIndex < pickNames.Length
                        ? pickNames[slotIndex]
                        : Systems_FighterIdentity.EmptyPick;
                    if (string.IsNullOrEmpty(pick) || pick == Systems_FighterIdentity.EmptyPick)
                    {
                        continue;
                    }
                    requestedAnyFighter = true;

                    PlacedFighter standing = TakePlacedFighter(placed, pick);
                    if (standing != null)
                    {
                        standing.host.SetActive(true);
                        standing.host.transform.SetPositionAndRotation(slots[slotIndex], spawnRotation);
                        adopted++;
                        continue;
                    }

                    int rosterIndex = SpawnableRosterIndex(pick);
                    if (rosterIndex < 0)
                    {
                        // A name with neither a body in the scene nor a prefab to
                        // build one from. Reported rather than skipped quietly:
                        // this is the failure the old index-based picks produced
                        // with no message at all.
                        unfilled.Add($"{pick} (slot {slotIndex + 1})");
                        continue;
                    }

                    ContestRosterEntry entry = _roster[rosterIndex];
                    nameCounts[rosterIndex]++;
                    string instanceName = nameCounts[rosterIndex] > 1
                        ? $"Contest_{entry.displayName}{nameCounts[rosterIndex]}"
                        : $"Contest_{entry.displayName}";
                    Spawn(entry, holder.transform, slots[slotIndex], spawnRotation, instanceName,
                        nameCounts[rosterIndex] - 1, rosterIndex);
                    spawned++;
                }

                nothingFielded = spawned == 0 && adopted == 0;
                if (nothingFielded && _roster.Length > 0
                    && _roster[0] != null && _roster[0].prefab != null)
                {
                    // Never start an empty ring — fall back to one default fighter.
                    //
                    // This also covers a line-up of nothing but EMPTY choices.
                    // Honouring that literally would leave a ring with no
                    // contestants, and the contest has no way out of one: no
                    // round can end, so no champion is crowned and the scene is
                    // never reloaded. One fighter on the mat is a worse match than
                    // the player asked for and a far better one than a dead scene.
                    //
                    // Guarded on the prefab because Instantiate(null) throws, and a
                    // throw here would land inside the spawn holder's try block and
                    // take the whole line-up with it.
                    Spawn(_roster[0], holder.transform, slots[0], spawnRotation,
                        $"Contest_{_roster[0].displayName}", 0, 0);
                    spawned++;
                }
            }
            finally
            {
                // Without this a throw inside Configure strands the inactive holder
                // — and the half-built fighter inside it — in the scene for good.
                Destroy(holder);
            }

            int stoodDown = StandDownUnused(placed);
            LastFieldedCount = adopted + spawned;

            // One greppable line per contest, in the same spirit as CONTEST_ROUND
            // and WALK_RESULT: "did the line-up I chose actually take the mat" was
            // otherwise only answerable by watching the screen.
            Debug.Log($"CONTEST_LINEUP | fielded={adopted + spawned} " +
                $"(adopted={adopted} spawned={spawned} stoodDown={stoodDown}) | slots={slots.Length}");

            if (unfilled.Count > 0)
            {
                Debug.LogError($"Systems_ContestSpawner: the line-up asks for {unfilled.Count} " +
                    $"fighter(s) this scene cannot field — {string.Join(", ", unfilled)}. " +
                    "Give that name a roster entry with a prefab, or place it in the scene. " +
                    "(The menu's list is Systems_FighterIdentity.PickableNames; the scene's is " +
                    "this spawner's roster.)");
            }
            if (nothingFielded)
            {
                Debug.LogError(requestedAnyFighter
                    ? "Systems_ContestSpawner: a line-up was requested and nothing could be fielded for it, " +
                      "so the ring is holding a stand-in. Check that the roster's prefabs are assigned and " +
                      "that the fighters it names are in the scene."
                    : "Systems_ContestSpawner: every slot was left empty, so the ring is holding a stand-in " +
                      "rather than nothing at all — see the comment on that fallback for why an empty ring " +
                      "is not an option.");
            }

            if (_systemsRoot != null)
            {
                // Added while the root is still asleep, so their Start runs when
                // it wakes — with a full ring to discover, exactly like the
                // announcer and the hazard director beside them.
                EnsureSpectatorSystems(_systemsRoot);
                _systemsRoot.SetActive(true);
            }
            if (_menuOrbit != null)
            {
                _menuOrbit.enabled = false;
            }
            if (_dramaCamera != null)
            {
                _dramaCamera.enabled = true;
            }
        }

        /// <summary>
        /// One fighter that was standing in the scene when the contest began,
        /// with the name it is known by.
        /// </summary>
        private sealed class PlacedFighter
        {
            public string name;
            public GameObject host;
        }

        /// <summary>
        /// Every fighter already in the scene, by display name: the PhysX rigs,
        /// and anything answering <see cref="IContestFighter"/> — which is how
        /// Nick gets into this list at all, having no prefab to be spawned from
        /// and existing only as an authored object.
        /// </summary>
        private static List<PlacedFighter> CollectPlacedFighters()
        {
            var placed = new List<PlacedFighter>();
            foreach (Systems_FighterRig rig in FindObjectsByType<Systems_FighterRig>(FindObjectsInactive.Include))
            {
                Systems_FighterIdentity.Resolve(rig, out string name, out _);
                placed.Add(new PlacedFighter { name = name, host = rig.gameObject });
            }
            foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
            {
                if (behaviour is IContestFighter fighter)
                {
                    placed.Add(new PlacedFighter { name = fighter.DisplayName, host = behaviour.gameObject });
                }
            }
            return placed;
        }

        /// <summary>
        /// Claims the first spare fighter of this name, REMOVING it from the
        /// list so a second pick of the same name finds none and is built
        /// instead. That is what makes a line-up of two of a kind work: the
        /// first is the body already on the mat, the second is a new one.
        /// </summary>
        private static PlacedFighter TakePlacedFighter(List<PlacedFighter> placed, string name)
        {
            for (int index = 0; index < placed.Count; index++)
            {
                PlacedFighter candidate = placed[index];
                if (candidate.host == null || !string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                placed.RemoveAt(index);
                return candidate;
            }
            return null;
        }

        /// <summary>
        /// Stands down every fighter the line-up did not claim. Called after the
        /// slots are filled, so what is left in the list is exactly what nobody
        /// asked to fight.
        /// </summary>
        private static int StandDownUnused(List<PlacedFighter> placed)
        {
            int stoodDown = 0;
            for (int index = 0; index < placed.Count; index++)
            {
                GameObject host = placed[index].host;
                if (host == null || !host.activeSelf)
                {
                    continue;
                }
                host.SetActive(false);
                stoodDown++;
            }
            return stoodDown;
        }

        /// <summary>
        /// Roster index that can BUILD a fighter of this name, or -1 for a name
        /// the roster cannot produce — which is every name the scene must supply
        /// itself, Nick among them.
        /// </summary>
        private int SpawnableRosterIndex(string name)
        {
            for (int rosterIndex = 0; rosterIndex < _roster.Length; rosterIndex++)
            {
                ContestRosterEntry entry = _roster[rosterIndex];
                if (entry != null && entry.prefab != null
                    && string.Equals(entry.displayName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return rosterIndex;
                }
            }
            return -1;
        }

        // Called by the editor scene tool.
        public void EditorSetMenuOrbit(Systems_MenuOrbitCamera menuOrbit)
        {
            _menuOrbit = menuOrbit;
        }

        /// <summary>
        /// Attaches the spectator systems that carry no scene state of their
        /// own: the joint-stress heatmap, the impulse-scaled impact FX, the
        /// pre-round tale of the tape and the colour-commentary band.
        ///
        /// WIRED AT RUNTIME RATHER THAN PLACED IN THE SCENE, ON PURPOSE. The
        /// contest scenes are generated artifacts and the tool that generates
        /// this one is the documented way to destroy it — `BuildAll` opens an
        /// empty scene and never re-adds the spawner, so six of its nine steps
        /// bail while it logs success anyway (CLAUDE.md). Anything that has to
        /// be dragged into SCN_TEST_BALANCE_CONTEST by hand is therefore one
        /// regeneration away from being silently absent, with a scene that still
        /// runs and simply shows less. These four need no serialized
        /// references — they discover fighters themselves and load their assets
        /// from <see cref="Systems_SpectatorKit"/> — so there is nothing to be
        /// gained by putting them in the scene and a whole failure mode to be
        /// avoided by not.
        ///
        /// Idempotent: adding a second copy would double every thud and every
        /// glow, and this runs once per contest start.
        /// </summary>
        private static void EnsureSpectatorSystems(GameObject systemsRoot)
        {
            if (systemsRoot == null)
            {
                // Worth a line rather than a silent return: with no systems root
                // the entire spectator layer is absent, and its absence looks
                // exactly like it working badly.
                Debug.LogWarning("Systems_ContestSpawner: no systems root — joint stress, impact FX " +
                                 "and the tale of the tape will not be attached.");
                return;
            }
            int added = 0;
            if (systemsRoot.GetComponentInChildren<Systems_ImpactFx>(true) == null)
            {
                AddSpectatorSystem<Systems_ImpactFx>(systemsRoot, "ImpactFx");
                added++;
            }
            if (systemsRoot.GetComponentInChildren<Systems_JointStressView>(true) == null)
            {
                AddSpectatorSystem<Systems_JointStressView>(systemsRoot, "JointStressView");
                added++;
            }
            // Contact shadows, footfalls and the strain tint. Same contract as
            // the four above: they discover fighters themselves, load what they
            // need from Systems_SpectatorKit, and hold no scene state — so they
            // are attached here rather than dragged into a hand-authored scene
            // that a regeneration could silently drop them from.
            if (systemsRoot.GetComponentInChildren<Systems_BlobShadow>(true) == null)
            {
                AddSpectatorSystem<Systems_BlobShadow>(systemsRoot, "BlobShadow");
                added++;
            }
            if (systemsRoot.GetComponentInChildren<Systems_Footsteps>(true) == null)
            {
                AddSpectatorSystem<Systems_Footsteps>(systemsRoot, "Footsteps");
                added++;
            }
            if (systemsRoot.GetComponentInChildren<Systems_FighterShading>(true) == null)
            {
                AddSpectatorSystem<Systems_FighterShading>(systemsRoot, "FighterShading");
                added++;
            }
            // Added AFTER the tale of the tape so its Start runs after the card
            // exists. Nothing depends on that ordering today — the commentary
            // borrows the referee's document, not the card's — but the two share
            // a band of screen and the one that builds last is the one drawn on
            // top, which is the behaviour wanted if they ever do overlap.
            if (systemsRoot.GetComponentInChildren<Systems_ColourCommentary>(true) == null)
            {
                AddSpectatorSystem<Systems_ColourCommentary>(systemsRoot, "ColourCommentary");
                added++;
            }
            if (added > 0)
            {
                // One line, once per contest. These systems are created rather
                // than placed, so this is the only way to tell from a log
                // whether they exist at all — which is the question that took a
                // play session to answer the first time.
                Debug.Log($"Systems_ContestSpawner: attached {added} spectator system(s) to " +
                          $"{systemsRoot.name}.");
            }
        }

        private static void AddSpectatorSystem<T>(GameObject systemsRoot, string objectName)
            where T : Component
        {
            var host = new GameObject(objectName);
            host.transform.SetParent(systemsRoot.transform, false);
            host.AddComponent<T>();
        }

        /// <summary>
        /// Instantiates one fighter under <paramref name="holder"/> — which the caller
        /// keeps inactive — configures it, then reparents it to the scene root. That
        /// last step is what activates it, so Awake and OnEnable run against the
        /// finished configuration rather than the prefab's.
        /// </summary>
        private static void Spawn(ContestRosterEntry entry, Transform holder,
            Vector3 position, Quaternion rotation, string instanceName, int copyIndex, int rosterIndex)
        {
            var instance = Instantiate(entry.prefab, holder);
            instance.name = instanceName;
            instance.transform.SetPositionAndRotation(position, rotation);
            Configure(instance, entry, copyIndex, rosterIndex);
            instance.transform.SetParent(null, worldPositionStays: true);
        }

        /// <summary>
        /// PUBLIC so an editor tool that places a roster entry at author time
        /// calls this exact code rather than a second copy of it. Safe to call
        /// from the Editor: components do not Awake until play begins.
        /// </summary>
        public static void Configure(GameObject instance, ContestRosterEntry entry, int copyIndex,
            int rosterIndex)
        {
            var rig = instance.GetComponent<Systems_FighterRig>();
            var stamina = instance.GetComponent<Systems_Stamina>();
            if (stamina != null) { stamina.enabled = false; }

            // A fighter that is NOT a PhysX rig -- a MuJoCo creature answers
            // IContestFighter instead -- has no rig to hang fall sensors on. The
            // ring referees it through that interface, so only the tint and the
            // identity below apply to it.
            if (rig != null)
            {
                // Fall sensors by role, not by joint index: shins are found by
                // name, and gloves exist only on rigs with arms.
                rig.Torso.gameObject.AddComponent<Sensor_GroundContact>();
                rig.Head.gameObject.AddComponent<Sensor_GroundContact>();
                for (int jointIndex = 0; jointIndex < rig.Joints.Count; jointIndex++)
                {
                    var jointBody = rig.Joints[jointIndex].body;
                    if (jointBody != null &&
                        jointBody.name.IndexOf("shin", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        jointBody.gameObject.AddComponent<Sensor_GroundContact>();
                    }
                }
                if (rig.GloveLeft != null)
                {
                    rig.GloveLeft.gameObject.AddComponent<Sensor_GroundContact>();
                }
                if (rig.GloveRight != null)
                {
                    rig.GloveRight.gameObject.AddComponent<Sensor_GroundContact>();
                }
            }

            if (entry.tint != null)
            {
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    renderers[rendererIndex].sharedMaterial = entry.tint;
                }
            }
            Color wash = TintCopy(instance, copyIndex);

            // Recorded here because this is the only code that knows all three
            // inputs: which roster entry the fighter came from, what tint that
            // entry carried, and which copy wash the duplicate got. Everything
            // downstream — scoreboard plates, the match tally, the drama
            // camera's winner focus — reads it back off the instance instead of
            // guessing from the GameObject name.
            instance.AddComponent<Systems_FighterIdentity>().Initialize(
                instance.name.Replace("Contest_", ""),
                IdentityColor(entry, rosterIndex) * wash,
                rosterIndex);
        }

        // Fallback swatches, used for roster entries with no tint material.
        // Grandma and Grandpa are the reason this exists: they wear their own
        // textures and are deliberately left untinted, so there is no material
        // to read a colour off and the scoreboard would otherwise show them
        // both as plain white.
        private static readonly Color[] RosterSwatches =
        {
            new(0.35f, 0.55f, 1f),   // 0
            new(1f, 0.45f, 0.72f),   // 1
            new(0.45f, 0.85f, 0.55f),// 2
            new(1f, 0.35f, 0.30f)    // 3
        };

        /// <summary>
        /// The colour that identifies this fighter on the scoreboard: its tint
        /// material's albedo where it has one, otherwise a swatch from the
        /// palette above.
        /// </summary>
        private static Color IdentityColor(ContestRosterEntry entry, int rosterIndex)
        {
            if (entry.tint != null && entry.tint.HasProperty(BaseColorId))
            {
                Color tint = entry.tint.GetColor(BaseColorId);
                tint.a = 1f;
                return tint;
            }
            return RosterSwatches[rosterIndex % RosterSwatches.Length];
        }

        // An eight-slot ring is filled from a four-entry roster, so it normally
        // holds two of each kind wearing exactly the same material — the
        // scoreboard called them Grandma and Grandma2 while the ring showed no
        // way to tell which was which. The first of each kind is left exactly as
        // authored and only the copies are washed, through a
        // MaterialPropertyBlock: URP's _BaseColor multiplies the albedo, so a
        // pale wash still reads over Grandma's and Grandpa's textures, and no
        // material is instantiated and no shader looked up at runtime — a
        // Shader.Find material here would strip out of the Android build.
        private static readonly Color[] CopyWashes =
        {
            new(0.62f, 0.78f, 1f),   // copy 2: cool
            new(1f, 0.80f, 0.55f),   // copy 3: warm
            new(0.70f, 1f, 0.72f)    // copy 4: green
        };

        /// <summary>Applies the copy wash and returns it, so the identity swatch can match the body.</summary>
        private static Color TintCopy(GameObject instance, int copyIndex)
        {
            if (copyIndex <= 0)
            {
                return Color.white;
            }
            Color wash = CopyWashes[(copyIndex - 1) % CopyWashes.Length];
            var block = new MaterialPropertyBlock();
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                Renderer renderer = renderers[rendererIndex];
                Material material = renderer.sharedMaterial;
                if (material == null)
                {
                    continue;
                }
                // Multiply rather than replace, so a material that already
                // carries a colour keeps it.
                Color baseColor = material.HasProperty(BaseColorId)
                    ? material.GetColor(BaseColorId)
                    : Color.white;
                renderer.GetPropertyBlock(block);
                block.SetColor(BaseColorId, baseColor * wash);
                renderer.SetPropertyBlock(block);
            }
            return wash;
        }

    }
}
