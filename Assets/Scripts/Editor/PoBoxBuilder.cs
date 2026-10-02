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
    /// Builds the project's two scenes from nothing. <c>Assets/Scenes/Arena.unity</c>: the ring, the hall,
    /// the lights (baked and live), every trained boxer dressed for each corner, eleven cameras, the walk-on
    /// Timeline, the effects, the decals, the sound, the HUD. <c>Assets/Scenes/Menu.unity</c>: the first
    /// screen, where the two boxers are chosen.
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
        public const string MenuScenePath = "Assets/Scenes/Menu.unity";
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
            // Last, so it is the scene left open: pressing Play in the editor then starts where the app does.
            BuildMenuScene();
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

            // Light cookies and the decal renderer feature, on both tiers.
            StageBakery.PreparePipeline();

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
            AssetBakery.Weave(canvasMat, "Assets/Textures/AmbientCG/Fabric030_Normal.jpg", 16f, 0.7f);
            Texture2D lampCookie = StageBakery.LampCookie(), sweepCookie = StageBakery.SweepCookie();
            Material logoDecal = StageBakery.Decal("Decal_Logo", StageBakery.LogoTexture());
            Material spotDecal = StageBakery.Decal("Decal_Spot", StageBakery.SpotTexture());
            Material scuffDecal = StageBakery.Decal("Decal_Scuff", StageBakery.ScuffTexture());
            Material hallSky = StageBakery.Skybox();
            UnityEngine.Audio.AudioMixer mixer = StageBakery.Mixer();
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
            crowdMat.SetColor("_RedCorner", Red);
            crowdMat.SetColor("_BlueCorner", Blue);
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
            PanelSettings panel = AssetBakery.Panel();
            PolicyProfile[] profiles = AssetBakery.Profiles();
            Mesh crowdMesh = AssetBakery.CrowdMesh(6.6f, 11, 0.95f, 0.42f, FloorY, 0.64f);

            AudioClip[] light = { AssetBakery.Punch("punch_light_1", 0.12f, 150f, 2500f, 0.7f, 11), AssetBakery.Punch("punch_light_2", 0.11f, 172f, 2900f, 0.75f, 12) };
            AudioClip[] heavy = { AssetBakery.Punch("punch_heavy_1", 0.28f, 70f, 1400f, 0.8f, 21), AssetBakery.Punch("punch_heavy_2", 0.26f, 84f, 1600f, 0.85f, 22) };
            AudioClip[] blocked = { AssetBakery.Punch("punch_block_1", 0.09f, 225f, 5200f, 1.1f, 31), AssetBakery.Punch("punch_block_2", 0.08f, 250f, 6000f, 1.15f, 32) };
            AssetDatabase.SaveAssets();

            // ---- environment
            // The hall's ambient light and what shiny things reflect come from a photograph of a real boxing
            // gym. It is never drawn: the camera clears to black, and the hall beyond the lamps is dark.
            RenderSettings.skybox = hallSky;
            RenderSettings.ambientMode = hallSky != null ? AmbientMode.Skybox : AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.075f, 0.085f, 0.12f);
            RenderSettings.ambientIntensity = 0.45f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 0.55f;
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
            var drawn = new List<RopeFlex.Rope>();
            foreach (float h in ropeHeights)
            {
                drawn.Add(Rope("Rope_S_" + h, ringRoot, new Vector3(0f, h, -RingHalf), false, physRope));
                drawn.Add(Rope("Rope_N_" + h, ringRoot, new Vector3(0f, h, RingHalf), false, physRope));
                drawn.Add(Rope("Rope_W_" + h, ringRoot, new Vector3(-RingHalf, h, 0f), true, physRope));
                drawn.Add(Rope("Rope_E_" + h, ringRoot, new Vector3(RingHalf, h, 0f), true, physRope));
            }
            // What is seen of the ropes: one mesh for all sixteen, which bows where a body is against it.
            var ropesGo = new GameObject("Ropes (drawn)");
            ropesGo.transform.SetParent(ringRoot, false);
            ropesGo.AddComponent<MeshFilter>();
            ropesGo.AddComponent<MeshRenderer>().sharedMaterial = ropeMat;
            var ropeFlex = ropesGo.AddComponent<RopeFlex>();
            ropeFlex.ropes = drawn.ToArray();

            // ---- decals: the promoter's mark in the middle, and a pool of marks for what the fight leaves
            Transform decals = Group("Decals", env);
            if (logoDecal != null) Projector("Canvas Logo", decals, logoDecal, new Vector3(0f, 0.03f, 0f), 2.7f, 0f, true);
            var marksGo = new GameObject("Canvas Marks");
            marksGo.transform.SetParent(decals, false);
            var canvasMarks = marksGo.AddComponent<CanvasMarks>();
            canvasMarks.ringCentre = Vector3.zero;
            canvasMarks.ringHalf = RingHalf;
            if (spotDecal != null && scuffDecal != null)
            {
                var spots = new List<DecalProjector>();
                var scuffs = new List<DecalProjector>();
                for (int i = 0; i < 14; i++) spots.Add(Projector("Wet Spot " + i, marksGo.transform, spotDecal, new Vector3(0f, 0.03f, 0f), 0.3f, 0f, false));
                for (int i = 0; i < 10; i++) scuffs.Add(Projector("Scuff " + i, marksGo.transform, scuffDecal, new Vector3(0f, 0.03f, 0f), 0.5f, 0f, false));
                canvasMarks.spots = spots.ToArray();
                canvasMarks.scuffs = scuffs.ToArray();
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
            // Mixed: its shadows and what it does to the fighters are live, what it bounces round the hall is baked.
            key.lightmapBakeType = LightmapBakeType.Mixed;

            // The house lights: dim, in the roof, never seen directly. Baked only; they are what stops the
            // stands and the floor beyond the ring being a black hole.
            Color[] house = { new Color(1f, 0.82f, 0.62f), new Color(0.62f, 0.76f, 1f) };
            for (int i = 0; i < 6; i++)
            {
                float a = i * Mathf.PI / 3f + 0.4f;
                var h = new GameObject("House Light " + i).AddComponent<Light>();
                h.transform.SetParent(lights, false);
                h.transform.position = new Vector3(Mathf.Cos(a) * 11f, 7.5f, Mathf.Sin(a) * 11f);
                h.type = LightType.Point;
                h.range = 22f;
                h.intensity = 46f;
                h.color = house[i % 2];
                h.shadows = LightShadows.Soft;
                h.lightmapBakeType = LightmapBakeType.Baked;
            }

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
                    spot.cookie = lampCookie;
                    spot.lightmapBakeType = LightmapBakeType.Mixed;
                    ringLights.Add(spot);
                }
            }

            // Two follow-spots, off until the walk-on swings them onto the corners or a winner is found.
            // Each hangs over the corner opposite the one it lights, so it lights its fighter from the front.
            var sweeps = new List<Light>();
            for (int i = 0; i < 2; i++)
            {
                float s = i == 0 ? 1f : -1f;
                var sweep = new GameObject(i == 0 ? "Sweep Red" : "Sweep Blue").AddComponent<Light>();
                sweep.transform.SetParent(lights, false);
                sweep.transform.position = new Vector3(s * rigHalf * 0.9f, rigHeight - 0.2f, s * rigHalf * 0.9f);
                sweep.transform.rotation = Quaternion.LookRotation(new Vector3(-s * 0.72f, 1.2f, -s * 0.72f) - sweep.transform.position);
                sweep.type = LightType.Spot;
                sweep.spotAngle = 17f;
                sweep.innerSpotAngle = 13f;
                sweep.range = 18f;
                sweep.intensity = 0f;
                sweep.color = new Color(1f, 0.97f, 0.92f);
                sweep.shadows = LightShadows.None;
                sweep.cookie = sweepCookie;
                sweep.lightmapBakeType = LightmapBakeType.Realtime;
                sweeps.Add(sweep);
            }

            // Light probes over the ring: what carries the baked bounce onto the fighters.
            var probes = new GameObject("Light Probes").AddComponent<LightProbeGroup>();
            probes.transform.SetParent(lights, false);
            var probeAt = new List<Vector3>();
            foreach (float py in new[] { 0.25f, 1.1f, 2.1f })
                for (int px = -2; px <= 2; px++)
                    for (int pz = -2; pz <= 2; pz++)
                        probeAt.Add(new Vector3(px * 1.6f, py, pz * 1.6f));
            probes.probePositions = probeAt.ToArray();

            // ---- look
            var lookGo = new GameObject("Look");
            lookGo.transform.SetParent(env, false);
            var volume = lookGo.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 0f;
            volume.sharedProfile = look;

            // ---- corners
            Transform marks = Group("Corners");
            Transform redCorner = Mark("Red Corner", marks, new Vector3(-0.72f, 0f, -0.72f), new Vector3(1f, 0f, 1f));
            Transform blueCorner = Mark("Blue Corner", marks, new Vector3(0.72f, 0f, 0.72f), new Vector3(-1f, 0f, -1f));
            Transform redNeutral = Mark("Red Neutral", marks, new Vector3(-2.1f, 0f, 2.1f), new Vector3(1f, 0f, -1f));
            Transform blueNeutral = Mark("Blue Neutral", marks, new Vector3(2.1f, 0f, -2.1f), new Vector3(-1f, 0f, 1f));
            // Where each stands for the walk-on: in its own corner, a step out from the post.
            Transform redStool = Mark("Red Stool", marks, new Vector3(-2.15f, 0f, -2.15f), new Vector3(1f, 0f, 1f));
            Transform blueStool = Mark("Blue Stool", marks, new Vector3(2.15f, 0f, 2.15f), new Vector3(-1f, 0f, -1f));

            // ---- fighters
            // Every trained entrant (Assets/Entrants, filled by PoBox/Import Trained Entrants), each built
            // twice, once dressed for each corner, and all switched off: the two that the menu picks are
            // switched on when the scene starts (MatchSetup). With no entrants at all, the scripted stand-ins.
            Transform cast = Group("Fighters");
            string[] entrants = EntrantFactory.Available();
            bool trained = entrants.Length >= 1;
            Fighter red, blue;
            var boxers = new List<MatchSetup.Boxer>();
            MatchSetup.Pairing[] rings = null;
            if (trained)
            {
                // Contact as the trainer has it: friction 1 on every shape, and nothing bounces. A sole that
                // grips a little less than the one the policy learned on is a fighter that slips when it pushes off.
                PhysicsMaterial asTrained = PhysicsAsset("AsTrained", 1f, 1f, 0f);
                asTrained.frictionCombine = PhysicsMaterialCombine.Maximum;
                asTrained.bounceCombine = PhysicsMaterialCombine.Minimum;
                redLook.body = redLook.sole = redLook.leather = asTrained;
                blueLook.body = blueLook.sole = blueLook.leather = asTrained;

                var roster = new List<PolicyProfile>();
                foreach (string entrant in entrants)
                {
                    PolicyProfile profile = EntrantFactory.Profile(entrant);
                    var boxer = new MatchSetup.Boxer
                    {
                        name = entrant,
                        red = EntrantFactory.Build(entrant, redCorner.position, redCorner.rotation, redLook, 0, cast),
                        blue = EntrantFactory.Build(entrant, blueCorner.position, blueCorner.rotation, blueLook, 1, cast),
                    };
                    foreach (Fighter f in new[] { boxer.red, boxer.blue })
                    {
                        f.profile = profile;
                        f.ringHalf = RingHalf;
                        var brain = f.GetComponent<Rl.PolicyBrain>();
                        brain.ringCentre = Vector3.zero;
                        brain.ringHalf = RingHalf;
                        brain.neutralSpot = (f == boxer.red ? redNeutral : blueNeutral).position;
                        f.gameObject.SetActive(false);
                    }
                    boxers.Add(boxer);
                    roster.Add(profile);
                }
                profiles = roster.ToArray();
                // Who fights when the arena is opened without the menu: the first against the second.
                red = boxers[0].red;
                blue = boxers[Mathf.Min(1, boxers.Count - 1)].blue;
                // The fight itself runs in MuJoCo; the bodies in the scene are its shadows.
                rings = EntrantFactory.AddRings(entrants, Vector3.zero, red.mjcf.controlDecimation, cast);
                Debug.Log($"[PoBox] {entrants.Length} boxer(s) in the arena, {rings.Length} pairing(s) with a MuJoCo match model.");
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
                red.opponent = blue; blue.opponent = red;
                red.profile = profiles[0]; blue.profile = profiles[1];
            }
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
            // Measures and limits the finished mix.
            mainGo.AddComponent<AudioMeter>();

            CinemachineCamera wide = ShotCam("Shot Wide", cams, 34f);
            CinemachineCamera orbit = ShotCam("Shot Orbit", cams, 34f);
            CinemachineCamera cornerCam = ShotCam("Shot High Corner", cams, 30f);
            CinemachineCamera low = ShotCam("Shot Low Ropes", cams, 68f);
            CinemachineCamera shoulderRed = ShotCam("Shot Shoulder Red", cams, 36f);
            CinemachineCamera shoulderBlue = ShotCam("Shot Shoulder Blue", cams, 36f);
            CinemachineCamera impact = ShotCam("Shot Impact", cams, 30f);
            CinemachineCamera overhead = ShotCam("Shot Overhead", cams, 44f);
            CinemachineCamera walkRed = ShotCam("Shot Walk-on Red", cams, 30f);
            CinemachineCamera walkBlue = ShotCam("Shot Walk-on Blue", cams, 30f);
            CinemachineCamera winnerCam = ShotCam("Shot Winner", cams, 32f);
            wide.transform.SetPositionAndRotation(new Vector3(0f, 3f, -7f), Quaternion.Euler(16f, 0f, 0f));
            wide.Priority = 30;

            // ---- effects
            Transform fx = Group("VFX");
            ParticleSystem sweat = Particles("Sweat", fx, sweatMat, 0.35f, 0.6f, 2.5f, 6.5f, 0.02f, 0.05f, 1.3f, 240, ParticleSystemShapeType.Cone, 32f, 0.03f, false);
            ParticleSystem shock = Particles("Shock Ring", fx, shockMat, 0.2f, 0.24f, 0f, 0f, 0.5f, 0.5f, 0f, 16, ParticleSystemShapeType.Sphere, 0f, 0.001f, true);
            ParticleSystem dust = Particles("Canvas Dust", fx, dustMat, 0.8f, 1.4f, 0.4f, 1.3f, 0.3f, 0.7f, -0.05f, 120, ParticleSystemShapeType.Hemisphere, 0f, 0.25f, true);
            ParticleSystem stars = Particles("Knockdown Stars", fx, starMat, 0.6f, 1.0f, 1.2f, 2.8f, 0.12f, 0.22f, 0.5f, 60, ParticleSystemShapeType.Sphere, 0f, 0.12f, false);
            // Sweat is drawn stretched along its flight, and stops at the canvas.
            var sweatRenderer = sweat.GetComponent<ParticleSystemRenderer>();
            sweatRenderer.renderMode = ParticleSystemRenderMode.Stretch;
            sweatRenderer.velocityScale = 0.035f;
            sweatRenderer.lengthScale = 1.6f;
            var sweatHits = sweat.collision;
            sweatHits.enabled = true;
            sweatHits.type = ParticleSystemCollisionType.Planes;
            sweatHits.mode = ParticleSystemCollisionMode.Collision3D;
            sweatHits.AddPlane(canvas.transform.parent);          // the ring's own origin: the canvas is y = 0
            sweatHits.bounce = 0f;
            sweatHits.dampen = 1f;
            sweatHits.lifetimeLoss = 1f;
            var impactVfx = fx.gameObject.AddComponent<ImpactVfx>();
            impactVfx.sweat = sweat; impactVfx.shock = shock; impactVfx.dust = dust; impactVfx.stars = stars;
            var mood = fx.gameObject.AddComponent<ArenaMood>();
            mood.ringLights = ringLights.ToArray();
            mood.lampRenderers = lamps.ToArray();
            mood.sweeps = sweeps.ToArray();

            // ---- rules, broadcast, sound, diagnostics
            var boutGo = new GameObject("Bout");
            var league = boutGo.AddComponent<LeagueTable>();
            league.roster = profiles;
            var bout = boutGo.AddComponent<Bout>();
            bout.red = red; bout.blue = blue;
            bout.redCorner = redCorner; bout.blueCorner = blueCorner;
            bout.redNeutral = redNeutral; bout.blueNeutral = blueNeutral;
            bout.redStool = redStool; bout.blueStool = blueStool;
            bout.league = league;
            if (trained)
            {
                // A trained policy belongs to its body, and was trained at 200 physics steps a second.
                bout.fixedEntrants = true;
                bout.physicsStep = red.mjcf.physicsStep;
                // With fighters that get up by themselves the count is a real one: ten seconds.
                if (EntrantFactory.AllCanGetUp(entrants)) bout.countInterval = 1f;
            }
            mood.bout = bout;
            ropeFlex.bout = bout;
            boutGo.AddComponent<PhysicsStepper>();
            var excitement = boutGo.AddComponent<Excitement>();
            excitement.bout = bout;

            var broadcastGo = new GameObject("Broadcast");
            var director = broadcastGo.AddComponent<BroadcastDirector>();
            director.bout = bout;
            director.mainCamera = camera;
            director.brain = brainComponent;
            director.redLive = redSkin; director.blueLive = blueSkin;
            director.ringCentre = Vector3.zero;
            director.ringHalf = RingHalf;
            director.wideCam = wide; director.orbitCam = orbit; director.cornerCam = cornerCam; director.lowCam = low;
            director.shoulderRedCam = shoulderRed; director.shoulderBlueCam = shoulderBlue;
            director.impactCam = impact; director.overheadCam = overhead;
            director.walkRedCam = walkRed; director.walkBlueCam = walkBlue; director.winnerCam = winnerCam;
            director.walkOn = StageBakery.WalkOn(bout.walkOnSeconds, broadcastGo, brainComponent, walkRed, walkBlue, lights.gameObject,
                                                 sweeps[0], sweeps[1], redStool.position, blueStool.position, mood, mood.sweepIntensity);
            if (trained)
            {
                var setup = boutGo.AddComponent<MatchSetup>();
                setup.boxers = boxers.ToArray();
                setup.rings = rings;
                setup.defaultRed = red.mjcf.fighterName;
                setup.defaultBlue = blue.mjcf.fighterName;
                setup.bout = bout;
                setup.director = director;
            }
            var commentary = broadcastGo.AddComponent<Commentary>();
            commentary.bout = bout;

            var audioGo = new GameObject("Audio");
            var audio = audioGo.AddComponent<AudioDirector>();
            audio.bout = bout;
            audio.listener = listener;
            // The layers of a punch are recordings (Kenney's impact pack, CC0), with the synthesised ones
            // behind them if the recordings are not in the project.
            const string kenney = "Assets/Audio/Kenney";
            AudioClip[] slap = StageBakery.Clips(kenney, "impactPunch_medium", 5), slapHeavy = StageBakery.Clips(kenney, "impactPunch_heavy", 5);
            AudioClip[] thud = StageBakery.Clips(kenney, "impactSoft_heavy", 5), dull = StageBakery.Clips(kenney, "impactSoft_medium", 5);
            audio.punchLight = slap.Length > 0 ? slap : light;
            audio.punchHeavy = slapHeavy.Length > 0 ? slapHeavy : heavy;
            audio.punchBlocked = dull.Length > 0 ? dull : blocked;
            audio.bodyThud = thud.Length > 0 ? thud : heavy;
            audio.footsteps = StageBakery.Clips(kenney, "footstep_carpet", 5);
            audio.crowdGasp = StageBakery.Gasp();
            audio.mixer = mixer;
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

            // Anything another editor script wants in the arena is added now, before the scene is saved and
            // lit: it is handed the Environment group to hang things under.
            BuildingArena?.Invoke(env);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            // The bake needs the scene on disk, and writes its lightmaps beside it.
            if (BakeOnBuild && StageBakery.BakeLighting()) EditorSceneManager.SaveScene(scene, ScenePath);
            SetBuildScenes(trained);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoBox] built {ScenePath} with {(trained ? "the trained boxers " + string.Join(", ", entrants) : "the scripted stand-ins")}; crowd of {crowdMesh.vertexCount / 4}.");
        }

        /// <summary>
        /// Raised while the arena is being built, after everything of the builder's own is in it and before
        /// it is saved and its lighting baked. The argument is the scene's Environment group. An editor
        /// script subscribes from an [InitializeOnLoad] static constructor to put something of its own in
        /// the arena without this file being changed.
        /// </summary>
        public static event Action<Transform> BuildingArena;

        /// <summary>
        /// Whether building the arena also bakes its lighting (about a minute on the CPU). Off for the
        /// headless probes, which build the scene to measure physics and never look at it. Kept per editor.
        /// </summary>
        public static bool BakeOnBuild
        {
            get => EditorPrefs.GetBool("pobox.bakeOnBuild", true);
            set => EditorPrefs.SetBool("pobox.bakeOnBuild", value);
        }

        [MenuItem("PoBox/Bake Arena Lighting", priority = 5)]
        public static void BakeNow()
        {
            if (EditorSceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (StageBakery.BakeLighting()) EditorSceneManager.SaveOpenScenes();
        }

        /// <summary>The menu is the scene the app starts in, when there are boxers to choose between.</summary>
        static void SetBuildScenes(bool withMenu)
        {
            EditorBuildSettings.scenes = withMenu
                ? new[] { new EditorBuildSettingsScene(MenuScenePath, true), new EditorBuildSettingsScene(ScenePath, true) }
                : new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        // ---------------------------------------------------------------- menu scene

        /// <summary>
        /// The first screen: a camera with nothing to look at and one interface document, with a card for
        /// every trained boxer in each corner's column. Not built when there are no trained boxers; the app
        /// then starts in the arena with the stand-ins.
        /// </summary>
        [MenuItem("PoBox/Build Menu Scene", priority = 3)]
        public static void BuildMenuScene()
        {
            string[] entrants = EntrantFactory.Available();
            if (entrants.Length == 0)
            {
                SetBuildScenes(false);
                Debug.Log("[PoBox] no trained boxers, so no menu scene: the app starts in the arena.");
                return;
            }

            AssetBakery.EnsureFolder("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            PanelSettings panel = AssetBakery.Panel();
            var cards = new List<MenuView.Boxer>();
            foreach (string entrant in entrants) cards.Add(EntrantFactory.Card(entrant));

            var cameraGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.008f, 0.010f, 0.016f);
            camera.cullingMask = 0;
            cameraGo.AddComponent<AudioListener>();

            // Switched off while it is put together: the menu draws itself in the editor too, and should
            // first do so with its boxers already in hand.
            var menuGo = new GameObject("Menu");
            menuGo.SetActive(false);
            var document = menuGo.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(AssetBakery.MenuUxml);
            if (document.visualTreeAsset == null) Debug.LogError("[PoBox] Menu.uxml did not import; the menu will be empty.");
            var view = menuGo.AddComponent<MenuView>();
            view.boxers = cards.ToArray();
            view.redColor = Red;
            view.blueColor = Blue;
            menuGo.SetActive(true);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, MenuScenePath);
            SetBuildScenes(true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoBox] built {MenuScenePath} with {cards.Count} boxer(s) to choose from.");
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
        /// One rope: a capsule to run into, fat enough that a body moving at punching speed cannot step
        /// through it between two physics steps, and nothing to look at; what is seen of the ropes is one
        /// mesh for all of them (<see cref="RopeFlex"/>), and this returns the line that mesh draws for this one.
        /// </summary>
        static RopeFlex.Rope Rope(string name, Transform parent, Vector3 position, bool alongZ, PhysicsMaterial physics)
        {
            float half = RingHalf + 0.12f;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = alongZ ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.Euler(0f, 0f, 90f);
            go.isStatic = true;
            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.radius = 0.04f;
            capsule.height = 2f * half;
            capsule.sharedMaterial = physics;

            Vector3 along = alongZ ? Vector3.forward : Vector3.right;
            // Away from the middle of the ring, flat.
            var outward = new Vector3(alongZ ? Mathf.Sign(position.x) : 0f, 0f, alongZ ? 0f : Mathf.Sign(position.z));
            return new RopeFlex.Rope { a = position - along * half, b = position + along * half, outward = outward };
        }

        /// <summary>
        /// A decal looking straight down at the canvas. Its box is 6 cm deep and sits on the cloth, so it
        /// marks the canvas and not the boots standing on it.
        /// </summary>
        static DecalProjector Projector(string name, Transform parent, Material material, Vector3 position, float size, float turn, bool on)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(90f, turn, 0f));
            var d = go.AddComponent<DecalProjector>();
            d.material = material;
            d.size = new Vector3(size, size, 0.06f);
            d.pivot = new Vector3(0f, 0f, 0.03f);
            d.fadeFactor = 1f;
            d.enabled = on;
            return d;
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
