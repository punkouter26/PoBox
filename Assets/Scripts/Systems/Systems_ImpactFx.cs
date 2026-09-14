using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Weight. Every contact in the ring used to read the same — a fighter
    /// brushing the canvas and a fighter arriving on it head first both got
    /// nothing at all, because the only impact feedback in the scene was
    /// <see cref="Systems_FallImpactFx"/>, which fires once per fall off a head
    /// HEIGHT threshold and knows nothing about how hard anyone landed. This
    /// scales a scuff burst, a thud and a camera shake by the impulse the
    /// solver actually applied, so a heavy landing looks and sounds like one.
    ///
    /// It reads impulse in newton-seconds rather than force — see
    /// <see cref="Sensor_Impact"/> for why that distinction is load-bearing in
    /// a project that runs two different fixed timesteps.
    ///
    /// Created at runtime by <see cref="Systems_ContestSpawner"/>, and it
    /// attaches its own sensors to the fighters it discovers, so nothing about
    /// it is serialized into a prefab or a training scene. Test-scene harness
    /// only.
    /// </summary>
    public sealed class Systems_ImpactFx : MonoBehaviour
    {
        /// <summary>
        /// Impulse below which nothing plays, in newton-seconds. A standing
        /// fighter's feet exchange a continuous trickle of small impulses with
        /// the canvas; without a floor this fires every step of every round.
        /// </summary>
        private const float MIN_IMPULSE = 1.6f;

        /// <summary>
        /// Impulse treated as "as hard as it gets" for scaling purposes. A
        /// 5 kg limb arriving at 4 m/s is about 20 N-s, which is a proper
        /// faceplant; everything above this is clamped rather than louder.
        /// </summary>
        private const float FULL_IMPULSE = 20f;

        /// <summary>
        /// Minimum gap between two impacts on the SAME fighter. A ragdoll
        /// collapsing generates a burst of contacts across several limbs within
        /// a few physics steps, and without this each fall plays as a machine-gun
        /// rattle of eight thuds instead of one landing.
        /// </summary>
        private const float PER_FIGHTER_COOLDOWN = 0.12f;

        /// <summary>Voices for the foley. Eight fighters landing at once is the worst case.</summary>
        private const int AUDIO_VOICES = 6;

        private const int MAX_PARTICLES = 240;
        private const float PARTICLE_LIFETIME = 0.5f;

        /// <summary>
        /// Impulse above which a contact also throws sweat. Deliberately well
        /// up the range: scuff dust says "something touched", sweat says "that
        /// hurt", and a cue that fires on every contact says neither. A 5 kg
        /// limb arriving at 4 m/s is about 20 N-s (FULL_IMPULSE), so this is
        /// roughly the top third of what the ring produces.
        /// </summary>
        private const float SWEAT_IMPULSE = 9f;

        private const int MAX_SWEAT = 120;

        private Systems_FighterRig[] _rigs;
        private Systems_DramaCamera _dramaCamera;
        private Systems_SpectatorKit _kit;
        private ParticleSystem _scuff;
        private ParticleSystem _sweat;
        private AudioSource[] _voices;
        private int _nextVoice;
        private float[] _cooldowns;
        private Transform[] _owners;

        private void Start()
        {
            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            if (_rigs.Length == 0)
            {
                return;
            }
            _kit = Systems_SpectatorKit.Load();
            _dramaCamera = FindFirstObjectByType<Systems_DramaCamera>();
            _cooldowns = new float[_rigs.Length];
            _owners = new Transform[_rigs.Length];

            _scuff = BuildScuffParticles();
            _sweat = BuildSweatParticles();
            _voices = BuildVoices();

            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                _owners[rigIndex] = _rigs[rigIndex].transform;
                AttachSensors(_rigs[rigIndex], rigIndex);
            }
        }

        private void Update()
        {
            if (_cooldowns == null)
            {
                return;
            }
            // Unscaled, so the cooldown keeps draining through the knockout
            // slow-mo rather than stretching with it.
            float dt = Time.unscaledDeltaTime;
            for (int index = 0; index < _cooldowns.Length; index++)
            {
                if (_cooldowns[index] > 0f)
                {
                    _cooldowns[index] -= dt;
                }
            }
        }

        /// <summary>
        /// One sensor per jointed body plus the pelvis and torso. The joint list
        /// is the rig's own account of which bodies exist and is the right
        /// source for it: the raptor's is a different shape than the humanoids'
        /// and gloves exist only on rigs with arms, so anything that named parts
        /// by hand would silently cover less of some fighters than of others.
        /// </summary>
        private void AttachSensors(Systems_FighterRig rig, int rigIndex)
        {
            AttachSensor(rig.Pelvis != null ? rig.Pelvis.gameObject : null, rig, rigIndex);
            AttachSensor(rig.Torso != null ? rig.Torso.gameObject : null, rig, rigIndex);
            var joints = rig.Joints;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                RigJointEntry entry = joints[jointIndex];
                if (entry != null && entry.body != null)
                {
                    AttachSensor(entry.body.gameObject, rig, rigIndex);
                }
            }
        }

        private void AttachSensor(GameObject target, Systems_FighterRig rig, int rigIndex)
        {
            if (target == null || target.GetComponent<Sensor_Impact>() != null)
            {
                return;
            }
            var sensor = target.AddComponent<Sensor_Impact>();
            sensor.Owner = rig.transform;
            // Captured index rather than a lookup: this runs on every contact of
            // every limb and a FindIndex per callback is exactly the sort of
            // thing that shows up as a physics-step cost on a phone.
            sensor.Impacted = (point, normal, impulse) => OnImpact(rigIndex, point, normal, impulse);
        }

        private void OnImpact(int rigIndex, Vector3 point, Vector3 normal, float impulse)
        {
            if (impulse < MIN_IMPULSE || _cooldowns == null)
            {
                return;
            }
            if (rigIndex >= 0 && rigIndex < _cooldowns.Length)
            {
                if (_cooldowns[rigIndex] > 0f)
                {
                    return;
                }
                _cooldowns[rigIndex] = PER_FIGHTER_COOLDOWN;
            }

            float strength = Mathf.Clamp01(
                (impulse - MIN_IMPULSE) / (FULL_IMPULSE - MIN_IMPULSE));

            EmitScuff(point, normal, strength);
            // Only the hard ones. The gate is on the RAW impulse rather than on
            // the normalised strength, so it means the same thing whatever
            // MIN_IMPULSE is later tuned to.
            if (impulse >= SWEAT_IMPULSE)
            {
                EmitSweat(point, strength);
            }
            PlayThud(point, strength);

            // The camera shake and the impact are the same event, so they come
            // from one impulse source rather than from two systems that each
            // decided how hard it was. Null in the walk contest, which has no
            // drama camera — the scuff and the thud still play.
            if (_dramaCamera != null)
            {
                _dramaCamera.ShakeAt(point, strength);
            }
        }

        private void EmitScuff(Vector3 point, Vector3 normal, float strength)
        {
            if (_scuff == null)
            {
                return;
            }
            var emitParams = new ParticleSystem.EmitParams
            {
                position = point,
                applyShapeToPosition = true,
                startSize = Mathf.Lerp(0.04f, 0.13f, strength),
                startLifetime = PARTICLE_LIFETIME * Mathf.Lerp(0.6f, 1f, strength)
            };
            // Sprayed back along the contact normal, so a landing throws
            // particles up off the canvas and a body checked into a corner post
            // throws them sideways.
            _scuff.transform.SetPositionAndRotation(point, Quaternion.LookRotation(normal));
            int count = Mathf.RoundToInt(Mathf.Lerp(3f, 22f, strength));
            _scuff.Emit(emitParams, count);
        }

        /// <summary>
        /// Sweat. Sprayed UP and outward rather than along the contact normal
        /// like the scuff: dust comes off the surface that was hit, sweat comes
        /// off the body that hit it, and throwing both the same way made one
        /// look like a brighter copy of the other.
        /// </summary>
        private void EmitSweat(Vector3 point, float strength)
        {
            if (_sweat == null)
            {
                return;
            }
            var emitParams = new ParticleSystem.EmitParams
            {
                position = point,
                applyShapeToPosition = true,
                startSize = Mathf.Lerp(0.018f, 0.035f, strength),
                startLifetime = Mathf.Lerp(0.35f, 0.7f, strength)
            };
            _sweat.transform.SetPositionAndRotation(point, Quaternion.LookRotation(Vector3.up));
            _sweat.Emit(emitParams, Mathf.RoundToInt(Mathf.Lerp(4f, 18f, strength)));
        }

        private void PlayThud(Vector3 point, float strength)
        {
            if (_voices == null || _kit == null || _kit.impactClips == null || _kit.impactClips.Length == 0)
            {
                return;
            }
            AudioSource voice = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % _voices.Length;
            AudioClip clip = _kit.impactClips[Random.Range(0, _kit.impactClips.Length)];
            if (clip == null)
            {
                return;
            }
            voice.transform.position = point;
            // A light contact is not just quieter, it is higher and shorter —
            // pitching the same clip up is most of what separates a scuff from
            // a landing to the ear.
            voice.pitch = Mathf.Lerp(1.25f, 0.82f, strength);
            voice.PlayOneShot(clip, Mathf.Lerp(0.18f, 1f, strength));
        }

        /// <summary>
        /// The scuff burst, built in code rather than referenced as a prefab so
        /// that nothing has to be dragged into a scene this system is not placed
        /// in. Emission is entirely manual: the system never plays itself, it is
        /// only ever asked to Emit at a contact point.
        /// </summary>
        private ParticleSystem BuildScuffParticles()
        {
            var host = new GameObject("ImpactScuff");
            host.transform.SetParent(transform, false);
            var particles = host.AddComponent<ParticleSystem>();

            // Stopped before anything else is touched: several modules refuse
            // edits while a system is playing, and AddComponent starts one.
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = particles.main;
            main.loop = false;
            main.playOnAwake = false;
            main.maxParticles = MAX_PARTICLES;
            main.startLifetime = PARTICLE_LIFETIME;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 2.4f);
            main.startSize = 0.08f;
            main.gravityModifier = 0.65f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            // Scaled time on purpose: a slow-motion knockout should carry its
            // debris with it. This is the one presentation system here that
            // WANTS the slow-mo, because the particles are part of the physical
            // event rather than part of the HUD.
            main.useUnscaledTime = false;

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.enabled = false; // manual Emit only

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 38f;
            shape.radius = 0.02f;

            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = particles.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0f));

            var renderer = host.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (_kit != null && _kit.overlayMaterial != null)
            {
                renderer.sharedMaterial = _kit.overlayMaterial;
            }
            return particles;
        }

        /// <summary>
        /// The sweat burst. Same manual-emission arrangement as the scuff, and
        /// the same reason for building it in code: nothing has to be dragged
        /// into a scene this system is not placed in.
        ///
        /// Heavier gravity and a narrower cone than the scuff, so the droplets
        /// arc rather than drift — dust hangs, water falls, and at this scale
        /// that difference is most of what separates the two to the eye.
        /// </summary>
        private ParticleSystem BuildSweatParticles()
        {
            var host = new GameObject("ImpactSweat");
            host.transform.SetParent(transform, false);
            var particles = host.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = particles.main;
            main.loop = false;
            main.playOnAwake = false;
            main.maxParticles = MAX_SWEAT;
            main.startLifetime = 0.55f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.4f, 3.6f);
            main.startSize = 0.025f;
            main.gravityModifier = 1.8f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.85f, 0.92f, 1f, 0.85f));
            // Scaled time, like the scuff: the droplets are part of the physical
            // event and should carry through a slow-motion knockout with it.
            main.useUnscaledTime = false;

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.enabled = false;   // manual Emit only

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 22f;
            shape.radius = 0.06f;

            var renderer = host.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (_kit != null && _kit.overlayMaterial != null)
            {
                renderer.sharedMaterial = _kit.overlayMaterial;
            }
            return particles;
        }

        private AudioSource[] BuildVoices()
        {
            var voices = new AudioSource[AUDIO_VOICES];
            for (int voiceIndex = 0; voiceIndex < AUDIO_VOICES; voiceIndex++)
            {
                var host = new GameObject("ImpactVoice" + voiceIndex);
                host.transform.SetParent(transform, false);
                var source = host.AddComponent<AudioSource>();
                source.playOnAwake = false;
                // Positioned, so a fighter going down on the far side of the
                // ring is quieter and off to that side.
                source.spatialBlend = 1f;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.minDistance = 2f;
                source.maxDistance = 22f;
                // On the foley bus, so the announcer ducks it rather than
                // talking over it. Null-safe: a scene with no mix gets the
                // source back unchanged.
                voices[voiceIndex] = Systems_AudioMix.Route(source, AudioBus.Foley);
            }
            return voices;
        }
    }
}
