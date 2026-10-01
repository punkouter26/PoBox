using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using Unity.Cinemachine;
using PoBox.Audio;
using PoBox.Broadcast;
using PoBox.Diag;
using PoBox.Fx;
using PoBox.League;
using PoBox.Sim;
using PoBox.UI;

namespace PoBox.EditorTools
{
    /// <summary>
    /// Builds the project's one scene, <c>Assets/Scenes/Arena.unity</c>, from nothing: the ring, the hall,
    /// the lights, two fighters and their replay puppets, nine cameras, the effects, the sound, the HUD.
    ///
    /// Everything it makes is a real object saved in the scene, so the ring posts, the lamps, the corners
    /// the fighters start in and the camera lenses can all be moved or re-tuned in the editor afterwards.
    /// Re-running it rebuilds the scene from this recipe; hand edits that should survive belong here.
    ///
    /// Headless: <c>Unity.exe -batchmode -projectPath . -executeMethod PoBox.EditorTools.PoBoxBuilder.BuildFromCommandLine</c>
    /// </summary>
    public static class PoBoxBuilder
    {
        public const string ScenePath = "Assets/Scenes/Arena.unity";
        const float RingHalf = 3.05f;       // rope line
        const float PostAt = 3.2f;
        const float FloorY = -1.2f;         // the hall floor; the canvas is y = 0

        static readonly Color Red = new Color(0.89f, 0.24f, 0.24f);
        static readonly Color Blue = new Color(0.20f, 0.49f, 0.94f);

        [MenuItem("PoBox/Build Everything", priority = 0)]
        public static void BuildAll()
        {
            ProjectSetup();
            BuildScene();
        }

        /// <summary>The -executeMethod entry point. Exits non-zero on any failure so a script can tell.</summary>
        public static void BuildFromCommandLine()
        {
            try
            {
                BuildAll();
                Debug.Log("[PoBox] BUILD OK");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[PoBox] BUILD FAILED: " + e);
                EditorApplication.Exit(1);
            }
        }

        // ---------------------------------------------------------------- project

        [MenuItem("PoBox/Apply Project Settings", priority = 1)]
        public static void ProjectSetup()
        {
            PlayerSettings.companyName = "Po";
            PlayerSettings.productName = "PoBox";
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultScreenWidth = 540;
            PlayerSettings.defaultScreenHeight = 960;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;

            // The editor must keep ticking when it is not the focused window, or anything driving it from
            // outside stalls. Interaction Mode: No Throttling.
            EditorPrefs.SetInt("InteractionMode", 1);
            EditorPrefs.SetInt("ApplicationIdleTime", 0);
            // Script Changes While Playing: Recompile After Finished Playing. Reloading scripts under a
            // running bout empties every static and drops both fighters' policies; they fall over and stay there.
            EditorPrefs.SetInt("ScriptCompilationDuringPlay", 1);

            var pc = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetBakery.SettingsDir + "/PC_RPAsset.asset");
            var mobile = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetBakery.SettingsDir + "/Mobile_RPAsset.asset");
            var global = AssetDatabase.LoadAssetAtPath<RenderPipelineGlobalSettings>(AssetBakery.SettingsDir + "/UniversalRenderPipelineGlobalSettings.asset");
            if (pc == null || mobile == null) throw new Exception("URP assets missing from Assets/Settings.");

            // A boxing ring is small: shadows only need to reach across the hall, not across a rooftop.
            pc.shadowDistance = 30f;
            mobile.shadowDistance = 20f;
            EditorUtility.SetDirty(pc);
            EditorUtility.SetDirty(mobile);

            if (GraphicsSettings.defaultRenderPipeline != pc) GraphicsSettings.defaultRenderPipeline = pc;
            if (global != null) EditorGraphicsSettings.SetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>(global);

            // Two quality levels, as copied from PoDecath: Mobile (0) and PC (1), each with its own pipeline asset.
            int current = QualitySettings.GetQualityLevel();
            string[] names = QualitySettings.names;
            bool switched = false;
            for (int i = 0; i < names.Length; i++)
            {
                // Only where it is not already so: switching the pipeline asset changes the scripting
                // defines, and that queues a recompile every time this runs.
                RenderPipelineAsset want = names[i] == "Mobile" ? mobile : pc;
                if (QualitySettings.GetRenderPipelineAssetAt(i) == want) continue;
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = want;
                switched = true;
            }
            if (switched) QualitySettings.SetQualityLevel(Mathf.Clamp(current, 0, names.Length - 1), false);

            Physics.defaultSolverIterations = 12;
            Physics.defaultSolverVelocityIterations = 4;

            AssetDatabase.SaveAssets();
            Debug.Log("[PoBox] project settings applied (Linear, portrait, URP, run in background).");
        }

        // ---------------------------------------------------------------- scene

        [MenuItem("PoBox/Build Arena Scene", priority = 2)]
        public static void BuildScene()
        {
            AssetBakery.EnsureFolder("Assets/Scenes");
            // The scene first, the assets after: opening a scene unloads unused assets, and a reference
            // held across that still serialises but compares equal to null.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- assets
            Texture2D canvasTex = AssetBakery.CanvasTexture();
            Texture2D dot = AssetBakery.DotTexture();
            Texture2D ring = AssetBakery.RingTexture();
            var smoke = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/Kenney/smoke_04.png");
            var star = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/Kenney/star_06.png");

            Material canvasMat = AssetBakery.Lit("Canvas", Color.white, 0.12f, 0f, canvasTex);
            Material apronMat = AssetBakery.Lit("Apron", new Color(0.06f, 0.07f, 0.11f), 0.35f);
            Material floorMat = AssetBakery.Lit("HallFloor", new Color(0.035f, 0.038f, 0.05f), 0.55f);
            Material postMat = AssetBakery.Lit("Post", new Color(0.55f, 0.57f, 0.62f), 0.75f, 0.9f);
            Material ropeMat = AssetBakery.Lit("Rope", new Color(0.86f, 0.86f, 0.84f), 0.3f);
            Material padRed = AssetBakery.Lit("Pad_Red", Red, 0.4f);
            Material padBlue = AssetBakery.Lit("Pad_Blue", Blue, 0.4f);
            Material padWhite = AssetBakery.Lit("Pad_White", new Color(0.9f, 0.9f, 0.9f), 0.4f);
            Material trussMat = AssetBakery.Lit("Truss", new Color(0.08f, 0.08f, 0.09f), 0.5f, 0.8f);
            Material lampMat = AssetBakery.Lit("Lamp", new Color(0.1f, 0.1f, 0.1f), 0.2f, 0f, null, new Color(1f, 0.96f, 0.88f) * 2.2f);
            Material crowdMat = AssetBakery.Crowd();
            Material overlay = AssetBakery.Overlay();

            Material sweatMat = AssetBakery.Particle("Fx_Sweat", dot, true, new Color(0.85f, 0.93f, 1f, 0.9f));
            Material shockMat = AssetBakery.Particle("Fx_Shock", ring, true, new Color(1f, 0.95f, 0.8f, 0.9f));
            Material dustMat = AssetBakery.Particle("Fx_Dust", smoke != null ? smoke : dot, false, new Color(0.72f, 0.76f, 0.84f, 0.35f));
            Material starMat = AssetBakery.Particle("Fx_Star", star != null ? star : dot, true, new Color(1f, 0.85f, 0.35f, 1f));
            Material trailMat = AssetBakery.Particle("Fx_Trail", dot, true, new Color(1f, 1f, 1f, 0.8f));

            var physCanvas = PhysicsAsset("Canvas", 0.9f, 0.95f, 0.05f);
            var physBody = PhysicsAsset("Body", 0.5f, 0.55f, 0.05f);
            var physSole = PhysicsAsset("Sole", 0.85f, 0.95f, 0f);
            var physLeather = PhysicsAsset("Leather", 0.4f, 0.45f, 0.12f);
            var physRope = PhysicsAsset("Rope", 0.5f, 0.5f, 0.35f);

            FighterFactory.Look redLook = Look(Red, new Color(0.78f, 0.60f, 0.48f), "Red", overlay, trailMat, physBody, physSole, physLeather);
            FighterFactory.Look blueLook = Look(Blue, new Color(0.56f, 0.40f, 0.30f), "Blue", overlay, trailMat, physBody, physSole, physLeather);

            VolumeProfile look = AssetBakery.Look();
            VolumeProfile replayLook = AssetBakery.ReplayLook();
            PanelSettings panel = AssetBakery.Panel();
            PolicyProfile[] profiles = AssetBakery.Profiles();
            Mesh crowdMesh = AssetBakery.CrowdMesh(6.6f, 11, 0.95f, 0.42f, FloorY, 0.64f);

            AudioClip[] light = { AssetBakery.Punch("punch_light_1", 0.12f, 150f, 2500f, 0.7f, 11), AssetBakery.Punch("punch_light_2", 0.11f, 172f, 2900f, 0.75f, 12) };
            AudioClip[] heavy = { AssetBakery.Punch("punch_heavy_1", 0.28f, 70f, 1400f, 0.8f, 21), AssetBakery.Punch("punch_heavy_2", 0.26f, 84f, 1600f, 0.85f, 22) };
            AudioClip[] blocked = { AssetBakery.Punch("punch_block_1", 0.09f, 225f, 5200f, 1.1f, 31), AssetBakery.Punch("punch_block_2", 0.08f, 250f, 6000f, 1.15f, 32) };
            AssetDatabase.SaveAssets();

            // ---- environment
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.075f, 0.085f, 0.12f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.02f;
            RenderSettings.fogColor = new Color(0.012f, 0.014f, 0.022f);

            Transform env = Group("Environment");
            Transform ringRoot = Group("Ring", env);

            GameObject canvas = Prim(PrimitiveType.Cube, "Canvas", ringRoot, new Vector3(0f, -0.15f, 0f), new Vector3(7.0f, 0.3f, 7.0f), canvasMat);
            canvas.GetComponent<Collider>().sharedMaterial = physCanvas;
            Prim(PrimitiveType.Cube, "Apron", ringRoot, new Vector3(0f, -0.72f, 0f), new Vector3(7.2f, 0.96f, 7.2f), apronMat);

            for (int ix = -1; ix <= 1; ix += 2)
            {
                for (int iz = -1; iz <= 1; iz += 2)
                {
                    string corner = (ix < 0 ? "W" : "E") + (iz < 0 ? "S" : "N");
                    Prim(PrimitiveType.Cylinder, "Post_" + corner, ringRoot, new Vector3(ix * PostAt, 0.575f, iz * PostAt), new Vector3(0.14f, 0.875f, 0.14f), postMat);
                    // Red corner is south-west, blue is north-east; the other two are neutral.
                    Material pad = ix < 0 && iz < 0 ? padRed : ix > 0 && iz > 0 ? padBlue : padWhite;
                    GameObject padGo = Prim(PrimitiveType.Cube, "Pad_" + corner, ringRoot, new Vector3(ix * (PostAt - 0.16f), 0.9f, iz * (PostAt - 0.16f)), new Vector3(0.22f, 1.05f, 0.22f), pad);
                    padGo.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
                }
            }

            float[] ropeHeights = { 0.45f, 0.75f, 1.05f, 1.35f };
            foreach (float h in ropeHeights)
            {
                Rope("Rope_S_" + h, ringRoot, new Vector3(0f, h, -RingHalf), false, ropeMat, physRope);
                Rope("Rope_N_" + h, ringRoot, new Vector3(0f, h, RingHalf), false, ropeMat, physRope);
                Rope("Rope_W_" + h, ringRoot, new Vector3(-RingHalf, h, 0f), true, ropeMat, physRope);
                Rope("Rope_E_" + h, ringRoot, new Vector3(RingHalf, h, 0f), true, ropeMat, physRope);
            }

            Prim(PrimitiveType.Cube, "HallFloor", env, new Vector3(0f, FloorY - 0.1f, 0f), new Vector3(60f, 0.2f, 60f), floorMat);

            var crowd = new GameObject("Crowd");
            crowd.transform.SetParent(env, false);
            crowd.AddComponent<MeshFilter>().sharedMesh = crowdMesh;
            var crowdRenderer = crowd.AddComponent<MeshRenderer>();
            crowdRenderer.sharedMaterial = crowdMat;
            crowdRenderer.shadowCastingMode = ShadowCastingMode.Off;
            crowdRenderer.receiveShadows = false;

            // ---- lights
            Transform lights = Group("Lighting", env);
            var key = new GameObject("Key Light").AddComponent<Light>();
            key.transform.SetParent(lights, false);
            key.type = LightType.Directional;
            key.transform.rotation = Quaternion.Euler(62f, 28f, 0f);
            key.color = new Color(1f, 0.96f, 0.9f);
            key.intensity = 1.25f;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.85f;

            const float rigHeight = 6.2f, rigHalf = 3.3f;
            Prim(PrimitiveType.Cube, "Truss_N", lights, new Vector3(0f, rigHeight + 0.2f, rigHalf), new Vector3(rigHalf * 2f + 0.3f, 0.18f, 0.18f), trussMat, false);
            Prim(PrimitiveType.Cube, "Truss_S", lights, new Vector3(0f, rigHeight + 0.2f, -rigHalf), new Vector3(rigHalf * 2f + 0.3f, 0.18f, 0.18f), trussMat, false);
            Prim(PrimitiveType.Cube, "Truss_E", lights, new Vector3(rigHalf, rigHeight + 0.2f, 0f), new Vector3(0.18f, 0.18f, rigHalf * 2f + 0.3f), trussMat, false);
            Prim(PrimitiveType.Cube, "Truss_W", lights, new Vector3(-rigHalf, rigHeight + 0.2f, 0f), new Vector3(0.18f, 0.18f, rigHalf * 2f + 0.3f), trussMat, false);

            var ringLights = new List<Light>();
            var lamps = new List<Renderer>();
            Color warm = new Color(1f, 0.9f, 0.76f), cool = new Color(0.78f, 0.88f, 1f);
            for (int ix = -1; ix <= 1; ix += 2)
            {
                for (int iz = -1; iz <= 1; iz += 2)
                {
                    string corner = (ix < 0 ? "W" : "E") + (iz < 0 ? "S" : "N");
                    var position = new Vector3(ix * rigHalf, rigHeight, iz * rigHalf);
                    GameObject lamp = Prim(PrimitiveType.Cube, "Lamp_" + corner, lights, position, new Vector3(0.55f, 0.22f, 0.55f), lampMat, false);
                    lamp.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                    lamps.Add(lamp.GetComponent<Renderer>());

                    var spot = new GameObject("Spot_" + corner).AddComponent<Light>();
                    spot.transform.SetParent(lights, false);
                    spot.transform.position = position - Vector3.up * 0.15f;
                    spot.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0.8f, 0f) - spot.transform.position);
                    spot.type = LightType.Spot;
                    spot.spotAngle = 64f;
                    spot.innerSpotAngle = 40f;
                    spot.range = 16f;
                    spot.intensity = 40f;
                    spot.color = ix * iz > 0 ? warm : cool;
                    spot.shadows = LightShadows.None;
                    ringLights.Add(spot);
                }
            }

            // ---- look
            var lookGo = new GameObject("Look");
            lookGo.transform.SetParent(env, false);
            var volume = lookGo.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 0f;
            volume.sharedProfile = look;

            var replayGo = new GameObject("Replay Look");
            replayGo.transform.SetParent(env, false);
            var replayVolume = replayGo.AddComponent<Volume>();
            replayVolume.isGlobal = true;
            replayVolume.priority = 10f;
            replayVolume.weight = 0f;
            replayVolume.sharedProfile = replayLook;

            // ---- corners
            Transform marks = Group("Corners");
            Transform redCorner = Mark("Red Corner", marks, new Vector3(-0.72f, 0f, -0.72f), new Vector3(1f, 0f, 1f));
            Transform blueCorner = Mark("Blue Corner", marks, new Vector3(0.72f, 0f, 0.72f), new Vector3(-1f, 0f, -1f));
            Transform redNeutral = Mark("Red Neutral", marks, new Vector3(-2.1f, 0f, 2.1f), new Vector3(1f, 0f, -1f));
            Transform blueNeutral = Mark("Blue Neutral", marks, new Vector3(2.1f, 0f, -2.1f), new Vector3(-1f, 0f, 1f));

            // ---- fighters
            // Trained entrants if there are two of them (Assets/Entrants, filled by PoBox/Import Trained
            // Entrants); otherwise the scripted stand-ins.
            Transform cast = Group("Fighters");
            string[] entrants = EntrantFactory.Available();
            // One entrant so far (the other is still on the bag): it fights a copy of itself.
            if (entrants.Length == 1) entrants = new[] { entrants[0], entrants[0] };
            bool trained = entrants.Length >= 2;
            Fighter red, blue;
            FighterSkin redPuppet, bluePuppet;
            if (trained)
            {
                Material xray = AssetBakery.StressXray();
                // Contact as the trainer has it: friction 1 on every shape, and nothing bounces. A sole that
                // grips a little less than the one the policy learned on is a fighter that slips when it pushes off.
                PhysicsMaterial asTrained = PhysicsAsset("AsTrained", 1f, 1f, 0f);
                asTrained.frictionCombine = PhysicsMaterialCombine.Maximum;
                asTrained.bounceCombine = PhysicsMaterialCombine.Minimum;
                redLook.body = redLook.sole = redLook.leather = asTrained;
                blueLook.body = blueLook.sole = blueLook.leather = asTrained;
                red = EntrantFactory.Build(entrants[0], redCorner.position, redCorner.rotation, redLook, xray, 0, cast, out redPuppet);
                blue = EntrantFactory.Build(entrants[1], blueCorner.position, blueCorner.rotation, blueLook, xray, 1, cast, out bluePuppet);
                profiles = new[] { EntrantFactory.Profile(entrants[0]), EntrantFactory.Profile(entrants[1]) };
                var redBrain = red.GetComponent<Rl.PolicyBrain>();
                var blueBrain = blue.GetComponent<Rl.PolicyBrain>();
                redBrain.opponent = blue.mjcf; blueBrain.opponent = red.mjcf;
                redBrain.ringCentre = blueBrain.ringCentre = Vector3.zero;
                redBrain.ringHalf = blueBrain.ringHalf = RingHalf;
                red.ringHalf = blue.ringHalf = RingHalf;
                // The fight itself runs in MuJoCo when the match model has been exported; these two are then its shadows.
                bool onMujoco = entrants[0] != entrants[1] && EntrantFactory.AddRing(red, blue, Vector3.zero, cast) != null;
                Debug.Log($"[PoBox] physics for the trained fighters: {(onMujoco ? "MuJoCo (mujoco.dll)" : "Unity")}");
                redBrain.neutralSpot = redNeutral.position; blueBrain.neutralSpot = blueNeutral.position;
            }
            else
            {
                red = FighterFactory.Build("Red", redCorner.position, redCorner.rotation, redLook, 0, cast);
                blue = FighterFactory.Build("Blue", blueCorner.position, blueCorner.rotation, blueLook, 1, cast);
                red.displayName = "RED"; blue.displayName = "BLUE";
                foreach (Fighter f in new[] { red, blue })
                {
                    var brain = f.GetComponent<ScriptedBoxer>();
                    brain.ringCentre = Vector3.zero;
                    brain.ringHalf = RingHalf;
                }
                redPuppet = FighterFactory.BuildPuppet("Red Replay Puppet", redLook, cast);
                bluePuppet = FighterFactory.BuildPuppet("Blue Replay Puppet", blueLook, cast);
                redPuppet.transform.position = redCorner.position;
                bluePuppet.transform.position = blueCorner.position;
            }
            red.opponent = blue; blue.opponent = red;
            red.profile = profiles[0]; blue.profile = profiles[1];
            FighterSkin redSkin = red.GetComponent<FighterSkin>(), blueSkin = blue.GetComponent<FighterSkin>();

            // ---- cameras
            Transform cams = Group("Cameras");
            var mainGo = new GameObject("Main Camera") { tag = "MainCamera" };
            mainGo.transform.SetParent(cams, false);
            mainGo.transform.SetPositionAndRotation(new Vector3(0f, 3f, -7f), Quaternion.Euler(16f, 0f, 0f));
            var camera = mainGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.008f, 0.010f, 0.016f);
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 120f;
            camera.fieldOfView = 36f;
            var listener = mainGo.AddComponent<AudioListener>();
            var brainComponent = mainGo.AddComponent<CinemachineBrain>();
            UniversalAdditionalCameraData camData = camera.GetUniversalAdditionalCameraData();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.FastApproximateAntialiasing;

            CinemachineCamera wide = ShotCam("Shot Wide", cams, 34f);
            CinemachineCamera orbit = ShotCam("Shot Orbit", cams, 34f);
            CinemachineCamera cornerCam = ShotCam("Shot High Corner", cams, 30f);
            CinemachineCamera low = ShotCam("Shot Low Ropes", cams, 68f);
            CinemachineCamera shoulderRed = ShotCam("Shot Shoulder Red", cams, 36f);
            CinemachineCamera shoulderBlue = ShotCam("Shot Shoulder Blue", cams, 36f);
            CinemachineCamera impact = ShotCam("Shot Impact", cams, 30f);
            CinemachineCamera overhead = ShotCam("Shot Overhead", cams, 44f);
            CinemachineCamera replay = ShotCam("Shot Replay", cams, 46f);
            wide.transform.SetPositionAndRotation(new Vector3(0f, 3f, -7f), Quaternion.Euler(16f, 0f, 0f));
            wide.Priority = 30;

            // ---- effects
            Transform fx = Group("VFX");
            ParticleSystem sweat = Particles("Sweat", fx, sweatMat, 0.35f, 0.6f, 2.5f, 6.5f, 0.02f, 0.05f, 1.3f, 240, ParticleSystemShapeType.Cone, 32f, 0.03f, false);
            ParticleSystem shock = Particles("Shock Ring", fx, shockMat, 0.2f, 0.24f, 0f, 0f, 0.5f, 0.5f, 0f, 16, ParticleSystemShapeType.Sphere, 0f, 0.001f, true);
            ParticleSystem dust = Particles("Canvas Dust", fx, dustMat, 0.8f, 1.4f, 0.4f, 1.3f, 0.3f, 0.7f, -0.05f, 120, ParticleSystemShapeType.Hemisphere, 0f, 0.25f, true);
            ParticleSystem stars = Particles("Knockdown Stars", fx, starMat, 0.6f, 1.0f, 1.2f, 2.8f, 0.12f, 0.22f, 0.5f, 60, ParticleSystemShapeType.Sphere, 0f, 0.12f, false);
            var impactVfx = fx.gameObject.AddComponent<ImpactVfx>();
            impactVfx.sweat = sweat; impactVfx.shock = shock; impactVfx.dust = dust; impactVfx.stars = stars;
            var mood = fx.gameObject.AddComponent<ArenaMood>();
            mood.ringLights = ringLights.ToArray();
            mood.lampRenderers = lamps.ToArray();

            // ---- rules, broadcast, sound, diagnostics
            var boutGo = new GameObject("Bout");
            var league = boutGo.AddComponent<LeagueTable>();
            league.roster = profiles;
            var bout = boutGo.AddComponent<Bout>();
            bout.red = red; bout.blue = blue;
            bout.redCorner = redCorner; bout.blueCorner = blueCorner;
            bout.redNeutral = redNeutral; bout.blueNeutral = blueNeutral;
            bout.league = league;
            if (trained)
            {
                // A trained policy belongs to its body, and was trained at 200 physics steps a second.
                bout.fixedEntrants = true;
                bout.physicsStep = red.mjcf.physicsStep;
                bout.fixedStepInSlowMotion = true;
            }
            boutGo.AddComponent<PhysicsStepper>();
            var excitement = boutGo.AddComponent<Excitement>();
            excitement.bout = bout;

            var broadcastGo = new GameObject("Broadcast");
            var replaySystem = broadcastGo.AddComponent<ReplaySystem>();
            replaySystem.bout = bout;
            replaySystem.redLive = redSkin; replaySystem.blueLive = blueSkin;
            replaySystem.redPuppet = redPuppet; replaySystem.bluePuppet = bluePuppet;
            var director = broadcastGo.AddComponent<BroadcastDirector>();
            director.bout = bout;
            director.mainCamera = camera;
            director.brain = brainComponent;
            director.redLive = redSkin; director.blueLive = blueSkin;
            director.redPuppet = redPuppet; director.bluePuppet = bluePuppet;
            director.replayVolume = replayVolume;
            director.ringCentre = Vector3.zero;
            director.ringHalf = RingHalf;
            director.wideCam = wide; director.orbitCam = orbit; director.cornerCam = cornerCam; director.lowCam = low;
            director.shoulderRedCam = shoulderRed; director.shoulderBlueCam = shoulderBlue;
            director.impactCam = impact; director.overheadCam = overhead; director.replayCam = replay;
            var commentary = broadcastGo.AddComponent<Commentary>();
            commentary.bout = bout;

            var audioGo = new GameObject("Audio");
            var audio = audioGo.AddComponent<AudioDirector>();
            audio.bout = bout;
            audio.listener = listener;
            audio.punchLight = light; audio.punchHeavy = heavy; audio.punchBlocked = blocked;
            audio.whoosh = AssetBakery.Real("whoosh");
            audio.bodyFall = AssetBakery.Real("sandThud");
            audio.bell = AssetBakery.Real("lapBell");
            audio.countBeep = AssetBakery.Real("countBeep");
            audio.sting = AssetBakery.Real("sting");
            audio.crowdBed = AssetBakery.Real("crowdBed");
            audio.crowdSwell = AssetBakery.Real("crowdSwell");
            audio.crowdGroan = AssetBakery.Real("crowdGroan");
            audio.crowdApplause = AssetBakery.Real("crowdApplause");

            var diag = new GameObject("Diagnostics");
            diag.AddComponent<PerfTelemetry>();

            // ---- HUD
            var hudGo = new GameObject("HUD");
            var document = hudGo.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(AssetBakery.HudUxml);
            document.sortingOrder = 10f;
            if (document.visualTreeAsset == null) Debug.LogError("[PoBox] Hud.uxml did not import; the HUD will be empty.");
            var hud = hudGo.AddComponent<HudView>();
            hud.bout = bout;
            hud.league = league;
            hud.director = director;
            hud.redColor = Red;
            hud.blueColor = Blue;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoBox] built {ScenePath} with {(trained ? "trained entrants " + red.displayName + " and " + blue.displayName : "the scripted stand-ins")}: {red.totalMass:0.0} and {blue.totalMass:0.0} kg, crowd of {crowdMesh.vertexCount / 4}.");
        }

        // ---------------------------------------------------------------- helpers

        static FighterFactory.Look Look(Color corner, Color skin, string tag, Material overlay, Material trail,
                                        PhysicsMaterial body, PhysicsMaterial sole, PhysicsMaterial leather)
        {
            return new FighterFactory.Look
            {
                skin = AssetBakery.Lit("Skin_" + tag, skin, 0.38f),
                trunks = AssetBakery.Lit("Trunks_" + tag, corner * 0.85f, 0.3f),
                glove = AssetBakery.Lit("Glove_" + tag, corner, 0.62f),
                boot = AssetBakery.Lit("Boot_" + tag, Color.Lerp(corner, Color.black, 0.75f), 0.4f),
                overlay = overlay,
                trail = trail,
                rim = corner,
                body = body, sole = sole, leather = leather,
            };
        }

        static PhysicsMaterial PhysicsAsset(string name, float dynamicFriction, float staticFriction, float bounce)
        {
            AssetBakery.EnsureFolder(AssetBakery.MaterialDir + "/Physics");
            string path = $"{AssetBakery.MaterialDir}/Physics/{name}.physicMaterial";
            var m = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (m == null)
            {
                m = new PhysicsMaterial(name);
                AssetDatabase.CreateAsset(m, path);
            }
            m.dynamicFriction = dynamicFriction;
            m.staticFriction = staticFriction;
            m.bounciness = bounce;
            EditorUtility.SetDirty(m);
            return m;
        }

        static Transform Group(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            return go.transform;
        }

        static Transform Mark(string name, Transform parent, Vector3 position, Vector3 facing)
        {
            Transform t = Group(name, parent);
            t.SetPositionAndRotation(position, Quaternion.LookRotation(facing.normalized, Vector3.up));
            return t;
        }

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 position, Vector3 scale, Material material, bool keepCollider = true)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            go.isStatic = true;
            if (!keepCollider) UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        /// <summary>
        /// One rope: a thin cylinder to look at, and a fatter capsule to run into, so that a body moving at
        /// punching speed cannot step through it between two physics steps.
        /// </summary>
        static void Rope(string name, Transform parent, Vector3 position, bool alongZ, Material material, PhysicsMaterial physics)
        {
            GameObject go = Prim(PrimitiveType.Cylinder, name, parent, position, new Vector3(0.045f, RingHalf + 0.12f, 0.045f), material, false);
            go.transform.localRotation = alongZ ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.Euler(0f, 0f, 90f);
            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.direction = 1;               // the cylinder's own long axis
            capsule.radius = 0.04f / 0.045f;     // 4 cm in the world, whatever the visual is scaled to
            capsule.height = 2f;
            capsule.sharedMaterial = physics;
        }

        static CinemachineCamera ShotCam(string name, Transform parent, float verticalFov)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var cm = go.AddComponent<CinemachineCamera>();
            cm.Lens.FieldOfView = verticalFov;
            cm.Lens.NearClipPlane = 0.1f;
            cm.Lens.FarClipPlane = 120f;
            cm.Priority = 10;

            var listener = go.AddComponent<CinemachineImpulseListener>();
            listener.ApplyAfter = CinemachineCore.Stage.Noise;
            listener.ChannelMask = 1;
            listener.Gain = 0.6f;
            listener.Use2DDistance = false;
            return cm;
        }

        static ParticleSystem Particles(string name, Transform parent, Material material,
                                        float lifeMin, float lifeMax, float speedMin, float speedMax, float sizeMin, float sizeMax,
                                        float gravity, int max, ParticleSystemShapeType shape, float angle, float radius, bool grow)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 1f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifeMin, lifeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speedMin, speedMax);
            main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
            main.gravityModifier = gravity;
            main.maxParticles = max;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var emission = ps.emission;
            emission.enabled = false;   // everything is emitted by hand, on a hit

            var sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = shape;
            sh.angle = angle;
            sh.radius = radius;

            var colour = ps.colorOverLifetime;
            colour.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0f, 1f) });
            colour.color = gradient;

            if (grow)
            {
                var size = ps.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.35f), new Keyframe(1f, 2.2f)));
            }

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return ps;
        }
    }
}
